using CloakHub.Core.Branding;
using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using Xunit;

namespace CloakHub.Core.Tests;

internal sealed class FakeContext : ILaunchedContext
{
    public IntPtr MainWindowHandle { get; set; } = IntPtr.Zero;
    public event EventHandler? Closed;
    public bool CloseCalled { get; private set; }
    public bool ThrowOnClose { get; set; }

    public Task CloseAsync()
    {
        CloseCalled = true;
        if (ThrowOnClose) throw new InvalidOperationException("browser already gone");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Simulate the user closing the last window.</summary>
    public void SimulateUserClose() => Closed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeLauncher : IBrowserLauncher
{
    public List<LaunchRequest> Requests { get; } = [];
    public List<string?> ObservedOverrideEnv { get; } = [];
    public Func<LaunchRequest, FakeContext>? Factory { get; set; }
    public Exception? Throw { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task<ILaunchedContext> LaunchPersistentContextAsync(
        string userDataDir, LaunchRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);

        // The delay comes BEFORE the variable is read, and that ordering is the
        // whole point of this fake. Reading it first made the concurrency test pass
        // even with the serialising gate removed — verified by deleting the gate —
        // because the read happened before any other launch could interleave. A test
        // that cannot fail is worse than no test: it reports a guarantee that is not
        // there. Sampling after the await puts the read inside the window where a
        // competing launch would have overwritten the value.
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct).ConfigureAwait(false);

        var seen = Environment.GetEnvironmentVariable(BinaryOverride.EnvironmentVariable);
        lock (ObservedOverrideEnv) ObservedOverrideEnv.Add(seen);

        if (Throw is not null) throw Throw;

        return Factory?.Invoke(request) ?? new FakeContext();
    }
}

internal sealed class FakePaths : ISessionPaths
{
    public string ProfileDataDir(string profileId) => $"/data/{profileId}";
    public string BrandingAssetRoot => "/assets";
    public string TempDir => "/tmp/cloakhub";
}

public class SessionManagerTests
{
    private static Profile P(string id = "p1", string name = "Shopping") =>
        new() { Id = id, Name = name };

    private static SessionManager Subject(
        FakeLauncher launcher, BadgeOs os = BadgeOs.Linux, FakeFileSystem? fs = null) =>
        new(launcher, new FakePaths(), os, new BadgeAssetWriter(fs ?? new FakeFileSystem()));

    // ---------------------------------------------------------------------
    // Ordinals. The badge numbers must always be 1..n for n running sessions.

    [Fact]
    public async Task Sessions_get_consecutive_ordinals()
    {
        var mgr = Subject(new FakeLauncher());

        var a = await mgr.StartAsync(P("a"), new LaunchRequest());
        var b = await mgr.StartAsync(P("b"), new LaunchRequest());
        var c = await mgr.StartAsync(P("c"), new LaunchRequest());

        Assert.Equal(1, ((SessionResult.Started)a).Session.Ordinal);
        Assert.Equal(2, ((SessionResult.Started)b).Session.Ordinal);
        Assert.Equal(3, ((SessionResult.Started)c).Session.Ordinal);
    }

    [Fact]
    public async Task A_stopped_session_returns_its_ordinal_to_the_pool()
    {
        var mgr = Subject(new FakeLauncher());
        await mgr.StartAsync(P("a"), new LaunchRequest());
        await mgr.StartAsync(P("b"), new LaunchRequest());
        await mgr.StartAsync(P("c"), new LaunchRequest());

        await mgr.StopAsync("b");
        var d = await mgr.StartAsync(P("d"), new LaunchRequest());

        // 2, not 4: the visible badges stay 1..n rather than drifting upward.
        Assert.Equal(2, ((SessionResult.Started)d).Session.Ordinal);
    }

    [Fact]
    public async Task A_failed_launch_does_not_consume_an_ordinal()
    {
        // The ordinal is reserved before the launch, so every failure path has to
        // give it back. Otherwise a few failed launches would push live sessions to
        // "4", "7", "9" with nothing in between.
        var launcher = new FakeLauncher { Throw = new InvalidOperationException("no binary") };
        var mgr = Subject(launcher);

        var failed = await mgr.StartAsync(P("a"), new LaunchRequest());
        Assert.IsType<SessionResult.Failed>(failed);

        launcher.Throw = null;
        var ok = await mgr.StartAsync(P("b"), new LaunchRequest());
        Assert.Equal(1, ((SessionResult.Started)ok).Session.Ordinal);
    }

    [Fact]
    public async Task A_user_closing_the_window_ends_the_session_and_frees_the_ordinal()
    {
        // Closing the last window is a normal way to end a session and must reach
        // the same teardown as an explicit stop, or the profile looks like it is
        // still running forever.
        var context = new FakeContext();
        var launcher = new FakeLauncher { Factory = _ => context };
        var mgr = Subject(launcher);

        await mgr.StartAsync(P("a"), new LaunchRequest());
        Assert.Single(await mgr.ListAsync());

        context.SimulateUserClose();

        // The handler is async; give it a turn to run.
        for (var i = 0; i < 50 && (await mgr.ListAsync()).Count > 0; i++)
            await Task.Delay(10);

        Assert.Empty(await mgr.ListAsync());

        var next = await mgr.StartAsync(P("b"), new LaunchRequest());
        Assert.Equal(1, ((SessionResult.Started)next).Session.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Idempotence and duplicates.

    [Fact]
    public async Task Starting_a_running_profile_twice_is_refused()
    {
        var mgr = Subject(new FakeLauncher());
        await mgr.StartAsync(P("a"), new LaunchRequest());

        var again = await mgr.StartAsync(P("a"), new LaunchRequest());

        var failed = Assert.IsType<SessionResult.Failed>(again);
        Assert.Contains("already running", failed.Error);
        Assert.Single(await mgr.ListAsync());
    }

    [Fact]
    public async Task A_concurrent_double_start_of_one_profile_yields_one_session()
    {
        // The lock is released across the launch, so two clicks can both pass the
        // first duplicate check. The loser must be discarded rather than overwrite
        // the winner, which would leak a browser process and an ordinal.
        var launcher = new FakeLauncher { Delay = TimeSpan.FromMilliseconds(80) };
        var mgr = Subject(launcher);

        var first = mgr.StartAsync(P("a"), new LaunchRequest());
        var second = mgr.StartAsync(P("a"), new LaunchRequest());
        var results = await Task.WhenAll(first, second);

        Assert.Single(results.OfType<SessionResult.Started>());
        Assert.Single(results.OfType<SessionResult.Failed>());
        Assert.Single(await mgr.ListAsync());

        // And the ordinal the loser reserved was returned.
        await mgr.StopAsync("a");
        var next = await mgr.StartAsync(P("b"), new LaunchRequest());
        Assert.Equal(1, ((SessionResult.Started)next).Session.Ordinal);
    }

    [Fact]
    public async Task Stopping_twice_is_not_an_error_and_does_not_double_release()
    {
        var mgr = Subject(new FakeLauncher());
        await mgr.StartAsync(P("a"), new LaunchRequest());
        await mgr.StartAsync(P("b"), new LaunchRequest());

        await mgr.StopAsync("a");
        var again = await mgr.StopAsync("a");
        Assert.IsType<SessionResult.Failed>(again);   // no longer running

        // "b" must still hold ordinal 2; a double release would have freed it and
        // handed 2 to the next launch while b was still using it.
        var sessions = await mgr.ListAsync();
        Assert.Equal(2, Assert.Single(sessions).Ordinal);
    }

    [Fact]
    public async Task Stopping_an_unknown_profile_is_reported_not_thrown()
    {
        var mgr = Subject(new FakeLauncher());
        Assert.IsType<SessionResult.Failed>(await mgr.StopAsync("nope"));
    }

    [Fact]
    public async Task A_browser_that_already_died_still_stops_cleanly()
    {
        // Closing a dead browser throws. The goal of stop is "not running", which is
        // already satisfied, so it must not leave the session stuck in the table.
        var launcher = new FakeLauncher { Factory = _ => new FakeContext { ThrowOnClose = true } };
        var mgr = Subject(launcher);
        await mgr.StartAsync(P("a"), new LaunchRequest());

        Assert.IsType<SessionResult.Stopped>(await mgr.StopAsync("a"));
        Assert.Empty(await mgr.ListAsync());
    }

    // ---------------------------------------------------------------------
    // Session limit.

    [Fact]
    public async Task The_session_limit_is_enforced_and_explained()
    {
        var mgr = Subject(new FakeLauncher());
        await mgr.StartAsync(P("a"), new LaunchRequest(), maxSessions: 2);
        await mgr.StartAsync(P("b"), new LaunchRequest(), maxSessions: 2);

        var third = await mgr.StartAsync(P("c"), new LaunchRequest(), maxSessions: 2);

        var failed = Assert.IsType<SessionResult.Failed>(third);
        Assert.Contains("2 concurrent session", failed.Error);
        // Singular/plural matters here because the free tier allows exactly one.
        var one = Subject(new FakeLauncher());
        await one.StartAsync(P("x"), new LaunchRequest(), maxSessions: 1);
        var second = await one.StartAsync(P("y"), new LaunchRequest(), maxSessions: 1);
        Assert.Contains("1 concurrent session.", ((SessionResult.Failed)second).Error);
    }

    [Fact]
    public async Task A_refused_launch_over_the_limit_does_not_consume_an_ordinal()
    {
        var mgr = Subject(new FakeLauncher());
        await mgr.StartAsync(P("a"), new LaunchRequest(), maxSessions: 1);
        await mgr.StartAsync(P("b"), new LaunchRequest(), maxSessions: 1);

        await mgr.StopAsync("a");
        var next = await mgr.StartAsync(P("c"), new LaunchRequest(), maxSessions: 1);
        Assert.Equal(1, ((SessionResult.Started)next).Session.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The binary override, which is process-wide and therefore the most
    // dangerous piece of this class.

    [Fact]
    public async Task The_executable_override_is_visible_during_the_launch_and_gone_after()
    {
        // macOS and Windows badging work by launching a different executable, so the
        // override has to actually reach the wrapper — and must not leak afterwards,
        // or every later launch in the process would use the wrong binary.
        var launcher = new FakeLauncher();
        var mgr = Subject(launcher, BadgeOs.MacOs);

        await mgr.StartAsync(P("a"), new LaunchRequest());

        var observed = Assert.Single(launcher.ObservedOverrideEnv);
        Assert.NotNull(observed);
        Assert.Contains(".app", observed);

        Assert.Null(Environment.GetEnvironmentVariable(BinaryOverride.EnvironmentVariable));
    }

    [Fact]
    public async Task Concurrent_launches_never_see_each_others_override()
    {
        // The override is an environment variable: process-wide, not per-call. Two
        // simultaneous launches would otherwise race and one profile could start
        // another profile's shim. Each observation must match its own profile.
        var launcher = new FakeLauncher { Delay = TimeSpan.FromMilliseconds(40) };
        var mgr = Subject(launcher, BadgeOs.MacOs);

        // Started without awaiting in between so all three are in flight at once;
        // the fake samples the variable after its delay, inside the race window.
        await Task.WhenAll(
            mgr.StartAsync(P("aaaa"), new LaunchRequest()),
            mgr.StartAsync(P("bbbb"), new LaunchRequest()),
            mgr.StartAsync(P("cccc"), new LaunchRequest()));

        Assert.Equal(3, launcher.ObservedOverrideEnv.Count);
        foreach (var seen in launcher.ObservedOverrideEnv)
        {
            Assert.NotNull(seen);
            // Every observation belongs to exactly one profile, so no launch saw a
            // value that had been overwritten by a concurrent one.
            var owners = new[] { "aaaa", "bbbb", "cccc" }.Count(id => seen!.Contains(id));
            Assert.Equal(1, owners);
        }

        Assert.Null(Environment.GetEnvironmentVariable(BinaryOverride.EnvironmentVariable));
    }

    [Fact]
    public async Task A_launch_failure_restores_the_override()
    {
        var launcher = new FakeLauncher { Throw = new InvalidOperationException("boom") };
        var mgr = Subject(launcher, BadgeOs.MacOs);

        await mgr.StartAsync(P("a"), new LaunchRequest());

        // A held gate would deadlock every subsequent launch, and a stale variable
        // would misdirect them.
        Assert.Null(Environment.GetEnvironmentVariable(BinaryOverride.EnvironmentVariable));

        launcher.Throw = null;
        Assert.IsType<SessionResult.Started>(await mgr.StartAsync(P("b"), new LaunchRequest()));
    }

    [Fact]
    public async Task Linux_brands_via_flags_and_keeps_the_stock_binary()
    {
        var launcher = new FakeLauncher();
        var mgr = Subject(launcher, BadgeOs.Linux);

        await mgr.StartAsync(P("a"), new LaunchRequest());

        var request = Assert.Single(launcher.Requests);
        Assert.Contains(request.Args, a => a.StartsWith("--class=", StringComparison.Ordinal));
        // No redirect on Linux: the WM resolves the icon through the desktop entry.
        Assert.Null(launcher.ObservedOverrideEnv[0]);
    }

    [Fact]
    public async Task Base_launch_args_are_preserved_when_badge_flags_are_added()
    {
        // Losing a caller's flags here would silently disable the fingerprint.
        var launcher = new FakeLauncher();
        var mgr = Subject(launcher, BadgeOs.Linux);

        await mgr.StartAsync(P("a"), new LaunchRequest
        {
            Args = ["--fingerprint=12345", "--fingerprint-platform=windows"],
        });

        var request = Assert.Single(launcher.Requests);
        Assert.Contains("--fingerprint=12345", request.Args);
        Assert.Contains("--fingerprint-platform=windows", request.Args);
    }

    // ---------------------------------------------------------------------
    // Logging.

    [Fact]
    public async Task The_flag_log_line_matches_what_actually_launched()
    {
        // The log is the first thing anyone reads when debugging a fingerprint, so a
        // line that omits flags added after it was written would mislead.
        var launcher = new FakeLauncher();
        var mgr = Subject(launcher, BadgeOs.Linux);

        await mgr.StartAsync(P("a"), new LaunchRequest { Args = ["--fingerprint=1"] });

        var logs = await mgr.LogsAsync("a");
        var line = logs.Single(l => l.Message.StartsWith("Chromium flags:", StringComparison.Ordinal));
        var actual = string.Join(" ", launcher.Requests[0].Args);
        Assert.Equal($"Chromium flags: {actual}", line.Message);
    }

    [Fact]
    public async Task A_failed_launch_is_logged_as_an_error()
    {
        var launcher = new FakeLauncher { Throw = new InvalidOperationException("no binary") };
        var mgr = Subject(launcher);

        await mgr.StartAsync(P("a"), new LaunchRequest());

        var logs = await mgr.LogsAsync("a");
        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("no binary"));
    }

    [Fact]
    public async Task Stop_all_clears_every_session_and_all_ordinals()
    {
        var mgr = Subject(new FakeLauncher());
        await mgr.StartAsync(P("a"), new LaunchRequest());
        await mgr.StartAsync(P("b"), new LaunchRequest());
        await mgr.StartAsync(P("c"), new LaunchRequest());

        await mgr.StopAllAsync();

        Assert.Empty(await mgr.ListAsync());
        var next = await mgr.StartAsync(P("d"), new LaunchRequest());
        Assert.Equal(1, ((SessionResult.Started)next).Session.Ordinal);
    }
}
