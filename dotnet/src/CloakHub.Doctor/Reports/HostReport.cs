using System.Runtime.InteropServices;
using CloakHub.Core.Branding;
using CloakHub.Core.Launch;
using CloakHub.Core.Platform;
using CloakHub.Doctor.Console;

namespace CloakHub.Doctor.Reports;

/// <summary>
/// What this machine is, and what the Hub will therefore do on it.
/// </summary>
public static class HostReport
{
    public static void Run(BadgeOs os)
    {
        Output.Section("Host");

        Output.Item("Operating system", $"{HostOs.Describe(os)} ({RuntimeInformation.OSDescription.Trim()})");
        Output.Item("Architecture", RuntimeInformation.OSArchitecture.ToString());
        Output.Item("Process architecture", RuntimeInformation.ProcessArchitecture.ToString());
        Output.Item(".NET runtime", RuntimeInformation.FrameworkDescription);
        Output.Item("Logical processors", Environment.ProcessorCount.ToString());
        Output.Item("64-bit process", Environment.Is64BitProcess ? "yes" : "no");
        Output.Item("Hub data directory", HostOs.HubDataDir(os));

        if (os == BadgeOs.Other)
        {
            Output.Plain();
            Output.Warn("This OS is not one of the three the Hub targets.");
            Output.Paragraph(
                "Profiles, fingerprint flags and proxies will still work, because none of " +
                "them are platform-specific. Instance badging will not: it depends entirely " +
                "on per-OS window-manager behaviour, so it reports as unavailable rather " +
                "than pretending.");
        }

        ReportSandbox(os);
        ReportBaseIcon();
    }

    /// <summary>
    /// The sandbox decision, including the reason.
    /// <para>
    /// Worth printing even on Windows and macOS, where the answer is always
    /// "kept": the interesting case is a user who read that the Hub sometimes
    /// passes <c>--no-sandbox</c> and wants to confirm it is not doing so on
    /// their machine. A report that only mentions the flag when it is present
    /// cannot answer that question.
    /// </para>
    /// </summary>
    private static void ReportSandbox(BadgeOs os)
    {
        Output.Section("Chromium sandbox");

        var forced = SandboxArgs.NoSandboxOverride();
        var decision = SandboxArgs.Resolve(
            os == BadgeOs.Linux,
            new SandboxArgs.Probe { ForceNoSandbox = forced });

        Output.Item("Sandbox", decision.Disabled ? "DISABLED" : "enabled");
        Output.Item("Flags added", decision.Args.Count == 0 ? "(none)" : string.Join(" ", decision.Args));

        if (os == BadgeOs.Linux)
        {
            var userns = SandboxArgs.UnprivilegedUsernsAllowed();
            Output.Item("Unprivileged userns", userns switch
            {
                true => "allowed",
                false => "blocked by kernel",
                null => "undetermined (assumed allowed)",
            });
            Output.Item("Looks containerised", SandboxArgs.LooksContainerised() ? "yes" : "no");
        }

        Output.Plain();
        if (decision.Disabled) Output.Warn(decision.Reason);
        else Output.Ok(decision.Reason);
    }

    /// <summary>
    /// Whether the embedded base icon loaded.
    /// <para>
    /// Checked separately from badge rendering because the two fail for different
    /// reasons and have different fixes: a missing resource is a packaging bug,
    /// an unreadable one is a corrupt build. Badges still render either way, so
    /// without this check the user would see a plain badge and have no way to
    /// tell it was not intentional.
    /// </para>
    /// </summary>
    private static void ReportBaseIcon()
    {
        Output.Section("Base icon");

        var bytes = AppIcon.Bytes;
        if (bytes is null)
        {
            Output.Warn($"Embedded resource {AppIcon.ResourceName} is missing.");
            Output.Paragraph(
                "Badges will still be generated, drawn on a transparent field instead of " +
                "the Hub icon. The number stays readable, so this is cosmetic — but it " +
                "means the build was packaged without its icon, which is worth fixing.");

            var available = AppIcon.AvailableResources();
            Output.Item("Resources found", available.Count == 0 ? "(none)" : string.Join(", ", available));
            return;
        }

        Output.Ok($"Loaded {bytes.Length:N0} bytes from {AppIcon.ResourceName}.");
    }
}
