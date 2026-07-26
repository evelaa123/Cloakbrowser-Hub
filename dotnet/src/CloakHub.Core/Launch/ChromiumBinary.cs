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

        // Pro builds are a separate directory and take precedence, matching the
        // wrapper: a user with a licensed build expects to be running it.
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
            .ThenByDescending(c => c.Version)
            .ToList();

        if (candidates.Count == 0)
        {
            return new BinaryResolution(null,
                $"No browser found in {cache}. Install one with: npx cloakbrowser install");
        }

        return new BinaryResolution(candidates[0].Exe, null);
    }

    /// <summary>
    /// Version from a <c>chromium-140.0.7339.207</c> directory name.
    /// <para>
    /// Compared as a version rather than a string so 140 sorts above 99 — a plain
    /// string comparison would pick the older build once the major hit three digits.
    /// </para>
    /// </summary>
    internal static Version ParseVersion(string directoryName)
    {
        var name = directoryName;

        if (name.StartsWith("chromium-", StringComparison.Ordinal))
            name = name["chromium-".Length..];
        if (name.EndsWith("-pro", StringComparison.Ordinal))
            name = name[..^"-pro".Length];

        return Version.TryParse(name, out var v) ? v : new Version(0, 0);
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
