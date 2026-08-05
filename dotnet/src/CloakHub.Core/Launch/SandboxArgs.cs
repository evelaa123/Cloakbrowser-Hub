using System.Globalization;
using System.Runtime.InteropServices;

namespace CloakHub.Core.Launch;

/// <summary>
/// Whether a launch needs <c>--no-sandbox</c>, and the flags that hide the infobar.
/// <para>
/// The reported symptom was cosmetic — Chromium showing "You are using an
/// unsupported command-line flag: --no-sandbox" on every launch. The cause was
/// not: the wrapper hardcodes <c>--no-sandbox</c> in its default stealth args,
/// so every session ran with Chromium's renderer sandbox switched off.
/// </para>
/// <para>That matters beyond the yellow bar:</para>
/// <list type="bullet">
///   <item>The sandbox is the boundary that stops a compromised renderer reading
///   the rest of the profile directory — cookies, saved passwords, tokens.
///   Anti-detect profiles exist precisely to hold valuable logged-in sessions,
///   so disabling it is a poor default for this workload specifically.</item>
///   <item>The infobar is itself a fingerprinting signal. It changes the window's
///   inner height by ~40px, so <c>innerHeight</c> no longer matches what a real
///   maximised Chrome on the spoofed screen size would report. A profile that
///   carefully spoofs a 1920x1080 desktop and then reports an off-by-40 viewport
///   is <i>more</i> identifiable than one that does not bother.</item>
/// </list>
/// <para>
/// Where the flag is genuinely needed: Linux without user namespaces. Chromium's
/// sandbox needs either unprivileged <c>CLONE_NEWUSER</c> or a setuid helper.
/// Inside a container, or on a kernel with
/// <c>kernel.unprivileged_userns_clone=0</c>, neither exists and Chromium exits
/// immediately. Refusing to launch there in the name of security would just be a
/// broken app, so the flag is kept — paired with <c>--test-type</c>, which is
/// what actually removes the infobar.
/// </para>
/// </summary>
public static class SandboxArgs
{
    public sealed record Decision(List<string> Args, bool Disabled, string Reason);

    /// <summary>Probe hooks, injectable so the decision logic is testable off-Linux.</summary>
    public sealed record Probe
    {
        public Func<bool?>? UsernsAllowed { get; init; }
        public Func<bool>? Containerised { get; init; }
        public bool ForceNoSandbox { get; init; }
    }

    /// <summary>
    /// Read whether this kernel permits the unprivileged user namespaces
    /// Chromium's sandbox depends on.
    /// <para>
    /// Returns <c>null</c> when the answer cannot be determined (the sysctl is
    /// absent on kernels that always allow it), which the caller treats as
    /// "allowed" — guessing "blocked" would disable the sandbox on machines that
    /// support it perfectly well.
    /// </para>
    /// </summary>
    public static bool? UnprivilegedUsernsAllowed()
    {
        // Debian/Ubuntu-specific knob; absent where the feature is always on.
        try
        {
            const string knob = "/proc/sys/kernel/unprivileged_userns_clone";
            if (File.Exists(knob))
                return File.ReadAllText(knob).Trim() != "0";
        }
        catch { /* unreadable — fall through */ }

        // Present on all modern kernels; 0 means user namespaces are unavailable.
        try
        {
            const string max = "/proc/sys/user/max_user_namespaces";
            if (File.Exists(max) &&
                int.TryParse(File.ReadAllText(max).Trim(), CultureInfo.InvariantCulture, out var n))
                return n > 0;
        }
        catch { /* unreadable — fall through */ }

        return null;
    }

    /// <summary>True when the process looks like it is running inside a container.</summary>
    public static bool LooksContainerised()
    {
        try
        {
            if (File.Exists("/.dockerenv")) return true;
        }
        catch { /* ignore */ }

        try
        {
            // A container runtime leaves its name in the cgroup path.
            var cgroup = File.ReadAllText("/proc/1/cgroup");
            foreach (var marker in new[] { "docker", "kubepods", "containerd", "lxc", "podman" })
                if (cgroup.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { /* not Linux, or no permission */ }

        return false;
    }

    /// <summary>Decide the sandbox flags for this machine.</summary>
    /// <param name="isLinux">Injectable so the Linux branch is testable anywhere.</param>
    public static Decision Resolve(bool? isLinux = null, Probe? probe = null)
    {
        probe ??= new Probe();
        isLinux ??= RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        // An explicit escape hatch, because no amount of probing beats a user who
        // knows their own machine and just needs the browser to start.
        if (probe.ForceNoSandbox)
            return new Decision(["--no-sandbox", "--test-type"], true,
                "Sandbox disabled by CLOAKBROWSER_HUB_NO_SANDBOX=1. The infobar is suppressed, but the " +
                "renderer runs unsandboxed — unset the variable once the launch problem is resolved.");

        // Windows and macOS ship a working sandbox with no kernel prerequisites,
        // so there is never a reason to disable it there.
        if (isLinux != true)
            return new Decision([], false, "Renderer sandbox enabled.");

        var userns = (probe.UsernsAllowed ?? UnprivilegedUsernsAllowed)();
        var contained = (probe.Containerised ?? LooksContainerised)();

        // Only an explicit false forces the flag. null means the sysctl is absent,
        // which on a modern kernel means user namespaces are always available.
        if (userns == false)
            return new Decision(["--no-sandbox", "--test-type"], true,
                "This kernel does not allow unprivileged user namespaces, which Chromium's sandbox " +
                "requires, so the session runs with --no-sandbox. The infobar is suppressed via --test-type.");

        if (contained)
            // Containers usually mask the sandbox even when the sysctl looks
            // permissive (seccomp profile, missing CAP_SYS_ADMIN). Chromium
            // failing to start is a worse outcome than a documented downgrade.
            return new Decision(["--no-sandbox", "--test-type"], true,
                "Running inside a container, where Chromium's sandbox is usually unavailable, so the " +
                "session runs with --no-sandbox. The infobar is suppressed via --test-type.");

        return new Decision([], false,
            "Renderer sandbox enabled (no --no-sandbox needed on this machine).");
    }

    /// <summary>Read the override from the environment.</summary>
    public static bool NoSandboxOverride(IDictionary<string, string>? env = null)
    {
        var raw = env is not null
            ? (env.TryGetValue("CLOAKBROWSER_HUB_NO_SANDBOX", out var v) ? v : null)
            : Environment.GetEnvironmentVariable("CLOAKBROWSER_HUB_NO_SANDBOX");
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s is "1" or "true" or "yes";
    }
}
