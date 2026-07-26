using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CloakHub.App.Services;
using CloakHub.Core.Model;
using CloakHub.Core.Platform;
using CloakHub.Core.Storage;

namespace CloakHub.App.ViewModels;

/// <summary>The app's five screens. Mirrors the <c>Route</c> union in App.tsx.</summary>
public enum Route { Profiles, Proxies, Import, License, Settings }

/// <summary>
/// The shell: navigation, and the state several pages need at once.
/// <para>
/// Profiles, settings and licence live here rather than in each page because more
/// than one screen reads them — the sidebar shows a running count, the profiles
/// page shows a licence warning, the settings page edits the same object. The
/// Electron build kept them in one context for the same reason; a page-local copy
/// would let two screens disagree about what is stored.
/// </para>
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileStore _profiles;
    private readonly SettingsStore _settings;
    private readonly HubPaths _paths;

    public MainWindowViewModel(ProfileStore profiles, SettingsStore settings, HubPaths paths)
    {
        _profiles = profiles;
        _settings = settings;
        _paths = paths;

        Toasts = new ToastHost();

        ProfilesPage = new ProfilesPageViewModel(profiles, settings, paths, Toasts);
        SettingsPage = new SettingsPageViewModel(settings, paths, Toasts, OnThemeChanged, OnZoomChanged);

        // The window scales against this, so it has to start at the stored value --
        // otherwise the app opens at 100% and only jumps to the user's size when they
        // next visit the settings page.
        _uiZoom = settings.Current.UiZoom;

        NavigateCommand = new RelayCommand<Route>(Navigate);

        NavItems =
        [
            new NavItem(Route.Profiles, "Profiles", "\u25c9"),
            new NavItem(Route.Proxies, "Proxies", "\u21c4"),
            new NavItem(Route.Import, "Import", "\u2913"),
            new NavItem(Route.License, "License", "\u2726"),
            new NavItem(Route.Settings, "Settings", "\u2699"),
        ];

        // A corrupt file was quarantined during load. Surfaced immediately, because
        // from the user's side an empty profile list is indistinguishable from the app
        // having thrown their work away -- they need to know the bytes still exist and
        // where they are.
        if (profiles.Quarantined is { } q)
        {
            Toasts.Error(
                $"profiles.json could not be read and was moved to {Path.GetFileName(q)}. " +
                "Your data is still on disk; the file is in the Hub data folder.");
        }

        foreach (var note in profiles.MigrationNotes) Toasts.Info(note);

        Navigate(Route.Profiles);
    }

    public ToastHost Toasts { get; }
    public ProfilesPageViewModel ProfilesPage { get; }
    public SettingsPageViewModel SettingsPage { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Bound by each sidebar button, with the route as its parameter.</summary>
    public RelayCommand<Route> NavigateCommand { get; }

    private Route _route = Route.Profiles;
    public Route CurrentRoute
    {
        get => _route;
        private set
        {
            if (!SetField(ref _route, value)) return;
            OnPropertyChanged(nameof(IsProfiles));
            OnPropertyChanged(nameof(IsProxies));
            OnPropertyChanged(nameof(IsImport));
            OnPropertyChanged(nameof(IsLicense));
            OnPropertyChanged(nameof(IsSettings));
        }
    }

    // Discrete booleans rather than one converter, so the XAML can bind visibility
    // directly and a typo becomes a compile-time binding error instead of a screen
    // that silently never shows.
    public bool IsProfiles => _route == Route.Profiles;
    public bool IsProxies => _route == Route.Proxies;
    public bool IsImport => _route == Route.Import;
    public bool IsLicense => _route == Route.License;
    public bool IsSettings => _route == Route.Settings;

    public void Navigate(Route route)
    {
        CurrentRoute = route;
        foreach (var item in NavItems) item.IsActive = item.Route == route;

        // Refreshed on entry rather than kept live: the list can be changed by the
        // editor, by an import, or by a session ending, and re-reading on navigation
        // is far simpler than invalidating from every one of those paths.
        if (route == Route.Profiles) ProfilesPage.Refresh();
    }

    /// <summary>Running session count, for the sidebar badge.</summary>
    public int RunningCount => ProfilesPage.RunningCount;

    // ------------------------------------------------------------------
    // Sidebar footer
    // ------------------------------------------------------------------

    /// <summary>
    /// Licence tier label.
    /// <para>
    /// Reports "No key" rather than assuming a tier when nothing is activated. A
    /// licence lookup needs the network, and an offline launch must not silently
    /// present the app as unlicensed-but-fine or as paid.
    /// </para>
    /// </summary>
    public string TierLabel => File.Exists(_paths.LicenseFile) ? "Key present" : "No key";

    public bool HasLicenseKey => File.Exists(_paths.LicenseFile);

    /// <summary>Version, from the assembly rather than a hardcoded string.</summary>
    public string VersionLabel =>
        $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public string PlatformLabel => HostOs.Describe(HostOs.Current);

    /// <summary>
    /// The sidebar logo, or null when the asset is unavailable.
    /// <para>
    /// Exposed with <see cref="HasMark"/> rather than bound directly, so a missing
    /// asset collapses the Image instead of leaving a 28px hole beside the wordmark.
    /// </para>
    /// </summary>
    public Avalonia.Media.Imaging.Bitmap? Mark => Branding.Mark;

    public bool HasMark => Branding.Mark is not null;

    private void OnThemeChanged(AppTheme theme) => App.ApplyTheme(theme);

    /// <summary>
    /// Interface scale, bound by the window's <c>LayoutTransformControl</c>.
    /// <para>
    /// Lives on the shell rather than the settings page because the transform wraps
    /// the whole window, including the sidebar and every other page. Binding it to
    /// the settings view model would make the scale depend on which screen is open.
    /// </para>
    /// </summary>
    private double _uiZoom = 1.0;
    public double UiZoom
    {
        get => _uiZoom;
        private set => SetField(ref _uiZoom, value);
    }

    private void OnZoomChanged(double zoom) => UiZoom = zoom;

    /// <summary>
    /// Called as the app closes.
    /// <para>
    /// Honours <c>CloseSessionsOnQuit</c>. Off by default: the browsers are separate
    /// processes and closing the Hub window is not a request to lose open tabs.
    /// </para>
    /// </summary>
    public void OnShutdown()
    {
        if (_settings.Current.CloseSessionsOnQuit) ProfilesPage.StopAll();
    }
}

/// <summary>One sidebar entry.</summary>
public sealed class NavItem : ViewModelBase
{
    public NavItem(Route route, string label, string glyph)
    {
        Route = route;
        Label = label;
        Glyph = glyph;
    }

    public Route Route { get; }
    public string Label { get; }

    /// <summary>
    /// A Unicode glyph rather than an icon font or SVG.
    /// <para>
    /// Matches the Electron build, which used the same characters. They render from
    /// the system font on all three platforms with no asset to ship, and at 13px an
    /// icon would carry no more meaning than the glyph does.
    /// </para>
    /// </summary>
    public string Glyph { get; }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }
}
