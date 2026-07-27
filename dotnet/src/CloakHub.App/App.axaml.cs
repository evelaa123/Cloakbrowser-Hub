using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CloakHub.App.Services;
using CloakHub.App.ViewModels;
using CloakHub.App.Views;
using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using CloakHub.Core.Platform;
using CloakHub.Core.Storage;

namespace CloakHub.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new HubPaths();
            var settings = new SettingsStore(paths.SettingsFile);
            var profiles = new ProfileStore(paths.ProfilesFile);
            var proxies = new ProxyStore(paths.ProxiesFile);

            // One session manager for the process. It owns the live browser handles
            // and the badge-number allocator, and a second instance would hand out
            // numbers the first one had already given away.
            var sessions = new SessionManager(
                new ChromiumLauncher(),
                new SessionPaths(paths, settings),
                HostOs.Current);

            // Applied before the window is constructed, so it opens in the saved theme
            // rather than flashing the default and then switching -- which is visible
            // and looks like a bug on a light-theme machine.
            ApplyTheme(settings.Current.Theme);

            var shell = new MainWindowViewModel(profiles, proxies, settings, paths, sessions);

            desktop.MainWindow = new MainWindow { DataContext = shell };

            // Sessions are not torn down here by default. The browsers are separate
            // processes, and a user closing the Hub window has not necessarily asked to
            // lose their open tabs; killing them would also skip the browser's own
            // shutdown, which is when it flushes cookies and session storage.
            desktop.ShutdownRequested += (_, _) => shell.OnShutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Map the stored theme onto Avalonia's variant.</summary>
    public static void ApplyTheme(AppTheme theme) =>
        Current!.RequestedThemeVariant = theme == AppTheme.Light
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
}
