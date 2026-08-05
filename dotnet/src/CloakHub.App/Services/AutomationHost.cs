using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloakHub.Core.Automation;
using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using CloakHub.Core.Storage;

namespace CloakHub.App.Services;

/// <summary>
/// Connects the automation API to the app's real stores and session manager.
/// <para>
/// The adapter exists so <see cref="AutomationServer"/> never holds the stores
/// directly. That keeps the routing testable against a fake, and — more usefully —
/// makes the capability surface of the API a written list rather than "whatever the
/// server happens to be able to reach". An endpoint that can launch browsers and
/// hand out CDP URLs is worth being explicit about.
/// </para>
/// <para>
/// Every launch decision — the flags, the proxy, the concurrency cap — is delegated
/// to the same code the UI uses. A second implementation here would drift, and the
/// drift would show up as a scripted session that is fingerprinted differently from
/// a clicked one, which is precisely the bug that would be hardest to notice.
/// </para>
/// </summary>
public sealed class AutomationHost : IAutomationHost
{
    private readonly ProfileStore _profiles;
    private readonly SessionManager _sessions;
    private readonly SettingsStore _settings;

    /// <summary>Starts a session exactly as the Profiles page does. Returns an error, or null.</summary>
    private readonly Func<Profile, CancellationToken, Task<string?>> _start;

    /// <summary>Where a profile's browser user-data directory lives, for deletion.</summary>
    private readonly Func<string, string> _dataDirFor;

    private readonly Action<string> _log;

    public AutomationHost(
        ProfileStore profiles,
        SessionManager sessions,
        SettingsStore settings,
        Func<Profile, CancellationToken, Task<string?>> start,
        Func<string, string> dataDirFor,
        Action<string> log)
    {
        _profiles = profiles;
        _sessions = sessions;
        _settings = settings;
        _start = start;
        _dataDirFor = dataDirFor;
        _log = log;
    }

    // ------------------------------------------------------------------
    // Profiles
    // ------------------------------------------------------------------

    public IReadOnlyList<Profile> ListProfiles() => _profiles.List();

    public Profile? GetProfile(string id) => _profiles.Get(id);

    public Profile CreateProfile(string? name, FingerprintPlatform? platform)
    {
        var settings = _settings.Current;

        var profile = ProfileFactory.NewProfile(
            string.IsNullOrWhiteSpace(name) ? DefaultName() : name.Trim(),
            platform ?? settings.DefaultPlatform);

        return _profiles.Add(profile);
    }

    public Profile? UpdateProfile(string id, Profile patched)
    {
        // Existence is re-checked here rather than trusted from the caller: the
        // server read the profile to build the patch, and a concurrent delete
        // between those two points would otherwise resurrect it.
        if (_profiles.Get(id) is null) return null;

        _profiles.Update(patched);
        return _profiles.Get(id);
    }

    public bool DeleteProfile(string id, bool deleteData)
    {
        if (!_profiles.Remove(id)) return false;

        if (deleteData)
        {
            try
            {
                var dir = _dataDirFor(id);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e)
            {
                // Logged, not thrown. The profile record is already gone, so failing
                // the call now would report "delete failed" for something that half
                // succeeded — and the caller has no way to retry just the directory.
                _log($"Could not delete the data directory for {id}: {e.Message}");
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Sessions
    // ------------------------------------------------------------------

    public async Task<string?> StartSessionAsync(string id, CancellationToken cancel)
    {
        var profile = _profiles.Get(id);
        if (profile is null) return "That profile no longer exists.";

        return await _start(profile, cancel).ConfigureAwait(false);
    }

    public async Task StopSessionAsync(string id, CancellationToken cancel)
    {
        // Cancellation is accepted but not forwarded: an interrupted stop would leave
        // a browser running with the Hub believing it had gone, and the teardown is
        // what flushes cookies and session storage back to disk.
        _ = cancel;
        await _sessions.StopAsync(id).ConfigureAwait(false);
    }

    public bool IsRunning(string id) => _sessions.IsRunning(id);

    /// <summary>
    /// Where a script attaches, or null when the session exposes no DevTools port.
    /// <para>
    /// Null is a real answer rather than an error: automation is opt-in per profile,
    /// and a session started without it genuinely has nowhere to connect. Fabricating
    /// an endpoint would move the failure to the script's connect call, where the
    /// cause is much harder to see.
    /// </para>
    /// </summary>
    public AutomationEndpoint? Endpoint(string id)
    {
        var live = _sessions.ListAsync().GetAwaiter().GetResult();

        foreach (var session in live)
        {
            if (!string.Equals(session.ProfileId, id, StringComparison.Ordinal)) continue;
            if (session.CdpPort is not { } port) return null;

            return new AutomationEndpoint
            {
                ProfileId = session.ProfileId,
                ProfileName = session.ProfileName,

                // The URL Chromium reported, when it was read. Deriving one from the
                // port would be a guess: the browser's WebSocket path contains a
                // per-launch GUID that cannot be reconstructed.
                WsEndpoint = session.WsEndpoint ?? "",
                HttpEndpoint = $"http://127.0.0.1:{port}",
                Port = port,
            };
        }

        return null;
    }

    public void Log(string message) => _log(message);

    /// <summary>A name that does not collide, for a profile created without one.</summary>
    private string DefaultName()
    {
        var taken = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var p in _profiles.List()) taken.Add(p.Name);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"API profile {i}";
            if (taken.Add(candidate)) return candidate;
        }

        return $"API profile {Guid.NewGuid().ToString()[..8]}";
    }
}
