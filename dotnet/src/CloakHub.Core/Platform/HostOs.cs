using System.Runtime.InteropServices;
using CloakHub.Core.Branding;
using CloakHub.Core.Network;

namespace CloakHub.Core.Platform;

/// <summary>
/// The one place that asks the runtime which OS this is.
/// <para>
/// Every decision that varies by platform — badge strategy, MAC command,
/// sandbox flags — takes the OS as a parameter so it can be unit-tested for all
/// branches on a single machine. That design only pays off if exactly one
/// component does the actual detection; otherwise the enum mapping gets
/// duplicated and the copies drift. This is that component.
/// </para>
/// </summary>
public static class HostOs
{
    /// <summary>Detected host OS, as the branding layer names it.</summary>
    public static BadgeOs Current =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? BadgeOs.Windows :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? BadgeOs.Linux :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? BadgeOs.MacOs :
        BadgeOs.Other;

    /// <summary>
    /// The same value in the vocabulary the network layer uses.
    /// <para>
    /// Two enums for one concept is not ideal, but the alternative is worse: it
    /// would make <c>CloakHub.Core.Network</c> depend on
    /// <c>CloakHub.Core.Branding</c> purely to borrow an enum, coupling MAC
    /// handling to icon rendering for no reason. A total mapping in one method
    /// is the cheaper compromise.
    /// </para>
    /// </summary>
    public static BadgeOsLike ToOsLike(BadgeOs os) => os switch
    {
        BadgeOs.Windows => BadgeOsLike.Windows,
        BadgeOs.Linux => BadgeOsLike.Linux,
        BadgeOs.MacOs => BadgeOsLike.MacOs,
        _ => BadgeOsLike.Other,
    };

    /// <summary>Human-readable name for the detected OS.</summary>
    public static string Describe(BadgeOs os) => os switch
    {
        BadgeOs.Windows => "Windows",
        BadgeOs.Linux => "Linux",
        BadgeOs.MacOs => "macOS",
        _ => "unrecognised",
    };

    /// <summary>Conventional icon extension for the host.</summary>
    public static string IconExtension(BadgeOs os) => os switch
    {
        BadgeOs.Windows => ".ico",
        BadgeOs.MacOs => ".icns",
        _ => ".png",
    };

    /// <summary>
    /// Where per-user application data belongs on this OS.
    /// <para>
    /// <c>SpecialFolder.ApplicationData</c> resolves to <c>%APPDATA%</c> on
    /// Windows and <c>~/.config</c> on Linux, both correct. On macOS .NET maps it
    /// to <c>~/.config</c> too, which is not where a Mac application should
    /// write, so that case is handled explicitly.
    /// </para>
    /// </summary>
    public static string AppDataRoot(BadgeOs? os = null)
    {
        var target = os ?? Current;
        if (target == BadgeOs.MacOs)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : appData;
    }

    /// <summary>Default data directory for the Hub itself.</summary>
    public static string HubDataDir(BadgeOs? os = null) =>
        Path.Combine(AppDataRoot(os), "CloakBrowserHub");

    /// <summary>
    /// The launcher stub shipped alongside the Hub, or null when it is absent.
    /// <para>
    /// Windows badging has two tiers and the difference is visible to the user, so
    /// this must answer honestly rather than optimistically: with a stub the Hub
    /// writes a per-profile <c>.exe</c> carrying the badged icon; without one it
    /// can only overlay the live taskbar button. Returning a path that does not
    /// exist would make the planner pick the shim and then fail at write time,
    /// which is exactly the silent-degradation failure the plan's Reason field
    /// exists to prevent.
    /// </para>
    /// </summary>
    public static string? FindLauncherStub(string? baseDirectory = null)
    {
        if (Current != BadgeOs.Windows) return null;

        var root = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(root, "CloakHub.Launcher.exe"),
                     Path.Combine(root, "stubs", "CloakHub.Launcher.exe"),
                 })
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* unreadable path — treat as absent */ }
        }

        return null;
    }
}
