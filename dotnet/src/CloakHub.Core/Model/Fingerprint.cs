namespace CloakHub.Core.Model;

/// <summary>Target OS the profile presents to websites.</summary>
public enum FingerprintPlatform { Windows, Macos, Linux }

/// <summary>Browser brand reported in UA + Client Hints.</summary>
public enum BrowserBrand { Chrome, Edge, Opera, Vivaldi }

/// <summary>
/// Three-state switch for a spoofable value, mirroring the vocabulary
/// anti-detect users already know from other tools:
/// <list type="bullet">
///   <item><c>Real</c> — pass the host's genuine value through.</item>
///   <item><c>Auto</c> — let the fingerprint seed derive it (the binary keeps
///   all seed-derived values mutually coherent, so this is the safe default).</item>
///   <item><c>Manual</c> — a value the user pinned explicitly.</item>
/// </list>
/// <para>
/// The TypeScript original had only <c>auto | manual</c>. <c>Real</c> is new and
/// deliberate: it is the only way to express "do not emit a flag for this at
/// all", which some parameters genuinely want (a real GPU string on a Windows
/// host is less suspicious than a pooled one).
/// </para>
/// </summary>
public enum ValueMode { Real, Auto, Manual }

/// <summary>
/// Noise handling for one fingerprint surface.
/// <para>
/// <b>Important limitation, stated here because the UI must not imply
/// otherwise:</b> the CloakBrowser binary exposes exactly one noise switch
/// (<c>--fingerprint-noise=false</c>) covering canvas, WebGL, audio and client
/// rects together. Per-surface values are therefore recorded but currently
/// collapse to that single flag — see <c>NoiseConfig.Resolve</c>. Storing them
/// separately now means the UI can already offer the per-surface control that
/// users expect, and a future binary with finer flags needs no data migration.
/// </para>
/// </summary>
public enum NoiseMode { Off, Real, Noise }

public sealed record ScreenConfig
{
    public ValueMode Mode { get; init; } = ValueMode.Auto;
    public int? Width { get; init; }
    public int? Height { get; init; }
}

public sealed record GpuConfig
{
    public ValueMode Mode { get; init; } = ValueMode.Auto;
    public string? Vendor { get; init; }
    public string? Renderer { get; init; }
}

public sealed record ValueOf<T> where T : struct
{
    public ValueMode Mode { get; init; } = ValueMode.Auto;
    public T? Value { get; init; }
}

/// <summary>off = leave untouched, Ip = follow proxy exit IP, Manual = pinned coords.</summary>
public enum GeoMode { Off, Ip, Manual }

public sealed record GeoConfig
{
    public GeoMode Mode { get; init; } = GeoMode.Off;
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Accuracy { get; init; }
}

public enum WebRtcMode { Off, Real, Auto, Manual }

public sealed record WebRtcConfig
{
    public WebRtcMode Mode { get; init; } = WebRtcMode.Auto;
    public string? Ip { get; init; }
}

/// <summary>
/// Per-surface noise settings. See <see cref="NoiseMode"/> for why these
/// currently collapse into one flag.
/// </summary>
public sealed record NoiseConfig
{
    // ------------------------------------------------------------------
    // Default: noise ON for every surface.
    //
    // These defaulted to Real in the first version of this port, which was a
    // silent regression caught by running the diagnostics report and reading the
    // flags it printed. The TypeScript original defaults `noise: true`, so a new
    // profile there gets canvas/WebGL/audio randomisation. Defaulting to Real
    // here collapsed to `--fingerprint-noise=false` and shipped a brand-new
    // profile with the central anti-detect defence switched off, while the UI
    // still presented it as protected.
    //
    // Noise on is also the right default independently of the port: a profile
    // that returns identical canvas and WebGL readings is trivially linkable
    // across sites, and that is the exact correlation this tool exists to break.
    // Turning it off is a deliberate choice for a site that fingerprints
    // aggressively enough to notice the noise itself — not something a user
    // should get by never opening the settings.
    // ------------------------------------------------------------------
    public NoiseMode Canvas { get; init; } = NoiseMode.Noise;
    public NoiseMode WebGl { get; init; } = NoiseMode.Noise;
    public NoiseMode Audio { get; init; } = NoiseMode.Noise;
    public NoiseMode ClientRects { get; init; } = NoiseMode.Noise;

    /// <summary>
    /// Collapse the four surfaces into the single value the binary accepts.
    /// <para>
    /// Returns <c>true</c> when noise injection should stay enabled. Any single
    /// surface asking for noise keeps it on for all of them, because the binary
    /// cannot separate them — erring toward "noise on" rather than silently
    /// disabling a surface the user explicitly asked to randomise.
    /// </para>
    /// </summary>
    public bool Resolve() =>
        Canvas == NoiseMode.Noise || WebGl == NoiseMode.Noise ||
        Audio == NoiseMode.Noise || ClientRects == NoiseMode.Noise;
}

public sealed record FingerprintConfig
{
    /// <summary>
    /// Master seed (10000-99999 by convention). A fixed seed is a stable device
    /// identity across launches, which is what makes a profile look like a
    /// returning visitor rather than a new machine each time.
    /// </summary>
    public int? Seed { get; init; }

    public FingerprintPlatform Platform { get; init; } = FingerprintPlatform.Windows;

    /// <summary>Client Hints platform version, e.g. "10.0.0" for Windows 10.</summary>
    public string? PlatformVersion { get; init; }

    public BrowserBrand Brand { get; init; } = BrowserBrand.Chrome;
    public string? BrandVersion { get; init; }

    public ScreenConfig Screen { get; init; } = new();
    public GpuConfig Gpu { get; init; } = new();

    /// <summary>navigator.hardwareConcurrency</summary>
    public ValueOf<int> CpuCores { get; init; } = new();

    /// <summary>navigator.deviceMemory (GB)</summary>
    public ValueOf<int> DeviceMemory { get; init; } = new();

    /// <summary>
    /// Storage quota in MB. Raised above the normalised default to defeat the
    /// incognito heuristic that reads a small quota as a private window.
    /// </summary>
    public int? StorageQuotaMb { get; init; } = 120000;

    public NoiseConfig Noise { get; init; } = new();

    /// <summary>Windows font metrics alignment (Chromium 148+, Linux host spoofing Windows).</summary>
    public bool WindowsFontMetrics { get; init; }

    /// <summary>Directory with target-platform fonts (Windows fonts on Linux etc.).</summary>
    public string? FontsDir { get; init; }

    /// <summary>Re-enable third-party cookies (needed for some SSO / payment flows).</summary>
    public bool AllowThirdPartyCookies { get; init; }

    public WebRtcConfig WebRtc { get; init; } = new();

    /// <summary>Taskbar height override (affects window.screen.availHeight coherence).</summary>
    public int? TaskbarHeight { get; init; }
}
