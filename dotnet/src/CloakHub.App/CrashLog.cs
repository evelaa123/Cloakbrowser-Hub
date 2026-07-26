using CloakHub.Core.Platform;

namespace CloakHub.App;

/// <summary>
/// Last-resort crash reporting.
/// <para>
/// A desktop GUI process that throws before its first window has nowhere to
/// display the error: on Windows a double-clicked executable has no console, so
/// the app simply disappears. This writes the exception where a user can be asked
/// to find it, which is the difference between an actionable bug report and "it
/// doesn't start".
/// </para>
/// </summary>
public static class CrashLog
{
    /// <summary>Where the log goes. In the Hub data directory, next to the profiles.</summary>
    public static string Path => System.IO.Path.Combine(HostOs.HubDataDir(), "crash.log");

    /// <summary>
    /// Append an exception, with a timestamp and environment context.
    /// <para>
    /// Appends rather than overwrites: an intermittent startup crash is diagnosed by
    /// comparing attempts, and keeping only the newest would throw away the pattern.
    /// </para>
    /// </summary>
    public static void Write(Exception ex)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // The OS and runtime are recorded because the crashes worth logging here
            // are overwhelmingly platform-specific -- a missing native dependency, a
            // display server that is not there -- and that context is exactly what a
            // user cannot be relied on to report.
            var entry =
                $"""
                ===============================================================
                {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
                OS      : {Environment.OSVersion}
                Runtime : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}
                Arch    : {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}
                Display : {DescribeDisplay()}

                {ex}

                """;

            File.AppendAllText(Path, entry);
        }
        catch
        {
            // Swallowed deliberately. This runs while the app is already failing, and
            // an exception from the crash handler would replace the original one --
            // losing the only useful information in the process.
        }
    }

    /// <summary>
    /// Whether a display server appears to be present.
    /// <para>
    /// The single most common cause of a Linux startup failure for a GUI app is no
    /// <c>DISPLAY</c> or <c>WAYLAND_DISPLAY</c> -- over SSH, in a container, on a
    /// headless server. The resulting exception does not say so, so it is recorded
    /// explicitly.
    /// </para>
    /// </summary>
    private static string DescribeDisplay()
    {
        if (!OperatingSystem.IsLinux()) return "n/a";

        var x11 = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");

        if (!string.IsNullOrEmpty(wayland)) return $"wayland ({wayland})";
        if (!string.IsNullOrEmpty(x11)) return $"x11 ({x11})";
        return "NONE - no DISPLAY or WAYLAND_DISPLAY set";
    }
}
