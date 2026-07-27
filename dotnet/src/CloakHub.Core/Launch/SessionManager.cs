using CloakHub.Core.Branding;
using CloakHub.Core.Model;

namespace CloakHub.Core.Launch;

/// <summary>
/// What the session manager needs from the browser wrapper.
/// <para>
/// An interface rather than a direct dependency on <c>CloakLauncher</c>, so the
/// orchestration — ordinal allocation, badge planning, the binary-override gate,
/// teardown ordering — can be tested without downloading a 200MB Chromium or
/// opening a window. Those are exactly the parts that were easy to get wrong.
/// </para>
/// </summary>
public interface IBrowserLauncher
{
    /// <summary>Start a persistent context and return a handle to it.</summary>
    Task<ILaunchedContext> LaunchPersistentContextAsync(
        string userDataDir, LaunchRequest request, CancellationToken ct);
}

/// <summary>Everything needed for one launch, already resolved.</summary>
public sealed record LaunchRequest
{
    public List<string> Args { get; init; } = [];
    public bool Headless { get; init; }
    public string? Timezone { get; init; }
    public string? Locale { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>
    /// The proxy for this session, or null for a direct connection.
    /// <para>
    /// Carried as the model rather than as prebuilt flags because an authenticated
    /// HTTP proxy may need a loopback relay standing behind it, and the relay has to
    /// be created and disposed with the session — which the launcher owns and a
    /// flag list cannot express.
    /// </para>
    /// </summary>
    public ProxyConfig? Proxy { get; init; }

    public bool GeoIp { get; init; }
    public bool Humanize { get; init; }
    public List<string> ExtensionPaths { get; init; } = [];
    public string? LicenseKey { get; init; }
    public string? BrowserVersion { get; init; }
    public string? ReleaseChannel { get; init; }

    /// <summary>
    /// Executable to run instead of the bundled browser, or null for the default.
    /// Set by the badge layer for the macOS bundle stub and the Windows shim.
    /// </summary>
    public string? ExecutableOverride { get; init; }
}

/// <summary>A live browser context.</summary>
public interface ILaunchedContext : IAsyncDisposable
{
    /// <summary>Native window handle, when the platform exposes one (Windows).</summary>
    IntPtr MainWindowHandle { get; }

    /// <summary>Raised when the user closes the last window.</summary>
    event EventHandler? Closed;

    Task CloseAsync();
}

/// <summary>
/// Starts and stops browser sessions, and owns the instance-badge lifecycle.
/// <para>
/// One session is one persistent context against the profile's own user-data
/// directory. Persistent rather than incognito is deliberate: it keeps cookies,
/// storage, service workers and cached fonts across runs, which is both what
/// account work needs and what defeats "empty ephemeral profile" heuristics.
/// </para>
/// </summary>
public sealed class SessionManager(
    IBrowserLauncher launcher,
    ISessionPaths paths,
    BadgeOs os,
    BadgeAssetWriter? badgeWriter = null)
{
    private readonly BadgeAssetWriter _badges = badgeWriter ?? new BadgeAssetWriter();
    private readonly OrdinalAllocator _ordinals = new();
    private readonly Dictionary<string, LiveSession> _sessions = [];
    private readonly Dictionary<string, List<SessionLogEntry>> _logs = [];

    // Guards the session table, the ordinal allocator and the log map together.
    // They are one piece of state: an ordinal handed out but not recorded against
    // a session would leak, and a session recorded without its ordinal would
    // release the wrong number on stop.
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private sealed class LiveSession
    {
        public required string ProfileId { get; init; }
        public required string ProfileName { get; init; }
        public required ILaunchedContext Context { get; init; }
        public required long StartedAt { get; init; }
        public required int Ordinal { get; init; }
        public required BadgePlan Plan { get; init; }
        public IReadOnlyList<string> WrittenAssets { get; init; } = [];
        public int? CdpPort { get; init; }
        public string? WsEndpoint { get; set; }

        /// <summary>Set while stop is in flight, so a double-click cannot tear down twice.</summary>
        public bool Closing { get; set; }
    }

    /// <summary>Raised when the session set changes, for the UI to refresh.</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>Raised for each new log line.</summary>
    public event EventHandler<(string ProfileId, SessionLogEntry Entry)>? Logged;

    /// <summary>Sessions currently running.</summary>
    public async Task<IReadOnlyList<SessionInfo>> ListAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            return [.. _sessions.Values.Select(Describe)];
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Log lines recorded for a profile.</summary>
    public async Task<IReadOnlyList<SessionLogEntry>> LogsAsync(string profileId)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            return _logs.TryGetValue(profileId, out var lines) ? [.. lines] : [];
        }
        finally { _mutex.Release(); }
    }

    /// <summary>
    /// Start a session for a profile.
    /// </summary>
    /// <param name="profile">Profile to launch.</param>
    /// <param name="request">
    /// Resolved launch parameters. The badge layer may add flags and an executable
    /// override, so this is treated as a base rather than the final word.
    /// </param>
    /// <param name="baseIcon">App icon PNG the badge is drawn onto.</param>
    /// <param name="maxSessions">Concurrency cap from the licence, or null for unlimited.</param>
    /// <param name="canWriteAssets">False for a read-only install.</param>
    /// <param name="windowsStub">Shipped launcher stub, enabling the Windows shim.</param>
    public async Task<SessionResult> StartAsync(
        Profile profile,
        LaunchRequest request,
        byte[]? baseIcon = null,
        int? maxSessions = null,
        bool canWriteAssets = true,
        string? windowsStub = null,
        CancellationToken ct = default)
    {
        int ordinal;
        BadgePlan plan;

        // ---- reserve under the lock, launch outside it ---------------------
        // The launch takes seconds (binary download, profile warm-up). Holding the
        // lock across it would block the UI thread's List() calls and make the app
        // look hung, so only the bookkeeping is serialised.
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.ContainsKey(profile.Id))
                return new SessionResult.Failed($"\"{profile.Name}\" is already running.");

            if (maxSessions is > 0 && _sessions.Count >= maxSessions)
                return new SessionResult.Failed(
                    $"Your licence allows {maxSessions} concurrent session" +
                    $"{(maxSessions == 1 ? "" : "s")}. Stop one before starting another.");

            ordinal = _ordinals.Acquire();
            plan = InstanceBadge.Plan(os, profile, ordinal, paths.BrandingAssetRoot, canWriteAssets, windowsStub);
        }
        finally { _mutex.Release(); }

        // From here on the ordinal is held, so every failure path must give it
        // back. Without this a failed launch would permanently consume a badge
        // number and the visible badges would stop being 1..n.
        try
        {
            var assets = _badges.Write(plan, BrowserExecutableFor(request), baseIcon, profile.Name);
            Log(profile.Id, plan.Strategy == BadgeStrategy.None ? LogLevel.Warn : LogLevel.Info, assets.Note);

            var args = new List<string>(request.Args);
            args.AddRange(assets.ExtraArgs);

            var effective = request with
            {
                Args = args,
                ExecutableOverride = assets.Executable ?? request.ExecutableOverride,
            };

            // Logged after the badge flags are merged so the line matches what
            // actually launched. A log that omits real flags is worse than no log:
            // it is the first thing anyone reads when debugging a fingerprint.
            Log(profile.Id, LogLevel.Info, $"Chromium flags: {string.Join(" ", effective.Args)}");

            ILaunchedContext context;

            // The override is an environment variable, so it is process-wide and
            // must be serialised. The gate is taken even when there is no override,
            // because an unredirected launch could otherwise observe a concurrent
            // launch's variable and start the wrong binary entirely.
            await using (await BinaryOverride.AcquireAsync(effective.ExecutableOverride, ct).ConfigureAwait(false))
            {
                context = await launcher
                    .LaunchPersistentContextAsync(paths.ProfileDataDir(profile.Id), effective, ct)
                    .ConfigureAwait(false);
            }

            // Windows overlay is the one strategy that needs a live window, so it
            // runs after the launch rather than before it.
            if (plan.Strategy == BadgeStrategy.WindowsOverlay && OperatingSystem.IsWindows())
                ApplyWindowsOverlay(profile, plan, context, baseIcon);

            var session = new LiveSession
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Context = context,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Ordinal = ordinal,
                Plan = plan,
                WrittenAssets = assets.Written,
            };

            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-checked, because the lock was released for the launch and a
                // second start could have been requested in that window. Losing the
                // race means discarding this context, not overwriting the winner.
                if (_sessions.ContainsKey(profile.Id))
                {
                    _ordinals.Release(ordinal);
                    _ = SafeCloseAsync(context);
                    return new SessionResult.Failed($"\"{profile.Name}\" is already running.");
                }
                _sessions[profile.Id] = session;
            }
            finally { _mutex.Release(); }

            // Subscribed after the session is recorded: the handler looks the
            // session up, so a window closed during startup would otherwise find
            // nothing and leak the ordinal.
            context.Closed += (_, _) => _ = OnWindowClosed(profile.Id);

            SessionsChanged?.Invoke(this, EventArgs.Empty);
            return new SessionResult.Started(Describe(session));
        }
        catch (Exception ex)
        {
            await _mutex.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _ordinals.Release(ordinal); }
            finally { _mutex.Release(); }

            Log(profile.Id, LogLevel.Error, $"Launch failed: {ex.Message}");
            return new SessionResult.Failed(ex.Message);
        }
    }

    /// <summary>Stop a running session.</summary>
    public async Task<SessionResult> StopAsync(string profileId)
    {
        LiveSession? session;

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(profileId, out session))
                return new SessionResult.Failed("That profile is not running.");

            // A second stop while the first is in flight is a normal double-click,
            // not an error, and running teardown twice would double-release the
            // ordinal and hand the same badge to two future sessions.
            if (session.Closing) return new SessionResult.Stopped(profileId);
            session.Closing = true;
        }
        finally { _mutex.Release(); }

        await SafeCloseAsync(session.Context).ConfigureAwait(false);
        await ForgetAsync(profileId).ConfigureAwait(false);

        return new SessionResult.Stopped(profileId);
    }

    /// <summary>Stop every session, for application exit.</summary>
    public async Task StopAllAsync()
    {
        List<string> ids;
        await _mutex.WaitAsync().ConfigureAwait(false);
        try { ids = [.. _sessions.Keys]; }
        finally { _mutex.Release(); }

        // Sequential rather than parallel. Teardown writes each profile's cookie
        // jar, and a machine closing twenty Chromium instances at once starves the
        // disk enough that some writes have been seen to lose the tail of a jar.
        foreach (var id in ids) await StopAsync(id).ConfigureAwait(false);

        await _mutex.WaitAsync().ConfigureAwait(false);
        try { _ordinals.Clear(); }
        finally { _mutex.Release(); }
    }

    // -----------------------------------------------------------------------

    private async Task OnWindowClosed(string profileId)
    {
        // The user closing the last window is a normal way to end a session, so it
        // must reach the same teardown as an explicit stop — otherwise the ordinal
        // stays allocated and the profile looks like it is still running.
        await _mutex.WaitAsync().ConfigureAwait(false);
        bool alreadyStopping;
        try
        {
            alreadyStopping = !_sessions.TryGetValue(profileId, out var s) || s.Closing;
            if (!alreadyStopping) _sessions[profileId].Closing = true;
        }
        finally { _mutex.Release(); }

        if (alreadyStopping) return;

        Log(profileId, LogLevel.Info, "Browser window closed; ending session.");
        await ForgetAsync(profileId).ConfigureAwait(false);
    }

    private async Task ForgetAsync(string profileId)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.Remove(profileId, out var session))
                _ordinals.Release(session.Ordinal);
        }
        finally { _mutex.Release(); }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task SafeCloseAsync(ILaunchedContext context)
    {
        // A browser that has already died throws on close. That is not a failure of
        // the stop operation — the goal is "not running", which is satisfied — and
        // letting it propagate would leave the session in the table forever.
        try { await context.CloseAsync().ConfigureAwait(false); }
        catch (Exception) { /* already gone */ }

        try { await context.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* already gone */ }
    }

    // Annotated rather than suppressed. The platform guard lives at the call site,
    // which the analyser cannot see through, so stating the requirement here is
    // what makes the constraint checkable instead of taken on trust.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ApplyWindowsOverlay(
        Profile profile, BadgePlan plan, ILaunchedContext context, byte[]? baseIcon)
    {
        var hwnd = context.MainWindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            Log(profile.Id, LogLevel.Warn,
                "The browser window handle was not available, so the taskbar badge could not be " +
                "applied. The session is unaffected.");
            return;
        }

        var ok = WindowsTaskbar.TrySetOverlay(
            hwnd,
            BadgeRenderer.BuildIco(baseIcon, plan.BadgeText),
            BadgeAssetWriter.DisplayName(profile.Name, plan.Ordinal),
            paths.TempDir);

        Log(profile.Id, ok ? LogLevel.Info : LogLevel.Warn,
            ok
                ? $"Taskbar badge \"{plan.BadgeText}\" applied."
                : "The taskbar badge could not be applied; the window keeps the stock icon.");
    }

    /// <summary>
    /// The real browser binary a generated launcher must invoke.
    /// <para>
    /// Not the same as the override: the shim and bundle stub need the path of the
    /// binary they wrap, whereas the override is the path of the shim itself.
    /// Confusing the two produces a launcher that invokes itself.
    /// </para>
    /// </summary>
    private static string BrowserExecutableFor(LaunchRequest request) =>
        request.ExecutableOverride ?? "chrome";

    private SessionInfo Describe(LiveSession s) => new()
    {
        ProfileId = s.ProfileId,
        ProfileName = s.ProfileName,
        StartedAt = s.StartedAt,
        Ordinal = s.Ordinal,
        CdpPort = s.CdpPort,
        WsEndpoint = s.WsEndpoint,
        BadgeStrategy = s.Plan.Strategy,
    };

    private void Log(string profileId, LogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var entry = new SessionLogEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), level, message);

        // Not taken under _mutex: Log is called from inside guarded regions, and a
        // non-reentrant semaphore would deadlock. The lists are only appended to,
        // and a short lock of their own keeps that safe.
        lock (_logs)
        {
            if (!_logs.TryGetValue(profileId, out var lines))
                _logs[profileId] = lines = [];
            lines.Add(entry);

            // Bounded, because a long-running session with a chatty extension can
            // otherwise grow this without limit.
            const int cap = 500;
            if (lines.Count > cap) lines.RemoveRange(0, lines.Count - cap);
        }

        Logged?.Invoke(this, (profileId, entry));
    }
}
