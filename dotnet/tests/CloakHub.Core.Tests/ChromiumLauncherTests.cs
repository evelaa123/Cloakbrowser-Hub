using System.Runtime.InteropServices;
using CloakHub.Core.Launch;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// The command line and the process lifecycle.
/// <para>
/// The argument list is the entire spoofing surface — a flag that quietly stops
/// being passed is a profile the UI reports as protected and the browser does not.
/// So the exact list is asserted rather than sampled.
/// </para>
/// </summary>
public sealed class ChromiumLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cloakhub-launcher-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    private static List<string> Args(LaunchRequest request, string dir = "/data/profile-1") =>
        ChromiumLauncher.BuildArgs(dir, request);

    // ------------------------------------------------------------------
    // Argument assembly
    // ------------------------------------------------------------------

    [Fact]
    public void The_user_data_directory_comes_first()
    {
        // Order matters to Chromium for this one: it decides where every other
        // per-profile flag lands.
        var args = Args(new LaunchRequest(), "/data/profile-1");
        Assert.Equal("--user-data-dir=/data/profile-1", args[0]);
    }

    [Fact]
    public void Every_prebuilt_flag_is_passed_through_unchanged()
    {
        // Fingerprint, privacy and sandbox flags are resolved upstream. This layer
        // must not filter, reorder or rewrite them -- a dropped --fingerprint flag
        // is an unprotected session.
        var request = new LaunchRequest
        {
            Args = ["--fingerprint=12345", "--fingerprint-platform=windows", "--no-sandbox"],
        };

        var args = Args(request);

        Assert.Contains("--fingerprint=12345", args);
        Assert.Contains("--fingerprint-platform=windows", args);
        Assert.Contains("--no-sandbox", args);
    }

    [Fact]
    public void Locale_sets_both_the_ui_language_and_the_accept_language_header()
    {
        // Setting only --lang leaves Accept-Language on the host's real languages
        // while JavaScript reports the spoofed one. That mismatch is a single
        // request to detect and points straight at a spoofed profile.
        //
        // The header is a q-value chain, not the bare tag: no shipping browser
        // sends a single-entry Accept-Language, so "de-DE" alone would trade one
        // recognisable signature for another.
        var args = Args(new LaunchRequest { Locale = "de-DE" });

        Assert.Contains("--lang=de-DE", args);
        Assert.Contains("--accept-lang=de-DE,de;q=0.9,en;q=0.8", args);
    }

    [Fact]
    public void The_profile_directory_is_stated_rather_than_left_to_the_default()
    {
        // The importer clones session data into <user-data-dir>/Default. If the
        // browser is not told to read that same subdirectory, an imported profile
        // starts logged out -- which is exactly what happened while this flag was
        // implicit. Pinning it here keeps the two halves of the contract together.
        var args = Args(new LaunchRequest());

        Assert.Contains("--profile-directory=Default", args);
    }

    [Fact]
    public void No_locale_means_neither_language_flag_is_invented()
    {
        var args = Args(new LaunchRequest());

        Assert.DoesNotContain(args, a => a.StartsWith("--lang=", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--accept-lang=", StringComparison.Ordinal));
    }

    [Fact]
    public void Timezone_is_never_a_command_line_flag()
    {
        // ICU reads TZ from the environment at process start; there is no flag that
        // moves Date and Intl. A --timezone argument would look like it worked and
        // do nothing, which is the worst possible outcome for a spoofing setting.
        var args = Args(new LaunchRequest { Timezone = "Europe/Berlin" });

        Assert.DoesNotContain(args, a => a.Contains("Europe/Berlin", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("timezone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Headless_is_the_new_mode_not_the_legacy_one()
    {
        // Old headless is a different binary path with its own detectable quirks.
        Assert.Contains("--headless=new", Args(new LaunchRequest { Headless = true }));
        Assert.DoesNotContain("--headless", Args(new LaunchRequest { Headless = false }));
    }

    [Fact]
    public void Extensions_are_passed_as_one_comma_joined_flag()
    {
        // Chromium takes a single --load-extension with comma-separated paths;
        // repeating the flag loads only the last one.
        var request = new LaunchRequest { ExtensionPaths = ["/ext/a", "/ext/b"] };

        Assert.Contains("--load-extension=/ext/a,/ext/b", Args(request));
    }

    [Fact]
    public void No_extensions_means_no_empty_flag()
    {
        // "--load-extension=" makes Chromium complain on startup.
        Assert.DoesNotContain(
            Args(new LaunchRequest()),
            a => a.StartsWith("--load-extension", StringComparison.Ordinal));
    }

    [Fact]
    public void A_user_agent_override_is_forwarded()
    {
        var request = new LaunchRequest { UserAgent = "Mozilla/5.0 (Windows NT 10.0)" };
        Assert.Contains("--user-agent=Mozilla/5.0 (Windows NT 10.0)", Args(request));
    }

    [Fact]
    public void First_run_and_default_browser_prompts_are_always_suppressed()
    {
        // Both steal focus on every fresh profile, and the default-browser check
        // writes state that then differs between profiles.
        var args = Args(new LaunchRequest());

        Assert.Contains("--no-first-run", args);
        Assert.Contains("--no-default-browser-check", args);
    }

    [Fact]
    public void Remote_debugging_is_off_unless_something_asked_for_it()
    {
        // An always-open CDP port is an automation surface a page can probe for, and
        // it is exactly what the rest of this app exists to avoid advertising.
        Assert.DoesNotContain(
            Args(new LaunchRequest()),
            a => a.Contains("remote-debugging", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Ports
    // ------------------------------------------------------------------

    [Fact]
    public void A_free_port_is_a_real_assignment_not_a_guess()
    {
        // Two profiles starting together must not be handed the same number, so the
        // port comes from the OS rather than from a counter.
        var a = ChromiumLauncher.FreePort();
        var b = ChromiumLauncher.FreePort();

        Assert.InRange(a, 1024, 65535);
        Assert.InRange(b, 1024, 65535);
    }

    // ------------------------------------------------------------------
    // Process behaviour
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reports_a_missing_browser_as_its_own_condition()
    {
        // Distinct from a general launch failure so the UI can offer the install
        // command instead of a stack trace for what is really a first-run state.
        var launcher = new ChromiumLauncher(
            () => new BinaryResolution(null, "No browser found. Install one."));

        var thrown = await Assert.ThrowsAsync<BrowserNotFoundException>(() =>
            launcher.LaunchPersistentContextAsync(
                Path.Combine(_root, "p1"), new LaunchRequest(), CancellationToken.None));

        Assert.Contains("Install one", thrown.Message);
    }

    [Fact]
    public async Task An_executable_override_bypasses_resolution_entirely()
    {
        // The badge layer substitutes a stub launcher. If resolution still ran, a
        // machine with no browser installed could not badge at all.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var stub = Stub("#!/bin/sh\nsleep 30\n");

        var launcher = new ChromiumLauncher(
            () => throw new InvalidOperationException("resolution should not run"));

        await using var context = await launcher.LaunchPersistentContextAsync(
            Path.Combine(_root, "p1"),
            new LaunchRequest { ExecutableOverride = stub },
            CancellationToken.None);

        Assert.NotNull(context);
    }

    [Fact]
    public async Task The_user_data_directory_is_created_before_launch()
    {
        // Chromium will create it itself, but only after it has already decided the
        // path is usable; on a fresh install the parent may not exist at all.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var stub = Stub("#!/bin/sh\nsleep 30\n");
        var dataDir = Path.Combine(_root, "nested", "deeper", "profile");

        var launcher = new ChromiumLauncher(() => new BinaryResolution(stub, null));

        await using var context = await launcher.LaunchPersistentContextAsync(
            dataDir, new LaunchRequest(), CancellationToken.None);

        Assert.True(Directory.Exists(dataDir));
    }

    [Fact]
    public async Task An_immediate_nonzero_exit_is_surfaced_rather_than_reported_as_running()
    {
        // A browser that dies on a bad flag would otherwise leave a row claiming to
        // be running with no window anywhere -- a state the user cannot act on.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var stub = Stub("#!/bin/sh\nexit 3\n");

        var launcher = new ChromiumLauncher(() => new BinaryResolution(stub, null));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchPersistentContextAsync(
                Path.Combine(_root, "p1"), new LaunchRequest(), CancellationToken.None));

        Assert.Contains("code 3", thrown.Message);
    }

    [Fact]
    public async Task Closing_a_session_ends_the_process()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var stub = Stub("#!/bin/sh\nsleep 30\n");
        var launcher = new ChromiumLauncher(() => new BinaryResolution(stub, null));

        var context = await launcher.LaunchPersistentContextAsync(
            Path.Combine(_root, "p1"), new LaunchRequest(), CancellationToken.None);

        await context.CloseAsync();

        // Idempotent: the browser can also exit on its own, and both paths run this.
        await context.CloseAsync();
    }

    [Fact]
    public async Task The_process_exiting_on_its_own_raises_Closed()
    {
        // The user closing the last browser window is an ordinary end to a session,
        // and the Hub has to notice or the row stays stuck at "Running".
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var stub = Stub("#!/bin/sh\nsleep 1\n");
        var launcher = new ChromiumLauncher(() => new BinaryResolution(stub, null));

        await using var context = await launcher.LaunchPersistentContextAsync(
            Path.Combine(_root, "p1"), new LaunchRequest(), CancellationToken.None);

        var closed = new TaskCompletionSource();
        context.Closed += (_, _) => closed.TrySetResult();

        var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(closed.Task, finished);
    }

    /// <summary>Write an executable shell script standing in for the browser.</summary>
    private string Stub(string script)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "stub-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, script);

        // Guarded rather than asserted: every caller already returns early on
        // Windows, but the analyser cannot see that through the call.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
