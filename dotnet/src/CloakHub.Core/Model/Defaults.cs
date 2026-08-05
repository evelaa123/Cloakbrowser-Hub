using System.Security.Cryptography;

namespace CloakHub.Core.Model;

/// <summary>
/// The value pools a new or randomised fingerprint draws from, ported from
/// <c>src/shared/defaults.ts</c>.
/// <para>
/// These are not decoration. A fingerprint is only convincing when its parts
/// co-occur in the real world: a machine claiming macOS with an
/// <c>ANGLE (NVIDIA, ... D3D11)</c> renderer is describing a computer that cannot
/// exist, and that single contradiction is more identifying than the honest
/// values would have been. So the pools are keyed by platform and the picks are
/// drawn per platform, never mixed.
/// </para>
/// <para>
/// The distributions are deliberately lumpy rather than uniform. 1920x1080 appears
/// three times in the Windows screen pool and 8 GB three times in the memory pool
/// because those are genuinely modal in the population. Sampling uniformly from a
/// set of plausible values produces a population that is itself implausible — rare
/// resolutions would show up as often as common ones, and a profile holding a
/// 1-in-9 screen size is more distinguishable, not less.
/// </para>
/// </summary>
public static class Pools
{
    /// <summary>Screen resolutions, per target platform, weighted by real-world share.</summary>
    public static readonly IReadOnlyDictionary<FingerprintPlatform, (int Width, int Height)[]> Screens =
        new Dictionary<FingerprintPlatform, (int, int)[]>
        {
            [FingerprintPlatform.Windows] =
            [
                // Repeated on purpose — see the class remarks on weighting.
                (1920, 1080), (1920, 1080), (1920, 1080),
                (1536, 864), (1366, 768), (2560, 1440),
                (1440, 900), (1600, 900), (3840, 2160),
            ],
            [FingerprintPlatform.Macos] =
            [
                // Apple's own default logical resolutions; a Mac reporting 1366x768
                // is not a configuration Apple ships.
                (1440, 900), (1512, 982), (1728, 1117),
                (1680, 1050), (2560, 1440), (1920, 1080),
            ],
            [FingerprintPlatform.Linux] =
            [
                (1920, 1080), (1920, 1080), (1366, 768),
                (2560, 1440), (1600, 900),
            ],
        };

    /// <summary>
    /// GPU vendor and renderer pairs that actually ship together.
    /// <para>
    /// Stored as pairs rather than two independent lists because the vendor and the
    /// renderer are not independent facts. Picking them separately would eventually
    /// emit "Apple Inc." with a Radeon renderer — a combination no real machine
    /// reports, and a trivial tell for anything that checks.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<FingerprintPlatform, (string Vendor, string Renderer)[]> Gpus =
        new Dictionary<FingerprintPlatform, (string, string)[]>
        {
            [FingerprintPlatform.Windows] =
            [
                ("Google Inc. (NVIDIA)",
                    "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
                ("Google Inc. (NVIDIA)",
                    "ANGLE (NVIDIA, NVIDIA GeForce GTX 1650 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
                ("Google Inc. (Intel)",
                    "ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
                ("Google Inc. (Intel)",
                    "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)"),
                ("Google Inc. (AMD)",
                    "ANGLE (AMD, AMD Radeon RX 6600 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ],
            [FingerprintPlatform.Macos] =
            [
                ("Apple Inc.", "Apple M1"),
                ("Apple Inc.", "Apple M2"),
                ("Apple Inc.", "Apple M3"),
                ("Apple Inc.", "ANGLE (Apple, Apple M1 Pro, OpenGL 4.1)"),
                ("Intel Inc.", "Intel(R) Iris(TM) Plus Graphics 640"),
            ],
            [FingerprintPlatform.Linux] =
            [
                ("Google Inc. (NVIDIA Corporation)",
                    "ANGLE (NVIDIA Corporation, NVIDIA GeForce RTX 3060/PCIe/SSE2, OpenGL 4.5.0)"),
                ("Google Inc. (Intel)",
                    "ANGLE (Intel, Mesa Intel(R) UHD Graphics 620 (KBL GT2), OpenGL 4.6)"),
                ("Google Inc. (AMD)",
                    "ANGLE (AMD, AMD Radeon Graphics (radeonsi), OpenGL 4.6)"),
            ],
        };

    /// <summary>
    /// navigator.hardwareConcurrency candidates.
    /// <para>
    /// Only even counts, and none below 4. Chromium clamps the value it reports, and
    /// a browser claiming 3 cores describes hardware that essentially is not sold.
    /// </para>
    /// </summary>
    public static readonly int[] CpuCores = [4, 6, 8, 8, 8, 12, 16];

    /// <summary>
    /// navigator.deviceMemory candidates, in GB.
    /// <para>
    /// Powers of two only, because that is the entire set of values the API is
    /// specified to report — it buckets real RAM to the nearest power of two, so 12
    /// is not a value a real browser can return.
    /// </para>
    /// </summary>
    public static readonly int[] DeviceMemory = [4, 8, 8, 8, 16, 16, 32];

    /// <summary>Client Hints platform versions per target OS.</summary>
    public static readonly IReadOnlyDictionary<FingerprintPlatform, string[]> PlatformVersions =
        new Dictionary<FingerprintPlatform, string[]>
        {
            // Windows 10 reports 10.0.0; Windows 11 reports 13.0.0 and up in Client
            // Hints, which is why the numbers here do not look like marketing names.
            [FingerprintPlatform.Windows] = ["10.0.0", "15.0.0", "19.0.0"],
            [FingerprintPlatform.Macos] = ["14.5.0", "15.1.0", "15.3.0"],
            [FingerprintPlatform.Linux] = ["6.6.0", "6.8.0"],
        };

    /// <summary>
    /// Curated locale and timezone pairs for quick manual pinning.
    /// <para>
    /// Paired for the same reason as the GPU list: a browser reporting
    /// <c>de-DE</c> in <c>Asia/Tokyo</c> is a contradiction a site can test for in
    /// one line of JavaScript. Offering them together makes the coherent choice the
    /// easy one.
    /// </para>
    /// </summary>
    public static readonly (string Label, string Locale, string Timezone)[] Locales =
    [
        ("United States (New York)", "en-US", "America/New_York"),
        ("United States (Los Angeles)", "en-US", "America/Los_Angeles"),
        ("United States (Chicago)", "en-US", "America/Chicago"),
        ("United Kingdom (London)", "en-GB", "Europe/London"),
        ("Germany (Berlin)", "de-DE", "Europe/Berlin"),
        ("France (Paris)", "fr-FR", "Europe/Paris"),
        ("Netherlands (Amsterdam)", "nl-NL", "Europe/Amsterdam"),
        ("Spain (Madrid)", "es-ES", "Europe/Madrid"),
        ("Italy (Rome)", "it-IT", "Europe/Rome"),
        ("Poland (Warsaw)", "pl-PL", "Europe/Warsaw"),
        ("Turkiye (Istanbul)", "tr-TR", "Europe/Istanbul"),
        ("Brazil (Sao Paulo)", "pt-BR", "America/Sao_Paulo"),
        ("Canada (Toronto)", "en-CA", "America/Toronto"),
        ("Australia (Sydney)", "en-AU", "Australia/Sydney"),
        ("India (Kolkata)", "en-IN", "Asia/Kolkata"),
        ("Singapore", "en-SG", "Asia/Singapore"),
        ("Japan (Tokyo)", "ja-JP", "Asia/Tokyo"),
        ("UAE (Dubai)", "ar-AE", "Asia/Dubai"),
        ("Ukraine (Kyiv)", "uk-UA", "Europe/Kyiv"),
        ("Russia (Moscow)", "ru-RU", "Europe/Moscow"),
    ];

    /// <summary>Row accent colours offered in the editor.</summary>
    public static readonly string[] Colours =
    [
        "#6366f1", "#8b5cf6", "#ec4899", "#f43f5e", "#f97316",
        "#eab308", "#22c55e", "#14b8a6", "#0ea5e9", "#64748b",
    ];

    /// <summary>
    /// Seed range, matching the wrapper's own convention.
    /// <para>
    /// Five digits is not a security boundary — the seed is a device identity, not a
    /// secret — but it keeps the value short enough to read aloud when comparing two
    /// profiles by hand.
    /// </para>
    /// </summary>
    public const int SeedMin = 10_000;
    public const int SeedMax = 99_999;
}

/// <summary>
/// Builds new and randomised profile values.
/// <para>
/// Separate from <see cref="Pools"/> so the data can be inspected and tested
/// without invoking the randomness, and lives in Core rather than the UI so the
/// diagnostics CLI and any future automation endpoint create profiles by exactly
/// the same rules the UI does.
/// </para>
/// </summary>
public static class ProfileFactory
{
    /// <summary>
    /// A cryptographic RNG, not <c>System.Random</c>.
    /// <para>
    /// Not for secrecy: for independence. <c>Random</c> seeded from the clock returns
    /// correlated sequences when several instances are created in the same tick,
    /// which is precisely what "create 20 profiles" does. Profiles that share
    /// fingerprint values are linkable, so the one property this generator must have
    /// is that two profiles made a millisecond apart are unrelated.
    /// </para>
    /// </summary>
    private static int Next(int exclusiveMax) => RandomNumberGenerator.GetInt32(exclusiveMax);

    private static T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>A fresh master seed.</summary>
    public static int NewSeed() =>
        RandomNumberGenerator.GetInt32(Pools.SeedMin, Pools.SeedMax + 1);

    /// <summary>Pick an accent colour.</summary>
    public static string NewColour() => Pick(Pools.Colours);

    /// <summary>
    /// A complete, internally coherent fingerprint for one platform.
    /// <para>
    /// Every value is pinned to <see cref="ValueMode.Manual"/> rather than left on
    /// <see cref="ValueMode.Auto"/>. Auto is coherent too — the binary derives it
    /// from the seed — but it is invisible: the user cannot see what their profile
    /// claims, so they cannot tell whether it suits the site they are opening.
    /// Pinning makes the identity concrete and editable, which is the entire reason
    /// the "new fingerprint" button exists.
    /// </para>
    /// </summary>
    public static FingerprintConfig NewFingerprint(FingerprintPlatform platform)
    {
        var (width, height) = Pick(Pools.Screens[platform]);
        var (vendor, renderer) = Pick(Pools.Gpus[platform]);

        return new FingerprintConfig
        {
            Seed = NewSeed(),
            Platform = platform,
            PlatformVersion = Pick(Pools.PlatformVersions[platform]),
            Screen = new ScreenConfig { Mode = ValueMode.Manual, Width = width, Height = height },
            Gpu = new GpuConfig { Mode = ValueMode.Manual, Vendor = vendor, Renderer = renderer },
            CpuCores = new ValueOf<int> { Mode = ValueMode.Manual, Value = Pick(Pools.CpuCores) },
            DeviceMemory = new ValueOf<int> { Mode = ValueMode.Manual, Value = Pick(Pools.DeviceMemory) },
            // Left at the record's own defaults: noise on for every surface, and a
            // storage quota that does not read as an incognito window. Both are
            // documented on FingerprintConfig; restating them here would create a
            // second place for the default to drift.
        };
    }

    /// <summary>
    /// Re-roll a fingerprint while keeping the parts the user chose.
    /// <para>
    /// Preserves <see cref="FingerprintConfig.Brand"/>, the noise settings, the fonts
    /// directory and the WebRTC mode, because none of those are part of the hardware
    /// identity being replaced — a user who set Edge and turned canvas noise off
    /// asked for a different device, not a different browser and defence posture.
    /// </para>
    /// </summary>
    public static FingerprintConfig Reroll(FingerprintConfig existing, FingerprintPlatform platform)
    {
        var fresh = NewFingerprint(platform);

        return fresh with
        {
            Brand = existing.Brand,
            BrandVersion = existing.BrandVersion,
            Noise = existing.Noise,
            FontsDir = existing.FontsDir,
            WindowsFontMetrics = existing.WindowsFontMetrics,
            AllowThirdPartyCookies = existing.AllowThirdPartyCookies,
            WebRtc = existing.WebRtc,
            StorageQuotaMb = existing.StorageQuotaMb,
            TaskbarHeight = existing.TaskbarHeight,
        };
    }

    /// <summary>A new profile, ready to save.</summary>
    public static Profile NewProfile(string name, FingerprintPlatform platform, string? folderId = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            FolderId = folderId,
            Colour = NewColour(),
            Fingerprint = NewFingerprint(platform),
            Startup = new StartupConfig
            {
                // Port blocking on by default, matching what the migration backfills
                // for existing profiles. A new profile must not be less protected
                // than an upgraded one.
                BlockedPorts = [.. Launch.PrivacyArgs.DefaultBlockedPorts],
            },
            CreatedAt = now,
            UpdatedAt = now,
            SchemaVersion = Storage.ProfileMigration.CurrentVersion,
        };
    }
}
