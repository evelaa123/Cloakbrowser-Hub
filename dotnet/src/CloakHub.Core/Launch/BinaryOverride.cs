namespace CloakHub.Core.Launch;

/// <summary>
/// Points the CloakBrowser launcher at a different executable for one launch.
/// <para>
/// This exists because of a real gap in the wrapper, established by inspecting
/// the assembly rather than assumed: <c>LaunchContextOptions</c> exposes no
/// <c>ExecutablePath</c> and no <c>Env</c>. The only supported way to redirect
/// the binary is the <c>CLOAKBROWSER_BINARY_PATH</c> environment variable, which
/// <c>Config.GetLocalBinaryOverride()</c> re-reads on every call (verified, not
/// inferred — the value can be changed between two calls and the second call
/// sees the new one).
/// </para>
/// <para>
/// That matters for instance badging, because two of the three strategies work by
/// launching something else: the macOS <c>.app</c> stub and the Windows shim.
/// Without a redirect the browser would start directly and neither the Dock nor
/// the taskbar would ever see the badged bundle.
/// </para>
/// <para>
/// <b>The catch, and why this type is shaped the way it is.</b> An environment
/// variable is per-process, not per-call. Two sessions launching concurrently
/// would race: profile A sets the variable, profile B overwrites it, and A ends
/// up running B's shim — which would badge the wrong window and, worse, point a
/// profile at another profile's launcher. So the redirect is serialised by a
/// process-wide gate held across the launch, and released only once the browser
/// has started. This narrows concurrent launches to one at a time; that is a real
/// cost, accepted because a wrong-binary launch is a correctness failure while a
/// slightly slower launch is not.
/// </para>
/// </summary>
public sealed class BinaryOverride : IAsyncDisposable
{
    /// <summary>Variable the wrapper reads. Found by probing the assembly.</summary>
    public const string EnvironmentVariable = "CLOAKBROWSER_BINARY_PATH";

    // A SemaphoreSlim rather than lock(), because the guarded region contains an
    // await: the browser launch itself. A monitor cannot be held across an await
    // and would throw on release from a different thread.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string? _previous;
    private bool _released;

    private BinaryOverride(string? previous) => _previous = previous;

    /// <summary>
    /// Take the gate and point the launcher at <paramref name="executable"/>.
    /// <para>
    /// Pass null to take the gate without redirecting. That is not a no-op and is
    /// deliberate: an unbadged launch still has to wait for any in-flight
    /// redirected launch to finish, otherwise it could observe that launch's
    /// variable and start the wrong binary.
    /// </para>
    /// </summary>
    public static async Task<BinaryOverride> AcquireAsync(
        string? executable, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);

        // Captured after the gate is held, so it cannot record another launch's
        // in-flight value and restore that instead of the user's real setting.
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var handle = new BinaryOverride(previous);

        try
        {
            if (executable is not null)
                Environment.SetEnvironmentVariable(EnvironmentVariable, executable);
            return handle;
        }
        catch
        {
            // Never leave the gate held on a failure to set the variable, or every
            // later launch in the process would block forever.
            handle.Release();
            throw;
        }
    }

    /// <summary>
    /// Restore the previous value and release the gate.
    /// <para>
    /// Must happen once the browser process exists, not when the session ends: the
    /// child has already inherited its executable by then, so continuing to hold
    /// the variable would serialise every launch behind the longest-lived session.
    /// </para>
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    private void Release()
    {
        // Idempotent: teardown can be reached from several paths (normal disposal,
        // the constructor's failure handler, an exception mid-launch), and a double
        // release would let two launches into the guarded region at once.
        if (_released) return;
        _released = true;

        try
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, _previous);
        }
        finally
        {
            // In a finally so a failure to restore the variable still frees the gate.
            // A stale override is recoverable; a permanently held gate is not.
            Gate.Release();
        }
    }
}
