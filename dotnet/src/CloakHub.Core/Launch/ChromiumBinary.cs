using System.Runtime.InteropServices;

namespace CloakHub.Core.Launch;

/// <summary>
/// Locates the stealth Chromium build on disk.
/// <para>
/// The layout mirrored here is the CloakBrowser wrapper's own cache, so a machine
/// that has ever run the Node wrapper — or the <c>cloakbrowser</c> CLI — already has
/// a binary the Hub can use, and the Hub does not maintain a second 200 MB copy of
/// the same download.
/// </para>
/// <para>
/// The resolution order matches the wrapper exactly, because any divergence would
/// mean the Hub silently launching a different build from the one the user's CLI
/// reports. That is the sort of mismatch that produces a fingerprint the user cannot
/// account for.
/// </para>
/// </summary>
public static class ChromiumBinary
{
    /// <summary>Explicit path override, honoured before anything else.</summary>
    public const string PathVariable = "CLOAKBROWSER_BINARY_PATH";

    /// <summary>Relocates the whole cache directory.</summary>
    public const string CacheVariable = "CLOAKBROWSER_CACHE_DIR";

    /// <summary>
    /// Root of the shared binary cache: <c>~/.cloakbrowser</c> unless relocated.
    /// </summary>
    public static string CacheDir(IDictionary<string, string>? env = null)
    {
        var custom = Read(CacheVariable, env);
        if (!string.IsNullOrWhiteSpace(custom)) return custom!;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cloakbrowser");
    }

    /// <summary>
    /// The executable inside an unpacked build directory.
    /// <para>
    /// macOS keeps it inside an <c>.app</c> bundle; Windows names it
    /// <c>chrome.exe</c>; Linux ships a bare <c>chrome</c>.
    /// </para>
    /// </summary>
    public static string ExecutableIn(string buildDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(buildDir, "Chromium.app", "Contents", "MacOS", "Chromium");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(buildDir, "chrome.exe");

        return Path.Combine(buildDir, "chrome");
    }

    /// <summary>
    /// Find a usable browser, or explain why there is not one.
    /// <para>
    /// Returns the newest cached build rather than the first found. A user who has
    /// updated has two directories side by side, and launching the older one would
    /// quietly undo the update.
    /// </para>
    /// </summary>
    public static BinaryResolution Resolve(IDictionary<string, string>? env = null)
    {
        // 1. An explicit path wins outright, including over a newer cached build:
        //    someone who set this is pinning a specific binary on purpose.
        var pinned = Read(PathVariable, env);
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            return File.Exists(pinned)
                ? new BinaryResolution(pinned, null)
                : new BinaryResolution(null,
                    $"{PathVariable} points at {pinned}, which does not exist.");
        }

        var cache = CacheDir(env);
        if (!Directory.Exists(cache))
        {
            return new BinaryResolution(null,
                $"No browser found. Expected a CloakBrowser cache at {cache}.");
        }

        // Tier outranks version, matching the wrapper. The two tiers are different
        // browser builds, not two points on one timeline: dropping from Pro to Free
        // to gain a version would swap out the patch set a user's profiles were
        // fingerprinted under, which is a bigger change than running a build a few
        // weeks old. Within a tier the newest always wins.
        var candidates = Directory.EnumerateDirectories(cache, "chromium-*")
            .Select(dir => new
            {
                Dir = dir,
                Exe = ExecutableIn(dir),
                Pro = dir.EndsWith("-pro", StringComparison.Ordinal),
                Version = ParseVersion(Path.GetFileName(dir)),
            })
            .Where(c => File.Exists(c.Exe))
            .OrderByDescending(c => c.Pro)
            .ThenByDescending(c => c.Version, VersionOrder.Instance)
            .ToList();

        if (candidates.Count == 0)
        {
            return new BinaryResolution(null,
                $"No browser found in {cache}. Install one with: npx cloakbrowser install");
        }

        return new BinaryResolution(candidates[0].Exe, null);
    }

    /// <summary>
    /// Ordering key from a build directory name such as
    /// <c>chromium-148.0.7778.215.2-pro</c>.
    /// <para>
    /// Parsed by hand rather than through <see cref="Version"/>, which accepts at
    /// most four components. CloakBrowser publishes a fifth — the patch revision of
    /// the stealth build itself — so <c>Version.TryParse</c> returned false for
    /// every real directory name and the old code fell back to <c>0.0</c> for all
    /// of them. With every candidate scoring identically the sort became a no-op
    /// and the resolver launched whatever the filesystem happened to enumerate
    /// first, which is how a machine holding both 148 and 150 kept starting 148.
    /// </para>
    /// <para>
    /// The failure was invisible from the UI: the update banner compares the
    /// version strings for inequality, so it correctly announced 150 while the
    /// launcher went on using 148 — the app appeared to know about an update it
    /// was silently refusing to use.
    /// </para>
    /// <para>
    /// Unparsable or missing components sort below anything numeric rather than
    /// throwing. A directory that does not follow the scheme is far more likely to
    /// be a leftover or a hand-made copy than the build the user wants launched.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<int> ParseVersion(string directoryName)
    {
        var name = directoryName;

        if (name.StartsWith("chromium-", StringComparison.Ordinal))
            name = name["chromium-".Length..];
        if (name.EndsWith("-pro", StringComparison.Ordinal))
            name = name[..^"-pro".Length];

        name = name.Trim();
        if (name.Length == 0) return [];

        var parts = name.Split('.');
        var numbers = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            // Stop at the first component that is not a plain number: a suffix
            // like "-rc1" or a stray word must not silently contribute a 0 that
            // would reorder otherwise-equal builds.
            if (!int.TryParse(part.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
                break;

            numbers.Add(n);
        }

        return numbers;
    }

    /// <summary>
    /// Compare two build versions component by component.
    /// <para>
    /// A shorter version that matches on every shared component sorts lower, so
    /// <c>148.0.7778.215</c> is older than <c>148.0.7778.215.2</c> — the fifth
    /// component is the stealth patch level, and a build carrying one is a
    /// revision of the build without it.
    /// </para>
    /// </summary>
    internal static int CompareVersions(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var length = Math.Max(a.Count, b.Count);

        for (var i = 0; i < length; i++)
        {
            // Missing components count as 0 rather than as "unknown": that keeps
            // the comparison a total order, which OrderBy requires to be stable.
            var left = i < a.Count ? a[i] : 0;
            var right = i < b.Count ? b[i] : 0;

            if (left != right) return left.CompareTo(right);
        }

        return 0;
    }

    /// <summary>Orders build directories newest-first, for use as a comparer.</summary>
    internal sealed class VersionOrder : IComparer<IReadOnlyList<int>>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(IReadOnlyList<int>? x, IReadOnlyList<int>? y) =>
            CompareVersions(x ?? [], y ?? []);
    }

    private static string? Read(string name, IDictionary<string, string>? env) =>
        env is null
            ? Environment.GetEnvironmentVariable(name)
            : env.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Either a path or the reason there is not one.
/// <para>
/// A result type rather than an exception because "no browser installed yet" is an
/// ordinary first-run state that the UI should explain, not a fault.
/// </para>
/// </summary>
public sealed record BinaryResolution(string? Path, string? Error)
{
    public bool Found => Path is not null;
}
