using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CloakHub.Core.Network;

namespace CloakHub.Core.Launch;

/// <summary>
/// Launches the stealth Chromium build as a child process.
/// <para>
/// The browser is started directly rather than through a driver. Every stealth
/// setting this app produces is already a command-line flag — see
/// <see cref="FingerprintArgs"/> — so a driver would add a process, an automation
/// protocol and a detectable surface without adding capability. It would also
/// contradict the goal: a page that finds automation instrumentation has learned
/// something about the visitor that no amount of fingerprint spoofing hides.
/// </para>
/// <para>
/// Remote debugging is therefore opt-in and off unless the automation API asked for
/// it, rather than a permanent fixture of every launch.
/// </para>
/// </summary>
public sealed class ChromiumLauncher(Func<BinaryResolution>? resolver = null) : IBrowserLauncher
{
    private readonly Func<BinaryResolution> _resolve = resolver ?? (() => ChromiumBinary.Resolve());

    public async Task<ILaunchedContext> LaunchPersistentContextAsync(
        string userDataDir, LaunchRequest request, CancellationToken ct)
    {
        // An override means the badge layer wants a stub launched in the browser's
        // place; it is already an absolute path to something runnable.
        string executable;
        if (!string.IsNullOrWhiteSpace(request.ExecutableOverride))
        {
            executable = request.ExecutableOverride!;
        }
        else
        {
            var resolved = _resolve();
            if (!resolved.Found) throw new BrowserNotFoundException(resolved.Error!);
            executable = resolved.Path!;
        }

        Directory.CreateDirectory(userDataDir);

        // Built before the process starts because an authenticated HTTP proxy needs
        // its relay listening before the browser makes its first request. Owned by
        // the context from here on, so the listener dies with the session.
        var proxy = request.Proxy is null
            ? new ProxyLaunch([], null)
            : ProxyArgs.Build(request.Proxy);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,

            // Chromium writes a great deal to stderr even on a healthy run. Left
            // attached to an unread pipe it eventually fills the buffer and the
            // browser blocks on its own logging, which presents as a window that
            // freezes minutes after opening.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in BuildArgs(userDataDir, request))
            psi.ArgumentList.Add(arg);

        foreach (var arg in proxy.Args)
            psi.ArgumentList.Add(arg);

        // The wrapper reads the licence from the environment. Passed per-process
        // rather than set globally so a key never leaks into unrelated children.
        if (!string.IsNullOrWhiteSpace(request.LicenseKey))
            psi.Environment["CLOAKBROWSER_LICENSE_KEY"] = request.LicenseKey!;

        // Timezone is an environment variable, not a flag: ICU reads TZ at process
        // start, which is what Date and Intl report. Setting it per-process is also
        // what makes two profiles in different timezones possible at once — changing
        // it globally would move the Hub itself and every other session with it.
        if (!string.IsNullOrWhiteSpace(request.Timezone))
            psi.Environment["TZ"] = request.Timezone!;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
        {
            process.Dispose();
            proxy.Dispose();
            throw new InvalidOperationException($"Could not start {executable}.");
        }

        // Drained continuously and discarded. Reading them is what keeps the pipes
        // from filling; keeping them is not useful, since Chromium's own noise would
        // bury anything worth showing the user.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var context = new ProcessContext(process, proxy);

        // A browser that dies immediately has almost always failed on its arguments
        // or a missing shared library, and reporting that now — rather than showing
        // a running session with no window — is the difference between a fixable
        // error and a mystery.
        await Task.Delay(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);

        if (process.HasExited && process.ExitCode != 0)
        {
            var code = process.ExitCode;
            await context.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The browser exited immediately (code {code}). " +
                "This usually means a bad launch flag or a missing system library.");
        }

        return context;
    }

    /// <summary>
    /// Assemble the command line.
    /// <para>
    /// Internal so the exact argument list can be asserted in tests: these flags are
    /// the entire fingerprint, and a silently dropped one is a profile that is not
    /// protected while the UI says it is.
    /// </para>
    /// </summary>
    internal static List<string> BuildArgs(string userDataDir, LaunchRequest request)
    {
        var args = new List<string> { $"--user-data-dir={userDataDir}" };

        // Already-resolved fingerprint, privacy and sandbox flags.
        args.AddRange(request.Args);

        if (request.Headless) args.Add("--headless=new");

        // Timezone is deliberately absent from the argument list: ICU reads TZ at
        // process start, so it is applied as an environment variable by the caller.
        if (!string.IsNullOrWhiteSpace(request.Locale))
        {
            args.Add($"--lang={request.Locale}");

            // Without this the Accept-Language header keeps the host's languages
            // while JavaScript reports the spoofed one — a mismatch that is trivial
            // to test for and points straight at a spoofed profile.
            args.Add($"--accept-lang={request.Locale}");
        }

        if (!string.IsNullOrWhiteSpace(request.UserAgent))
            args.Add($"--user-agent={request.UserAgent}");

        if (request.ExtensionPaths.Count > 0)
            args.Add($"--load-extension={string.Join(",", request.ExtensionPaths)}");

        // First run and default-browser prompts would otherwise steal focus on every
        // launch of a fresh profile, and the default-browser check writes state that
        // differs between profiles.
        args.Add("--no-first-run");
        args.Add("--no-default-browser-check");

        return args;
    }

    /// <summary>Free localhost TCP port, for remote debugging when enabled.</summary>
    internal static int FreePort()
    {
        // Binding port 0 and reading back the assignment is the only race-free way to
        // do this. Choosing a number and hoping would collide as soon as two profiles
        // start together, and Chromium would fail with "address in use".
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// No browser is installed.
/// <para>
/// Distinct from a general launch failure so the UI can offer the install command
/// instead of showing a stack trace for what is really a first-run state.
/// </para>
/// </summary>
public sealed class BrowserNotFoundException(string message) : Exception(message);

/// <summary>A running browser process, exposed as a session handle.</summary>
internal sealed class ProcessContext : ILaunchedContext
{
    private readonly Process _process;

    /// <summary>
    /// The proxy relay, when the session needed one.
    /// <para>
    /// Held here so its lifetime is exactly the session's. A relay that outlived the
    /// browser would be a listening socket still holding the user's proxy
    /// credentials, with nothing left to use it.
    /// </para>
    /// </summary>
    private readonly ProxyLaunch _proxy;

    private int _disposed;

    public ProcessContext(Process process, ProxyLaunch proxy)
    {
        _process = process;
        _proxy = proxy;

        // Raised for any exit, including the user closing the last window, which is a
        // perfectly normal way to end a session and must be treated like Stop so
        // cookies are still written back.
        _process.Exited += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Closed;

    /// <summary>
    /// Native window handle, Windows only.
    /// <para>
    /// Zero elsewhere, and zero on Windows until the window exists. The badge layer
    /// treats zero as "no handle yet" rather than an error, because taskbar badging
    /// is cosmetic and must never fail a launch.
    /// </para>
    /// </summary>
    public IntPtr MainWindowHandle
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return IntPtr.Zero;

            try
            {
                if (_process.HasExited) return IntPtr.Zero;
                _process.Refresh();
                return _process.MainWindowHandle;
            }
            catch
            {
                // Racing a process that is exiting. Not worth reporting for a value
                // that is only used to decorate an icon.
                return IntPtr.Zero;
            }
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try
        {
            if (_process.HasExited) return;

            // Ask first. Chromium flushes cookies, localStorage and session state on a
            // clean shutdown; killing it outright loses whatever has not hit disk,
            // which for an account profile can mean a session token.
            try
            {
                if (!_process.CloseMainWindow())
                {
                    // No window to close (headless, or it never appeared).
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the request.
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A hung renderer must not leave an orphan holding the profile's
                // singleton lock, which would make the profile unlaunchable until the
                // process was killed by hand.
                try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }
        finally
        {
            _process.Dispose();
            _proxy.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
