namespace CloakHub.Core.Import;

/// <summary>
/// Find browser profiles in the standard install locations for this machine.
/// <para>
/// Reading a Chromium <c>Cookies</c> SQLite file directly and decrypting it would
/// need three OS-specific paths — DPAPI on Windows, the Keychain on macOS, a
/// libsecret-derived AES key on Linux — for a feature the user can get reliably by
/// copying the profile wholesale. So the import deliberately never decrypts:
/// </para>
/// <list type="number">
///   <item>Discover the installed browsers and their profile folders.</item>
///   <item>Copy the profile's <i>settings</i> into a Hub profile (locale).</item>
///   <item>Copy the session-bearing files when the user wants a true clone —
///         cookies included, because the stealth binary decrypts them the same way
///         the original browser did.</item>
///   <item>For cookies alone, point the user at an export file, which the cookie
///         engine already handles losslessly.</item>
/// </list>
/// <para>
/// Step 3 is the one that "keeps the session" without touching encryption at all:
/// the encrypted values travel with the profile and are decrypted by the browser
/// that owns the key.
/// </para>
/// </summary>
public static class BrowserDiscovery
{
    /// <summary>
    /// Folders inside a Chromium user-data dir that are never profiles.
    /// <para>
    /// A marker check alone is not enough here: several of these are cheap to test
    /// but expensive to size, and one of them — <c>System Profile</c> — genuinely
    /// has a <c>Preferences</c> file, so it would otherwise be offered to the user
    /// as an importable identity that contains nothing.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> NonProfileDirs = new(StringComparer.Ordinal)
    {
        "System Profile",
        "Crashpad",
        "GrShaderCache",
        "ShaderCache",
        "GraphiteDawnCache",
        "component_crx_cache",
        "extensions_crx_cache",
        "SwReporter",
        "Safe Browsing",
        "Subresource Filter",
        "WidevineCdm",
        "BrowserMetrics",
        "OptimizationGuidePredictionModels",
        "segmentation_platform",
        "Webstore Downloads",
        "CertificateRevocation",
        "FileTypePolicies",
        "OriginTrials",
        "PKIMetadata",
        "TpcdMetadata",
        "ZxcvbnData",
        "hyphen-data",
    };

    /// <summary>
    /// Scan the machine for browser profiles that can be imported.
    /// <para>
    /// <paramref name="roots"/> is injectable so the whole walk can be exercised
    /// against a temp tree; production passes null and gets the real locations.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DiscoveredProfile> Discover(IEnumerable<BrowserRoot>? roots = null)
    {
        var found = new List<DiscoveredProfile>();

        foreach (var root in roots ?? StandardRoots())
        {
            if (!Directory.Exists(root.Path)) continue;

            if (root.Family == ProfileFamily.Firefox)
            {
                found.AddRange(FirefoxProfiles(root));
                continue;
            }

            found.AddRange(ChromiumProfiles(root));
        }

        // Stable, predictable order in the picker: browser first so a user's
        // profiles group by the browser they came from, then by label.
        found.Sort(static (a, b) =>
        {
            var byBrowser = string.Compare(a.Browser, b.Browser, StringComparison.CurrentCultureIgnoreCase);
            return byBrowser != 0
                ? byBrowser
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return found;
    }

    private static IEnumerable<DiscoveredProfile> FirefoxProfiles(BrowserRoot root)
    {
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(root.Path);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            if (!ProfileMarkers.IsFirefox(dir)) continue;

            // Firefox names profiles "<8 random chars>.<name>". The random prefix
            // is noise to the user, so only the readable half is shown.
            var folder = ProfileMarkers.LastSegment(dir);
            var dot = folder.IndexOf('.');
            var label = dot >= 0 && dot < folder.Length - 1 ? folder[(dot + 1)..] : folder;

            yield return new DiscoveredProfile
            {
                Browser = root.Name,
                Name = $"{label} ({root.Name})",
                Path = dir,
                Family = ProfileFamily.Firefox,
                HasCookies = ProfileMarkers.HasCookies(dir),
                SizeMb = ApproximateSizeMb(dir),
            };
        }
    }

    private static IEnumerable<DiscoveredProfile> ChromiumProfiles(BrowserRoot root)
    {
        var candidates = new List<string>();

        // Opera Stable is itself the profile directory rather than a user-data root
        // containing one, so the root is a candidate in its own right. Checking it
        // unconditionally costs one File.Exists and covers every single-profile
        // layout without special-casing a vendor.
        if (ProfileMarkers.IsChromium(root.Path)) candidates.Add(root.Path);

        try
        {
            foreach (var dir in Directory.GetDirectories(root.Path))
            {
                if (NonProfileDirs.Contains(ProfileMarkers.LastSegment(dir))) continue;
                if (ProfileMarkers.IsChromium(dir)) candidates.Add(dir);
            }
        }
        catch
        {
            // Unreadable root: whatever was already found still stands.
        }

        foreach (var dir in candidates)
        {
            yield return new DiscoveredProfile
            {
                Browser = root.Name,
                Name = $"{ProfileMarkers.ChromiumLabel(dir)} — {root.Name}",
                Path = dir,
                Family = ProfileFamily.Chromium,
                HasCookies = ProfileMarkers.HasCookies(dir),
                SizeMb = ApproximateSizeMb(dir),
                Locale = ProfileMarkers.ChromiumLocale(dir),
            };
        }
    }

    /// <summary>
    /// Rough directory size in MB, capped so a huge profile cannot stall the scan.
    /// <para>
    /// The number exists to warn a user that a clone will take a while, so being
    /// approximate is fine and being slow is not. A warm Chrome profile holds
    /// hundreds of thousands of cache files; stat-ing all of them would take longer
    /// than the copy the number is meant to describe. After the budget the walk
    /// stops and reports what it measured, which is still the right order of
    /// magnitude because the big files are found early.
    /// </para>
    /// </summary>
    public static double? ApproximateSizeMb(string dir, int budgetFiles = 4000)
    {
        long bytes = 0;
        var seen = 0;

        var queue = new Queue<string>();
        queue.Enqueue(dir);

        while (queue.Count > 0 && seen < budgetFiles)
        {
            var current = queue.Dequeue();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(current).EnumerateFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (seen >= budgetFiles) break;

                try
                {
                    if (entry is DirectoryInfo sub)
                    {
                        // Following a link here could walk out of the profile and
                        // size an unrelated part of the disk.
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        queue.Enqueue(sub.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        seen++;
                        bytes += file.Length;
                    }
                }
                catch
                {
                    // A file deleted mid-walk — the browser rotating a cache
                    // shard — is normal, not a failure.
                }
            }
        }

        if (seen == 0) return null;
        return Math.Round(bytes / (1024.0 * 1024.0), 1);
    }

    /// <summary>
    /// The user-data roots to look in on this machine.
    /// <para>
    /// Every plausible location is listed rather than the "correct" one per
    /// browser: Chrome on Linux is in three different places depending on whether
    /// it came from the vendor .deb, a Snap or a Flatpak, and a user who installed
    /// it the unusual way is exactly the user whose profiles are hardest to find
    /// by hand.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BrowserRoot> StandardRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<BrowserRoot>();

        void Add(string name, ProfileFamily family, params string[] paths)
        {
            foreach (var p in paths)
                if (!string.IsNullOrWhiteSpace(p))
                    roots.Add(new BrowserRoot(name, p, family));
        }

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                        ?? Path.Combine(home, "AppData", "Local");
            var roaming = Environment.GetEnvironmentVariable("APPDATA")
                          ?? Path.Combine(home, "AppData", "Roaming");

            Add("Chrome", ProfileFamily.Chromium, Path.Combine(local, "Google", "Chrome", "User Data"));
            Add("Edge", ProfileFamily.Chromium, Path.Combine(local, "Microsoft", "Edge", "User Data"));
            Add("Brave", ProfileFamily.Chromium, Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
            Add("Chromium", ProfileFamily.Chromium, Path.Combine(local, "Chromium", "User Data"));
            Add("Opera", ProfileFamily.Chromium, Path.Combine(roaming, "Opera Software", "Opera Stable"));
            Add("Vivaldi", ProfileFamily.Chromium, Path.Combine(local, "Vivaldi", "User Data"));
            Add("Yandex", ProfileFamily.Chromium, Path.Combine(local, "Yandex", "YandexBrowser", "User Data"));
            Add("Firefox", ProfileFamily.Firefox, Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"));
            return roots;
        }

        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(home, "Library", "Application Support");

            Add("Chrome", ProfileFamily.Chromium, Path.Combine(support, "Google", "Chrome"));
            Add("Edge", ProfileFamily.Chromium, Path.Combine(support, "Microsoft Edge"));
            Add("Brave", ProfileFamily.Chromium, Path.Combine(support, "BraveSoftware", "Brave-Browser"));
            Add("Chromium", ProfileFamily.Chromium, Path.Combine(support, "Chromium"));
            Add("Opera", ProfileFamily.Chromium, Path.Combine(support, "com.operasoftware.Opera"));
            Add("Vivaldi", ProfileFamily.Chromium, Path.Combine(support, "Vivaldi"));
            Add("Yandex", ProfileFamily.Chromium, Path.Combine(support, "Yandex", "YandexBrowser"));
            Add("Firefox", ProfileFamily.Firefox, Path.Combine(support, "Firefox", "Profiles"));
            return roots;
        }

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(home, ".config");

        Add("Chrome", ProfileFamily.Chromium,
            Path.Combine(config, "google-chrome"),
            Path.Combine(home, ".var", "app", "com.google.Chrome", "config", "google-chrome"));
        Add("Edge", ProfileFamily.Chromium,
            Path.Combine(config, "microsoft-edge"),
            Path.Combine(home, ".var", "app", "com.microsoft.Edge", "config", "microsoft-edge"));
        Add("Brave", ProfileFamily.Chromium,
            Path.Combine(config, "BraveSoftware", "Brave-Browser"),
            Path.Combine(home, ".var", "app", "com.brave.Browser", "config", "BraveSoftware", "Brave-Browser"));
        Add("Chromium", ProfileFamily.Chromium,
            Path.Combine(config, "chromium"),
            Path.Combine(home, "snap", "chromium", "common", "chromium"));
        Add("Opera", ProfileFamily.Chromium, Path.Combine(config, "opera"));
        Add("Vivaldi", ProfileFamily.Chromium, Path.Combine(config, "vivaldi"));
        Add("Yandex", ProfileFamily.Chromium, Path.Combine(config, "yandex-browser"));
        Add("Firefox", ProfileFamily.Firefox,
            Path.Combine(home, ".mozilla", "firefox"),
            Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"),
            Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"));

        return roots;
    }
}

/// <summary>A user-data directory to look in, and the browser it belongs to.</summary>
public sealed record BrowserRoot(string Name, string Path, ProfileFamily Family);
