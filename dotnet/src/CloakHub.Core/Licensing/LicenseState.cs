namespace CloakHub.Core.Licensing;

/// <summary>
/// What the key entitles the user to.
/// <para>
/// <see cref="Unknown"/> exists because "we could not reach the server" is not the
/// same as "you have no key", and conflating them tells a paying offline user they
/// are unlicensed.
/// </para>
/// </summary>
public enum LicenseTier
{
    /// <summary>No key at all — the older free binary.</summary>
    None,

    /// <summary>A free key: the latest binary, one concurrent session.</summary>
    Free,

    /// <summary>A paid key: the latest binary, plan-defined concurrent sessions.</summary>
    Pro,

    /// <summary>A key is present but could not be checked.</summary>
    Unknown,
}

/// <summary>
/// Everything the licence panel needs, resolved in one object.
/// <para>
/// Assembled as a snapshot rather than exposed as live properties so the UI cannot
/// render a half-updated state: the tier, the seat count and the session counts are
/// only meaningful together, and a panel that showed a new tier beside a stale seat
/// count would be actively misleading about what a launch will do.
/// </para>
/// </summary>
public sealed record LicenseState
{
    public LicenseTier Tier { get; init; } = LicenseTier.None;

    /// <summary>The key, shortened for display. Never the full value.</summary>
    public string? MaskedKey { get; init; }

    /// <summary>Plan name as the server reported it, e.g. "solo".</summary>
    public string? Plan { get; init; }

    public bool Valid { get; init; }

    /// <summary>Expiry as the server reported it — a date string, not parsed, because the format is theirs.</summary>
    public string? Expires { get; init; }

    /// <summary>Concurrent sessions the server currently counts against this key, or null when unknown.</summary>
    public int? ActiveSessions { get; init; }

    /// <summary>Sessions this app currently has open.</summary>
    public int LocalSessions { get; init; }

    /// <summary>Concurrent sessions the plan allows, or null when unknown or unbounded.</summary>
    public int? Seats { get; init; }

    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>
    /// Set when the key file was stored in a non-UTF-8 encoding and has been
    /// rewritten. Surfaced so the user learns why their valid key was being
    /// rejected, instead of the app appearing to fix itself at random.
    /// </summary>
    public bool KeyFileRepaired { get; init; }

    /// <summary>True when the key came from an environment variable, so the file is not in play.</summary>
    public bool FromEnvironment { get; init; }

    public string? Error { get; init; }

    public bool HasKey => !string.IsNullOrEmpty(MaskedKey);

    /// <summary>
    /// True when the check failed for a reason that is not the key's fault.
    /// <para>
    /// The UI uses this to choose its wording. "Could not reach the license server"
    /// and "this key is invalid" call for opposite actions from the user, and
    /// showing the second when the first is true is the failure mode this whole
    /// distinction exists to prevent.
    /// </para>
    /// </summary>
    public bool Unreachable => Tier == LicenseTier.Unknown && HasKey;

    public string TierLabel => Tier switch
    {
        LicenseTier.None => "No key",
        LicenseTier.Free => "Free",
        LicenseTier.Pro => Plan is { Length: > 0 } p ? Capitalise(p) : "Pro",
        _ => "Unchecked",
    };

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
