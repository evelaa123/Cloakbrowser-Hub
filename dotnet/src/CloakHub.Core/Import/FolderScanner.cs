namespace CloakHub.Core.Import;

/// <summary>
/// Find importable browser profiles under a folder the user picked.
/// <para>
/// <see cref="BrowserDiscovery"/> only looks in the standard install locations,
/// which misses every case where the profile did not come from a browser installed
/// on this machine: a backup, a profile copied off another PC, a <c>.zip</c> from a
/// teammate, an external drive.
/// </para>
/// <para>
/// The hard part is not reading the folder, it is <i>not</i> assuming what the user
/// picked. All of these are things a person will reasonably drop here:
/// </para>
/// <code>
///   &lt;picked&gt;/                              ← the profile itself (has Preferences)
///   &lt;picked&gt;/Default/                      ← a Chromium user-data root
///   &lt;picked&gt;/User Data/Default/            ← a copy of the whole browser data dir
///   &lt;picked&gt;/backup/User Data/Profile 1/   ← an unpacked archive with a wrapper dir
///   &lt;picked&gt;/xxxxxxxx.default-release/     ← a Firefox profile
/// </code>
/// <para>
/// So this walks a bounded depth looking for profile <i>markers</i> rather than
/// expecting a fixed layout. Depth and breadth are capped because the folder could
/// be <c>C:\</c>, and an unbounded walk over a network drive would hang the app with
/// no way to cancel.
/// </para>
/// </summary>
public static class FolderScanner
{
    /// <summary>
    /// How deep to look below the picked folder.
    /// <para>
    /// Four covers the deepest realistic nesting
    /// (<c>archive/backup/User Data/Profile 1</c>) without turning a mis-click on a
    /// drive root into a filesystem-wide scan.
    /// </para>
    /// </summary>
    public const int MaxDepth = 4;

    /// <summary>Cap on directories visited, as a hard stop regardless of depth.</summary>
    public const int MaxDirectories = 4000;

    /// <summary>Cap on results, so a user-data root with 200 profiles cannot flood the UI.</summary>
    public const int MaxResults = 60;

    /// <summary>
    /// Directories that are never profiles and are expensive to walk.
    /// <para>
    /// Ordinal comparison, not case-insensitive: these are exact names Chromium
    /// writes, and matching case-insensitively would also skip a user's own folder
    /// that happened to be called "cache".
    /// </para>
    /// </summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.Ordinal)
    {
        "Cache",
        "Code Cache",
        "GPUCache",
        "ShaderCache",
        "GrShaderCache",
        "DawnCache",
        "DawnWebGPUCache",
        "GraphiteDawnCache",
        "Service Worker",
        "IndexedDB",
        "Local Storage",
        "Session Storage",
        "blob_storage",
        "component_crx_cache",
        "extensions_crx_cache",
        "CertificateRevocation",
        "SafetyTips",
        "OptimizationHints",
        "node_modules",
        ".git",
        "System Volume Information",
        "$RECYCLE.BIN",
    };

    /// <summary>
    /// Scan a folder for importable browser profiles.
    /// <para>
    /// Breadth-first, so shallow and therefore more likely matches are reported
    /// before deep ones, and so hitting a cap still returns the most plausible
    /// results rather than whatever a depth-first walk happened to reach.
    /// </para>
    /// </summary>
    public static FolderScan Scan(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return FolderScan.Empty("No folder was selected.");

        if (File.Exists(root))
            return FolderScan.Empty("That path is a file, not a folder.");

        if (!Directory.Exists(root))
            return FolderScan.Empty("That folder does not exist or is not readable.");

        var found = new List<DiscoveredProfile>();

        // Keyed on the resolved path so a folder reachable twice — through a
        // symlink, a junction, or a bind mount — is reported once. Without this a
        // user-data root that links its own Default folder yields duplicate rows
        // that both import to the same source.
        var seen = new HashSet<string>(PathComparer);

        var visited = 0;
        var truncated = false;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            if (visited++ >= MaxDirectories)
            {
                truncated = true;
                break;
            }

            var isChromium = ProfileMarkers.IsChromium(dir);
            var isFirefox = !isChromium && ProfileMarkers.IsFirefox(dir);

            if (isChromium || isFirefox)
            {
                if (seen.Add(RealPath(dir)))
                {
                    found.Add(Describe(dir, isFirefox));
                    if (found.Count >= MaxResults)
                    {
                        truncated = true;
                        break;
                    }
                }

                // Do not descend into a profile: its own subfolders are never
                // profiles, and walking Cache/IndexedDB is where a scan goes to
                // die — a warm profile has tens of thousands of cache files.
                continue;
            }

            if (depth >= MaxDepth) continue;

            foreach (var child in Children(dir))
                queue.Enqueue((child, depth + 1));
        }

        found.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        if (found.Count == 0)
        {
            return new FolderScan
            {
                Profiles = [],
                Truncated = truncated,
                Root = root,
                Note =
                    "No browser profiles found in that folder. A Chromium profile folder contains a " +
                    "\"Preferences\" file (Firefox: \"prefs.js\") — try picking the folder that holds it, " +
                    "or its \"User Data\" parent.",
            };
        }

        return new FolderScan { Profiles = found, Truncated = truncated, Root = root };
    }

    private static DiscoveredProfile Describe(string dir, bool isFirefox)
    {
        if (isFirefox)
        {
            return new DiscoveredProfile
            {
                Browser = "Firefox",
                Name = $"{ProfileMarkers.LastSegment(dir)} (Firefox)",
                Path = dir,
                Family = ProfileFamily.Firefox,
                HasCookies = ProfileMarkers.HasCookies(dir),
            };
        }

        var browser = ProfileMarkers.GuessBrowser(dir);
        return new DiscoveredProfile
        {
            Browser = browser,
            Name = $"{ProfileMarkers.ChromiumLabel(dir)} — {browser}",
            Path = dir,
            Family = ProfileFamily.Chromium,
            HasCookies = ProfileMarkers.HasCookies(dir),
            Locale = ProfileMarkers.ChromiumLocale(dir),
        };
    }

    /// <summary>
    /// Sub-directories worth queueing, with unreadable ones skipped silently.
    /// <para>
    /// Permission denied on one subfolder must not abort the whole scan: on
    /// Windows a picked drive root always contains at least one directory the
    /// process cannot open, so propagating that would make scanning a drive
    /// impossible rather than merely incomplete.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Children(string dir)
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(dir);
        }
        catch
        {
            yield break;
        }

        foreach (var child in entries)
        {
            var name = ProfileMarkers.LastSegment(child);
            if (SkipDirs.Contains(name)) continue;

            // Symlinked directories can form cycles that the depth cap alone would
            // only bound, not break — and a link out to "/" would put the whole
            // filesystem inside the budget. The realpath dedup covers links that
            // point at a genuine profile from elsewhere in the tree.
            bool isLink;
            try
            {
                isLink = (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                continue;
            }

            if (isLink) continue;
            yield return child;
        }
    }

    /// <summary>Resolved path for dedup, falling back to the absolute path.</summary>
    private static string RealPath(string p)
    {
        try
        {
            return Directory.ResolveLinkTarget(p, returnFinalTarget: true)?.FullName
                   ?? Path.GetFullPath(p);
        }
        catch
        {
            return Path.GetFullPath(p);
        }
    }

    /// <summary>
    /// Path comparison that matches the filesystem.
    /// <para>
    /// Windows and macOS are case-insensitive by default; Linux is not. Using one
    /// rule everywhere would either miss duplicates on Windows or merge two
    /// genuinely distinct profiles on Linux.
    /// </para>
    /// </summary>
    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}

/// <summary>
/// The outcome of a folder scan.
/// <para>
/// Distinct from a bare list because the interesting cases here are the
/// <i>unsuccessful</i> ones: the user picked a folder one level too high, or an
/// archive whose layout is unexpected. A silent empty list gives them nothing to
/// act on, so the scan reports why it found nothing and whether it gave up early.
/// </para>
/// </summary>
public sealed record FolderScan
{
    public IReadOnlyList<DiscoveredProfile> Profiles { get; init; } = [];

    /// <summary>True when a cap stopped the walk before it finished.</summary>
    public bool Truncated { get; init; }

    /// <summary>Explanation shown when the list is empty or partially complete.</summary>
    public string? Note { get; init; }

    /// <summary>The folder that was scanned.</summary>
    public string? Root { get; init; }

    /// <summary>Temp directory an archive was unpacked into, to be released after import.</summary>
    public string? ExtractedTo { get; init; }

    public static FolderScan Empty(string note) => new() { Note = note };
}
