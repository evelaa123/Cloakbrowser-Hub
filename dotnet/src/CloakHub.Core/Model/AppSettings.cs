namespace CloakHub.Core.Model;

/// <summary>
/// Application-wide settings, as opposed to per-profile configuration.
/// <para>
/// Kept separate from <see cref="Profile"/> because the two have different
/// lifetimes and different blast radius: a bad profile affects one browsing
/// identity, a bad setting affects every launch. They are also stored in separate
/// files so that losing one cannot take the other with it.
/// </para>
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// Where per-profile browser user-data directories live.
    /// <para>
    /// Null means the default under the Hub's data directory. Kept overridable
    /// because these directories grow to hundreds of megabytes each — caches,
    /// service workers, IndexedDB — and users with a small system drive need to put
    /// them on a larger one.
    /// </para>
    /// </summary>
    public string? ProfilesDir { get; init; }

    public ReleaseChannel ReleaseChannel { get; init; } = ReleaseChannel.Stable;

    /// <summary>
    /// Pin an exact Chromium version, for rollback.
    /// <para>
    /// Null tracks the channel. This exists because a browser update can change
    /// fingerprint surfaces, and a user who has profiles that a site already trusts
    /// may need to stay on the exact build those profiles were established with.
    /// </para>
    /// </summary>
    public string? BrowserVersion { get; init; }

    /// <summary>
    /// Soft cap on simultaneous sessions.
    /// <para>
    /// Five matches the free tier. This is only the user's own preference — the
    /// effective limit is the lower of this and the licence's seat count, resolved
    /// by <c>SessionLimit</c>, which also reports which one applied so a refused
    /// launch never looks like a crash.
    /// </para>
    /// </summary>
    public int MaxConcurrentSessions { get; init; } = 5;

    /// <summary>Write cookies back to the encrypted jar when a session ends.</summary>
    public bool SaveCookiesOnClose { get; init; } = true;

    /// <summary>
    /// Close running browsers when the Hub quits.
    /// <para>
    /// Defaults false: the browsers are separate processes and a user who closes the
    /// Hub window has not necessarily asked to lose their open tabs. Killing them
    /// would also skip the browser's own shutdown, which is when it flushes
    /// cookies and session storage.
    /// </para>
    /// </summary>
    public bool CloseSessionsOnQuit { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>
    /// Interface scale, 1.0 = 100%.
    /// <para>
    /// Scales the whole layout rather than just font size, because larger type
    /// inside unchanged boxes reads as cramped rather than as bigger. Clamped on
    /// load: a value of 0 would render an invisible window with no way to fix it
    /// from inside the app.
    /// </para>
    /// </summary>
    public double UiZoom { get; init; } = 1.0;

    /// <summary>Fingerprint platform applied to brand-new profiles.</summary>
    public FingerprintPlatform DefaultPlatform { get; init; } = FingerprintPlatform.Windows;

    public AutomationSettings Automation { get; init; } = new();

    /// <summary>Lowest and highest interface scale the UI will accept.</summary>
    public const double MinZoom = 0.7;
    public const double MaxZoom = 1.6;

    /// <summary>
    /// Force every field into a usable range.
    /// <para>
    /// Applied after loading, because the settings file is plain JSON that a user
    /// can hand-edit and a syncing tool can mangle. Clamping beats rejecting: a
    /// zoom of 40 should give a very large but working window, not a refusal to
    /// start, and an out-of-range value the user cannot see is not worth an error
    /// dialogue they cannot act on.
    /// </para>
    /// </summary>
    public AppSettings Normalised() => this with
    {
        MaxConcurrentSessions = Math.Clamp(MaxConcurrentSessions, 1, 200),
        UiZoom = double.IsFinite(UiZoom) ? Math.Clamp(UiZoom, MinZoom, MaxZoom) : 1.0,
        Automation = Automation.Normalised(),
        // Whitespace is not a path but is easy to produce by clearing a text box,
        // and would otherwise be treated as a real directory named " ".
        ProfilesDir = string.IsNullOrWhiteSpace(ProfilesDir) ? null : ProfilesDir.Trim(),
        BrowserVersion = string.IsNullOrWhiteSpace(BrowserVersion) ? null : BrowserVersion.Trim(),
    };
}

public enum ReleaseChannel { Stable, Preview }

public enum AppTheme { Dark, Light }

/// <summary>
/// The local automation HTTP API, for driving profiles from Puppeteer or Selenium.
/// </summary>
public sealed record AutomationSettings
{
    public bool Enabled { get; init; }

    /// <summary>Loopback TCP port for the REST API.</summary>
    public int Port { get; init; } = 7317;

    /// <summary>
    /// Bearer token required on every request.
    /// <para>
    /// Never empty while enabled, and that is a security property rather than
    /// tidiness. This endpoint can launch browsers and hand out CDP URLs, so an
    /// unauthenticated one is a local privilege-escalation vector for anything else
    /// on the machine — including JavaScript in a page, which can reach 127.0.0.1
    /// even though it cannot read the response cross-origin.
    /// </para>
    /// </summary>
    public string Token { get; init; } = "";

    public AutomationSettings Normalised()
    {
        var port = Port is >= 1024 and <= 65535 ? Port : 7317;

        // Generated here rather than at the call site, so there is no path that
        // enables the API without a token — including a hand-edited file that sets
        // enabled true and leaves the token blank.
        var token = Enabled && string.IsNullOrWhiteSpace(Token) ? NewToken() : Token.Trim();

        return this with { Port = port, Token = token };
    }

    /// <summary>
    /// A fresh token, from a cryptographic RNG.
    /// <para>
    /// <c>Random</c> would be seeded predictably enough for another local process to
    /// guess, which defeats the point of having a token at all.
    /// </para>
    /// </summary>
    public static string NewToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
}
