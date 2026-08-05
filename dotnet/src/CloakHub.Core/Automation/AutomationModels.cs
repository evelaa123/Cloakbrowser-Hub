using CloakHub.Core.Model;

namespace CloakHub.Core.Automation;

/// <summary>
/// Where a script attaches to a running session.
/// <para>
/// Both forms are given because the automation libraries want different ones:
/// Puppeteer and Playwright take the WebSocket URL, Selenium takes the
/// <c>host:port</c> as <c>debuggerAddress</c>. Returning only one would force every
/// Selenium user to derive the other by hand from an undocumented convention.
/// </para>
/// </summary>
public sealed record AutomationEndpoint
{
    public required string ProfileId { get; init; }
    public required string ProfileName { get; init; }

    /// <summary>CDP WebSocket URL — <c>puppeteer.connect({ browserWSEndpoint })</c>.</summary>
    public required string WsEndpoint { get; init; }

    /// <summary>CDP HTTP origin — <c>http://127.0.0.1:&lt;port&gt;</c>, for Selenium's debuggerAddress.</summary>
    public required string HttpEndpoint { get; init; }

    /// <summary>The DevTools port Chromium actually bound.</summary>
    public required int Port { get; init; }
}

/// <summary>
/// What the automation server needs from the rest of the app.
/// <para>
/// An interface rather than direct references to the stores and the session
/// manager, so the routing can be tested against a fake without launching a browser
/// — and so the server cannot quietly reach for capabilities beyond this list.
/// </para>
/// </summary>
public interface IAutomationHost
{
    IReadOnlyList<Profile> ListProfiles();
    Profile? GetProfile(string id);
    Profile CreateProfile(string? name, FingerprintPlatform? platform);
    Profile? UpdateProfile(string id, Profile patched);
    bool DeleteProfile(string id, bool deleteData);

    Task<string?> StartSessionAsync(string id, CancellationToken cancel);
    Task StopSessionAsync(string id, CancellationToken cancel);

    AutomationEndpoint? Endpoint(string id);
    bool IsRunning(string id);

    void Log(string message);
}
