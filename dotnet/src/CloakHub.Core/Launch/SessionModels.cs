using CloakHub.Core.Branding;

namespace CloakHub.Core.Launch;

/// <summary>Severity of a line in a session log.</summary>
public enum LogLevel { Info, Warn, Error }

/// <summary>One line of session diagnostics.</summary>
/// <param name="At">Unix milliseconds.</param>
public sealed record SessionLogEntry(long At, LogLevel Level, string Message);

/// <summary>A running session, as the UI sees it.</summary>
public sealed record SessionInfo
{
    public string ProfileId { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public long StartedAt { get; init; }

    /// <summary>Badge number shown on this session's window icon.</summary>
    public int Ordinal { get; init; }

    /// <summary>DevTools port, when automation is enabled for the profile.</summary>
    public int? CdpPort { get; init; }

    /// <summary>Resolved CDP WebSocket URL, read once at start.</summary>
    public string? WsEndpoint { get; init; }

    /// <summary>How the window was branded, for display and troubleshooting.</summary>
    public BadgeStrategy BadgeStrategy { get; init; }
}

/// <summary>
/// Outcome of a start or stop request.
/// <para>
/// A result type rather than exceptions for expected failures. "This profile is
/// already running" and "the session limit is reached" are normal answers to a
/// user action, not faults, and modelling them as exceptions makes it too easy
/// for a caller to let one escape to a crash dialog. Genuine faults still throw.
/// </para>
/// </summary>
public abstract record SessionResult
{
    public sealed record Started(SessionInfo Session) : SessionResult;
    public sealed record Stopped(string ProfileId) : SessionResult;
    public sealed record Failed(string Error) : SessionResult;
}

/// <summary>Where a profile's data and generated assets live.</summary>
public interface ISessionPaths
{
    /// <summary>Chromium user-data directory for a profile.</summary>
    string ProfileDataDir(string profileId);

    /// <summary>Directory the Hub may write generated branding assets into.</summary>
    string BrandingAssetRoot { get; }

    /// <summary>Scratch directory for short-lived files such as overlay icons.</summary>
    string TempDir { get; }
}
