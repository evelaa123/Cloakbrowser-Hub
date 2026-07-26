using Avalonia;

namespace CloakHub.App;

internal static class Program
{
    /// <summary>
    /// Entry point.
    /// <para>
    /// <c>STAThread</c> is required on Windows: the native file and folder pickers
    /// are COM apartment-threaded, so without it the first "choose a directory"
    /// in Settings throws instead of opening a dialogue. It is inert on Linux and
    /// macOS, which is why it is applied unconditionally rather than guarded.
    /// </para>
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            // A GUI app that dies before a window exists shows the user nothing at
            // all -- on Windows a double-clicked exe with no console simply vanishes.
            // Writing the reason somewhere findable is the difference between a bug
            // report and "it doesn't start".
            CrashLog.Write(ex);
            throw;
        }
    }

    // Named exactly this because the Avalonia XAML previewer looks it up by
    // convention; renaming it silently breaks design-time preview.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
