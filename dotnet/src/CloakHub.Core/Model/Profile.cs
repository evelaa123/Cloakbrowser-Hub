namespace CloakHub.Core.Model;

public enum ProxyKind { None, Http, Https, Socks5 }

public record ProxyConfig
{
    public ProxyKind Kind { get; init; } = ProxyKind.None;
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>Comma separated hosts that bypass the proxy, e.g. ".google.com".</summary>
    public string? Bypass { get; init; }

    /// <summary>Optional URL that rotates the IP for this proxy (GET request).</summary>
    public string? RotationUrl { get; init; }

    /// <summary>Reference to an entry in the proxy library, if this profile uses one.</summary>
    public string? SavedProxyId { get; init; }

    public bool IsConfigured => Kind != ProxyKind.None && !string.IsNullOrWhiteSpace(Host) && Port is > 0;
}

public sealed record SavedProxy : ProxyConfig
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public long CreatedAt { get; init; }
    public ProxyCheckResult? LastCheck { get; init; }
}

public sealed record ProxyCheckResult
{
    public bool Ok { get; init; }
    public long CheckedAt { get; init; }
    public string? Ip { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Timezone { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public int? LatencyMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>Ip = derive from proxy exit IP (geoip), Manual = pinned value.</summary>
public enum LocaleMode { Ip, Manual }

public sealed record LocaleConfig
{
    public LocaleMode Mode { get; init; } = LocaleMode.Ip;

    /// <summary>BCP 47, e.g. "en-US".</summary>
    public string? Locale { get; init; }

    /// <summary>IANA zone, e.g. "America/New_York".</summary>
    public string? Timezone { get; init; }
}

public enum HumanPresetKind { Default, Careful }

public sealed record BehaviourConfig
{
    public bool Humanize { get; init; }
    public HumanPresetKind Preset { get; init; } = HumanPresetKind.Default;
    public double? TypingDelay { get; init; }
    public double? MistypeChance { get; init; }
    public bool IdleBetweenActions { get; init; }
}

public sealed record StartupConfig
{
    public bool Headless { get; init; }
    public List<string> StartPages { get; init; } = [];
    public List<string> ExtraArgs { get; init; } = [];
    public List<string> ExtensionPaths { get; init; } = [];

    /// <summary>
    /// Localhost ports to block from page-initiated scans.
    /// <para>
    /// Sites probe localhost ports (VNC 5900, RDP 3389, debug servers) to
    /// correlate a visitor across profiles: the set of reachable ports is a
    /// machine trait that survives every fingerprint change. Blocking them is
    /// also what a typical user's firewall already does, so it reads as normal.
    /// </para>
    /// </summary>
    public List<int> BlockedPorts { get; init; } = [];

    /// <summary>Send the DNT request header. Off by default — most users have it off.</summary>
    public bool DoNotTrack { get; init; }
}

/// <summary>
/// A workflow label the user assigns, independent of whether a browser is running.
/// <para>
/// Distinct from the live "running / idle" state on purpose. Whether a browser
/// process exists right now is a fact the Hub observes; whether an account is warmed
/// up, banned or ready to sell is a judgement only the user holds, and it has to
/// survive restarts. Conflating the two would mean the label vanished every time the
/// app closed, which is exactly when it matters most — at fifty profiles the
/// question "which of these are burnt" cannot be answered from memory.
/// </para>
/// </summary>
public enum ProfileStatus { None, New, Warming, Ready, Working, Paused, Banned, Retired }

/// <summary>
/// What the profile is used for, which drives the platform-specific hints shown in
/// the editor rather than changing any launch flag.
/// <para>
/// It is deliberately advisory. Encoding "this is a Facebook profile" into the
/// fingerprint would be a mistake: there is no configuration that makes a browser
/// more acceptable to one site in a way a site could not also detect. What the label
/// legitimately buys is a reminder of which identity a profile belongs to when the
/// list is long.
/// </para>
/// </summary>
public enum ProfileKind { None, Facebook, Google, TikTok, Crypto, Shopping, Ads, Other }

/// <summary>
/// A locally-administered MAC address for the profile's session.
/// <para>
/// <b>This does not affect your browser fingerprint.</b> No web API exposes a MAC
/// address — not <c>navigator</c>, not WebRTC, not WebGL — so a site cannot read it
/// and changing it cannot make a profile look like a different visitor. It changes
/// what the local network sees: the DHCP lease, the router's device list, and
/// MAC-based recognition on a captive portal.
/// </para>
/// <para>
/// It is modelled because other anti-detect tools offer it and users reasonably ask
/// where it went. Applying it needs elevated privileges and is planned per
/// interface by <c>MacSpoof</c>, so this record only records the intent; the UI
/// states the limitation rather than implying a fingerprint benefit.
/// </para>
/// </summary>
public sealed record MacConfig
{
    /// <summary>Off leaves the interface alone, which is the default.</summary>
    public ValueMode Mode { get; init; } = ValueMode.Real;

    /// <summary>Explicit address when <see cref="Mode"/> is Manual.</summary>
    public string? Address { get; init; }

    /// <summary>Interface to change, e.g. <c>eth0</c> or <c>en0</c>. Blank means "ask at launch".</summary>
    public string? InterfaceName { get; init; }
}

public sealed record Profile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public List<string> Tags { get; init; } = [];

    /// <summary>Id of the containing folder, or null for the root.</summary>
    public string? FolderId { get; init; }

    /// <summary>User-assigned workflow label. See <see cref="ProfileStatus"/>.</summary>
    public ProfileStatus Status { get; init; } = ProfileStatus.None;

    /// <summary>Advisory category. See <see cref="ProfileKind"/>.</summary>
    public ProfileKind Kind { get; init; } = ProfileKind.None;

    /// <summary>
    /// Local network MAC intent. Not a fingerprint control — see <see cref="MacConfig"/>.
    /// </summary>
    public MacConfig Mac { get; init; } = new();

    /// <summary>
    /// Hostname the OS reports on the LAN, when the user pins one.
    /// <para>
    /// Like the MAC, invisible to websites. Kept for the same reason and labelled
    /// with the same caveat in the UI.
    /// </para>
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>Row accent colour in the profiles table (hex, e.g. "#3b82f6").</summary>
    public string? Colour { get; init; }

    public string? UserAgent { get; init; }
    public FingerprintConfig Fingerprint { get; init; } = new();
    public ProxyConfig Proxy { get; init; } = new();
    public LocaleConfig Locale { get; init; } = new();
    public GeoConfig Geo { get; init; } = new();
    public BehaviourConfig Behaviour { get; init; } = new();
    public StartupConfig Startup { get; init; } = new();

    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
    public long? LastLaunchedAt { get; init; }

    /// <summary>
    /// Schema version, used by the migration layer. See <c>ProfileMigration</c>
    /// for why a version is needed rather than "backfill anything missing".
    /// </summary>
    public int SchemaVersion { get; init; }
}

/// <summary>A profile folder — the grouping users asked for alongside tags.</summary>
public sealed record ProfileFolder
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public long CreatedAt { get; init; }

    /// <summary>Display order in the sidebar; ties broken by name.</summary>
    public int SortOrder { get; init; }
}
