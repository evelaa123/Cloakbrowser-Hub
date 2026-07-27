using System.Runtime.InteropServices;

namespace CloakHub.Core.Binaries;

/// <summary>Free builds and licensed builds are cached side by side, never in place of one another.</summary>
public enum BinaryTier
{
    Free,
    Pro,
}

/// <summary>
/// Names, paths and URLs for the stealth Chromium download.
/// <para>
/// Every value here is kept in parity with the CloakBrowser wrapper's own
/// <c>config.js</c>. That parity is the whole point: the Hub and the
/// <c>cloakbrowser</c> CLI share one cache directory, so if the two disagreed about
/// a directory name the user would silently get two 400 MB copies of the same
/// browser, and — worse — the Hub could launch a build the CLI does not know about.
/// </para>
/// </summary>
public static class BinaryCatalog
{
    /// <summary>Overrides the download origin, for an air-gapped mirror.</summary>
    public const string DownloadUrlVariable = "CLOAKBROWSER_DOWNLOAD_URL";

    public const string DefaultDownloadBase = "https://cloakbrowser.dev";

    /// <summary>
    /// GitHub Releases, used only as a fallback for the free tier.
    /// <para>
    /// Deliberately not consulted when <see cref="DownloadUrlVariable"/> is set: a
    /// user who pointed the app at their own mirror has usually done so because the
    /// machine cannot reach the public internet, and quietly reaching out to GitHub
    /// anyway would both fail slowly and violate what they asked for.
    /// </para>
    /// </summary>
    public const string GithubDownloadBase = "https://github.com/CloakHQ/cloakbrowser/releases/download";

    /// <summary>
    /// Chromium version shipped with this wrapper generation, per platform.
    /// <para>
    /// Per-platform rather than one number because builds land at different times:
    /// during a transition macOS can be a whole major behind, and pinning everyone
    /// to the newest tag would make the macOS download 404.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PlatformVersions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["linux-x64"] = "146.0.7680.177.5",
            ["linux-arm64"] = "146.0.7680.177.3",
            ["darwin-arm64"] = "145.0.7632.109.2",
            ["darwin-x64"] = "145.0.7632.109.2",
            ["windows-x64"] = "146.0.7680.177.5",
        };

    /// <summary>
    /// Ed25519 public keys that may sign a release manifest, base64 of the raw 32 bytes.
    /// <para>
    /// A list rather than a single key so a key can be rotated without stranding
    /// clients that only know the old one. Pinned in the binary — not fetched —
    /// because a key fetched from the same origin as the download certifies
    /// nothing.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> SigningPublicKeys =
        ["MKFKwIhUcKWq5xTuNA0Ovg99njcDEcEJvmWYYhApvaU="];

    /// <summary>
    /// The platform tag used in archive names and version markers.
    /// <para>
    /// Throws for an unsupported platform rather than guessing: a wrong tag
    /// produces a 404 several minutes into a download, which is a far worse way to
    /// learn that linux-riscv64 has no build than being told immediately.
    /// </para>
    /// </summary>
    public static string PlatformTag()
    {
        var arch = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsLinux())
        {
            return arch switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => throw Unsupported(arch),
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return arch switch
            {
                Architecture.Arm64 => "darwin-arm64",
                Architecture.X64 => "darwin-x64",
                _ => throw Unsupported(arch),
            };
        }

        if (OperatingSystem.IsWindows())
        {
            return arch switch
            {
                Architecture.X64 => "windows-x64",
                _ => throw Unsupported(arch),
            };
        }

        throw new PlatformNotSupportedException(
            $"CloakBrowser has no build for {RuntimeInformation.OSDescription}. " +
            $"Set {Launch.ChromiumBinary.PathVariable} to a local Chromium binary to use the Hub anyway.");
    }

    private static PlatformNotSupportedException Unsupported(Architecture arch) =>
        new($"CloakBrowser has no build for {RuntimeInformation.RuntimeIdentifier} ({arch}). " +
            $"Supported: {string.Join(", ", PlatformVersions.Keys.Order())}. " +
            $"Set {Launch.ChromiumBinary.PathVariable} to a local Chromium binary to use the Hub anyway.");

    /// <summary>The version this build expects for the current platform.</summary>
    public static string DefaultVersion() =>
        PlatformVersions.TryGetValue(PlatformTag(), out var v) ? v : "146.0.7680.177.5";

    /// <summary>Archives are zip on Windows and tar.gz everywhere else, matching the release layout.</summary>
    public static string ArchiveExtension() => OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";

    public static string ArchiveName(string? platformTag = null) =>
        $"cloakbrowser-{platformTag ?? PlatformTag()}{ArchiveExtension()}";

    /// <summary>
    /// The cache directory shared with the CLI, honouring <c>CLOAKBROWSER_CACHE_DIR</c>.
    /// </summary>
    public static string CacheDir(Func<string, string?>? env = null)
    {
        var read = env ?? Environment.GetEnvironmentVariable;
        var custom = read(Launch.ChromiumBinary.CacheVariable);
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cloakbrowser");
    }

    /// <summary>
    /// Where an unpacked build lives: <c>&lt;cache&gt;/chromium-&lt;version&gt;[-pro]</c>.
    /// <para>
    /// The <c>-pro</c> suffix keeps the tiers apart on disk. Overwriting one with
    /// the other would mean a licence check failing at launch silently downgraded
    /// the user's browser to the free build — with a different fingerprint surface,
    /// on profiles that sites already trust.
    /// </para>
    /// </summary>
    public static string BuildDir(string version, BinaryTier tier, Func<string, string?>? env = null) =>
        Path.Combine(CacheDir(env), $"chromium-{version}{(tier == BinaryTier.Pro ? "-pro" : "")}");

    /// <summary>The executable inside a build directory.</summary>
    public static string ExecutableIn(string buildDir) => Launch.ChromiumBinary.ExecutableIn(buildDir);

    /// <summary>
    /// Marker file recording the newest version seen for a tier and channel.
    /// <para>
    /// Platform-scoped in the name because the cache directory can be on a shared
    /// or synced drive, and a marker written by a Windows machine must not tell a
    /// Linux one that a build it cannot run is available.
    /// </para>
    /// </summary>
    public static string VersionMarker(BinaryTier tier, bool preview, Func<string, string?>? env = null)
    {
        var prefix = tier == BinaryTier.Pro
            ? (preview ? "latest_pro_version_preview" : "latest_pro_version")
            : "latest_version";

        return Path.Combine(CacheDir(env), $"{prefix}_{PlatformTag()}");
    }

    public static string DownloadBase(Func<string, string?>? env = null)
    {
        var read = env ?? Environment.GetEnvironmentVariable;
        var custom = read(DownloadUrlVariable);
        return string.IsNullOrWhiteSpace(custom) ? DefaultDownloadBase : custom.Trim().TrimEnd('/');
    }

    /// <summary>True when the user pointed the app at their own mirror.</summary>
    public static bool HasCustomOrigin(Func<string, string?>? env = null)
    {
        var read = env ?? Environment.GetEnvironmentVariable;
        return !string.IsNullOrWhiteSpace(read(DownloadUrlVariable));
    }

    /// <summary>Free-tier archive URL, primary origin first then the GitHub mirror.</summary>
    public static IReadOnlyList<string> FreeArchiveUrls(string version, Func<string, string?>? env = null)
    {
        var name = ArchiveName();
        var urls = new List<string> { $"{DownloadBase(env)}/chromium-v{version}/{name}" };
        if (!HasCustomOrigin(env)) urls.Add($"{GithubDownloadBase}/chromium-v{version}/{name}");
        return urls;
    }

    /// <summary>Manifest URLs for a version, in the same order as the archive URLs.</summary>
    public static IReadOnlyList<string> ManifestBases(string version, Func<string, string?>? env = null)
    {
        var bases = new List<string> { $"{DownloadBase(env)}/chromium-v{version}" };
        if (!HasCustomOrigin(env)) bases.Add($"{GithubDownloadBase}/chromium-v{version}");
        return bases;
    }

    /// <summary>
    /// Licensed archive URL. Authenticated with the key as a bearer token.
    /// <para>
    /// The explicit version is in the path rather than left to the server, so the
    /// archive that arrives is the one the signed manifest was fetched for. Asking
    /// for "latest" and verifying against a separately-resolved version is a race
    /// that a release cut mid-download would lose.
    /// </para>
    /// </summary>
    public static string ProArchiveUrl(string version, Func<string, string?>? env = null) =>
        $"{DownloadBase(env)}/api/download/{version}";

    /// <summary>Endpoint reporting the newest licensed version for a channel.</summary>
    public static string ProVersionUrl(bool preview, Func<string, string?>? env = null) =>
        $"{DownloadBase(env)}/api/download/version{(preview ? "?channel=preview" : "")}";

    /// <summary>Endpoint reporting the newest free version for a channel.</summary>
    public static string FreeVersionUrl(bool preview, Func<string, string?>? env = null) =>
        $"{DownloadBase(env)}/api/download/latest{(preview ? "?channel=preview" : "")}";
}
