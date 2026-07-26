using System.Globalization;
using CloakHub.Core.Model;

namespace CloakHub.Core.Launch;

/// <summary>
/// Translates a Hub profile into CloakBrowser launch flags.
/// <para>
/// Pure functions only — no file, process or network access — so this is
/// unit-testable and the UI can call it to <i>preview</i> the exact argv a
/// profile will launch with. That preview is not a nicety: three of the bugs
/// fixed in the Electron version were diagnosed by reading it.
/// </para>
/// <para>Flag reference (CloakBrowser binary):</para>
/// <code>
///   --fingerprint=&lt;seed&gt;               master seed: canvas/WebGL/audio/fonts/rects
///   --fingerprint-platform=&lt;os&gt;         navigator.platform, UA OS, GPU pool
///   --fingerprint-platform-version=     Client Hints platform version
///   --fingerprint-brand / -brand-version
///   --fingerprint-screen-width/-height
///   --fingerprint-gpu-vendor/-renderer
///   --fingerprint-hardware-concurrency  navigator.hardwareConcurrency
///   --fingerprint-device-memory         navigator.deviceMemory (GB)
///   --fingerprint-storage-quota         MB — raise to look non-incognito
///   --fingerprint-timezone / -locale
///   --fingerprint-location=&lt;lat,lon&gt;
///   --fingerprint-noise=false           disable noise, keep deterministic seed
///   --fingerprint-windows-font-metrics  Chromium 148+
///   --fingerprint-allow-3p-cookies      Chromium 148+
///   --fingerprint-fonts-dir=&lt;path&gt;
///   --fingerprint-webrtc-ip=auto|&lt;ip&gt;
///   --fingerprint-taskbar-height=&lt;px&gt;
/// </code>
/// </summary>
public static class FingerprintArgs
{
    /// <summary>Flag prefixes the app owns — a user-supplied duplicate is ignored.</summary>
    private static readonly string[] OwnedPrefixes = ["--fingerprint", "--lang"];

    /// <summary>
    /// Stable fallback seed for a profile whose seed field was left empty.
    /// <para>
    /// FNV-1a over the profile id, mapped into the 10000-99999 range the wrapper
    /// itself uses. Deterministic on purpose: the same profile keeps the same
    /// device identity across launches and app restarts without persisting
    /// anything, and the flag preview stays stable between renders.
    /// </para>
    /// </summary>
    public static int SeedFromId(string id)
    {
        unchecked
        {
            uint h = 0x811c9dc5;
            foreach (var ch in id)
            {
                h ^= ch;
                h *= 0x01000193;
            }
            return 10000 + (int)(h % 90000);
        }
    }

    private static string PlatformFlag(FingerprintPlatform p) => p switch
    {
        FingerprintPlatform.Windows => "windows",
        FingerprintPlatform.Macos => "macos",
        FingerprintPlatform.Linux => "linux",
        _ => "windows",
    };

    /// <summary>
    /// Build the fingerprint-related Chromium flags for a profile.
    /// <para>
    /// Only values the user pinned (<c>Manual</c>) become explicit flags; auto
    /// values are left to the binary so they stay coherent with the seed.
    /// <c>ExtraArgs</c> are applied last and win on conflict, <i>except</i> for
    /// flags the app owns, where the profile wins — otherwise a stray user flag
    /// could silently break the identity the profile promises.
    /// </para>
    /// </summary>
    public static List<string> Build(Profile profile)
    {
        var fp = profile.Fingerprint;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        void Set(string flag, string? value = null) =>
            flags[flag] = value is null ? flag : $"{flag}={value}";

        void SetInt(string flag, int value) =>
            Set(flag, value.ToString(CultureInfo.InvariantCulture));

        // ------------------------------------------------------------------
        // A seed is ALWAYS emitted, never omitted.
        //
        // Previously an empty seed field meant "no --fingerprint flag", relying
        // on the wrapper's default args to supply a random one. The Hub launches
        // with StealthArgs=false (so it can drop the wrapper's hardcoded
        // --no-sandbox), which means those defaults no longer apply: omitting
        // the flag here would launch a browser with *no fingerprint spoofing at
        // all* while the UI still described the profile as protected.
        // ------------------------------------------------------------------
        var seed = fp.Seed is > 0 ? fp.Seed.Value : SeedFromId(profile.Id);
        SetInt("--fingerprint", seed);
        Set("--fingerprint-platform", PlatformFlag(fp.Platform));

        if (!string.IsNullOrWhiteSpace(fp.PlatformVersion))
            Set("--fingerprint-platform-version", fp.PlatformVersion);
        if (fp.Brand != BrowserBrand.Chrome)
            Set("--fingerprint-brand", fp.Brand.ToString());
        if (!string.IsNullOrWhiteSpace(fp.BrandVersion))
            Set("--fingerprint-brand-version", fp.BrandVersion);

        if (fp.Screen.Mode == ValueMode.Manual && fp.Screen.Width is > 0 && fp.Screen.Height is > 0)
        {
            SetInt("--fingerprint-screen-width", fp.Screen.Width.Value);
            SetInt("--fingerprint-screen-height", fp.Screen.Height.Value);
        }

        if (fp.Gpu.Mode == ValueMode.Manual)
        {
            if (!string.IsNullOrWhiteSpace(fp.Gpu.Vendor))
                Set("--fingerprint-gpu-vendor", fp.Gpu.Vendor);
            if (!string.IsNullOrWhiteSpace(fp.Gpu.Renderer))
                Set("--fingerprint-gpu-renderer", fp.Gpu.Renderer);
        }

        if (fp.CpuCores.Mode == ValueMode.Manual && fp.CpuCores.Value is > 0)
            SetInt("--fingerprint-hardware-concurrency", fp.CpuCores.Value.Value);
        if (fp.DeviceMemory.Mode == ValueMode.Manual && fp.DeviceMemory.Value is > 0)
            SetInt("--fingerprint-device-memory", fp.DeviceMemory.Value.Value);
        if (fp.StorageQuotaMb is > 0)
            SetInt("--fingerprint-storage-quota", fp.StorageQuotaMb.Value);
        if (fp.TaskbarHeight is >= 0)
            SetInt("--fingerprint-taskbar-height", fp.TaskbarHeight.Value);

        // Noise is ON in the binary by default; only the opt-out needs a flag.
        // The four per-surface settings collapse here because the binary has a
        // single switch — see NoiseConfig.Resolve.
        if (!fp.Noise.Resolve())
            Set("--fingerprint-noise", "false");

        if (fp.WindowsFontMetrics && fp.Platform == FingerprintPlatform.Windows)
            Set("--fingerprint-windows-font-metrics");
        if (!string.IsNullOrWhiteSpace(fp.FontsDir))
            Set("--fingerprint-fonts-dir", fp.FontsDir);
        if (fp.AllowThirdPartyCookies)
            Set("--fingerprint-allow-3p-cookies");

        // WebRTC: 'auto' is only meaningful behind a proxy — a spoofed ICE IP on
        // a direct connection is itself a mismatch, so it is skipped without one.
        if (fp.WebRtc.Mode == WebRtcMode.Manual && !string.IsNullOrWhiteSpace(fp.WebRtc.Ip))
            Set("--fingerprint-webrtc-ip", fp.WebRtc.Ip);
        else if (fp.WebRtc.Mode == WebRtcMode.Auto && profile.Proxy.IsConfigured)
            Set("--fingerprint-webrtc-ip", "auto");

        // Geolocation: explicit coordinates only. Ip mode is handled by geoip in
        // the wrapper; Off leaves the binary default untouched.
        if (profile.Geo.Mode == GeoMode.Manual && profile.Geo.Latitude is not null && profile.Geo.Longitude is not null)
        {
            var lat = profile.Geo.Latitude.Value.ToString(CultureInfo.InvariantCulture);
            var lon = profile.Geo.Longitude.Value.ToString(CultureInfo.InvariantCulture);
            Set("--fingerprint-location", $"{lat},{lon}");
        }

        // A pinned locale must also reach --lang so Accept-Language matches.
        if (profile.Locale.Mode == LocaleMode.Manual)
        {
            if (!string.IsNullOrWhiteSpace(profile.Locale.Locale))
            {
                Set("--lang", profile.Locale.Locale);
                Set("--fingerprint-locale", profile.Locale.Locale);
            }
            if (!string.IsNullOrWhiteSpace(profile.Locale.Timezone))
                Set("--fingerprint-timezone", profile.Locale.Timezone);
        }

        // User extra args last: they may add flags the app does not model, but
        // they may not hijack the identity flags above.
        foreach (var raw in profile.Startup.ExtraArgs)
        {
            var arg = raw.Trim();
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = arg.Split('=')[0];
            var owned = OwnedPrefixes.Any(p =>
                key == p || key.StartsWith(p + "-", StringComparison.Ordinal));
            if (owned && flags.ContainsKey(key)) continue;
            flags[key] = arg;
        }

        return [.. flags.Values];
    }
}
