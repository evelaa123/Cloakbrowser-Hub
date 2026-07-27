using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CloakHub.App.Services;
using CloakHub.Core.Licensing;
using CloakHub.Core.Model;
using CloakHub.Core.Platform;
using CloakHub.Core.Storage;

namespace CloakHub.App.ViewModels;

/// <summary>
/// Application settings.
/// <para>
/// Every property writes through to disk on set — there is no Save button, matching
/// the Electron build. Each setting is independent, so a form-wide save would only
/// create a window in which the UI and the file disagree, and the user would have no
/// way to tell which one the next launch will use.
/// </para>
/// </summary>
public sealed class SettingsPageViewModel : ViewModelBase
{
    private readonly SettingsStore _settings;
    private readonly HubPaths _paths;
    private readonly ToastHost _toasts;
    private readonly Action<AppTheme> _onThemeChanged;
    private readonly Action<double> _onZoomChanged;

    /// <summary>
    /// Called when the automation port, token or enabled flag changes.
    /// <para>
    /// The listener is bound with the values it started with, so a saved setting is
    /// not a live one. Without this the UI would show the new port while scripts kept
    /// reaching the old one — a discrepancy with no visible cause.
    /// </para>
    /// </summary>
    private readonly Action _onAutomationChanged;

    /// <summary>
    /// Suppresses write-through while the view model is populating its own fields.
    /// <para>
    /// Without it, assigning the loaded values in the constructor would each trigger
    /// a save, so simply opening the page would rewrite settings.json — and on a file
    /// that had just been normalised, that write would race the one the store already
    /// did.
    /// </para>
    /// </summary>
    private bool _loading;

    public SettingsPageViewModel(
        SettingsStore settings,
        HubPaths paths,
        ToastHost toasts,
        Action<AppTheme> onThemeChanged,
        Action<double> onZoomChanged,
        Action onAutomationChanged)
    {
        _settings = settings;
        _paths = paths;
        _toasts = toasts;
        _onThemeChanged = onThemeChanged;
        _onZoomChanged = onZoomChanged;
        _onAutomationChanged = onAutomationChanged;

        OpenProfilesFolderCommand = new RelayCommand(() => Reveal(EffectiveProfilesDir));
        OpenDataFolderCommand = new RelayCommand(() => Reveal(_paths.Root));
        ResetProfilesDirCommand = new RelayCommand(ResetProfilesDir);
        ChangeProfilesDirCommand = new AsyncRelayCommand(ChangeProfilesDirAsync, onError: toasts.Error);
        RegenerateTokenCommand = new RelayCommand(RegenerateToken);

        Reload();
    }

    /// <summary>
    /// Supplies a native folder picker.
    /// <para>
    /// Injected by the view rather than called directly, because Avalonia's picker
    /// hangs off the <c>TopLevel</c> and reaching for a window from a view model both
    /// couples the two and breaks the moment this runs without one (a unit test, or
    /// the XAML previewer).
    /// </para>
    /// </summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    // ------------------------------------------------------------------
    // Appearance
    // ------------------------------------------------------------------

    private AppTheme _theme;
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (!SetField(ref _theme, value) || _loading) return;
            Save(s => s with { Theme = value });

            // Applied immediately, not on restart. The whole point of a theme control
            // is that the user can see what they picked; deferring it would make the
            // setting look broken.
            _onThemeChanged(value);
        }
    }

    /// <summary>Theme choices, for the combo box.</summary>
    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.Dark, AppTheme.Light];

    /// <summary>
    /// Discrete zoom steps rather than a free-entry number.
    /// <para>
    /// A slider or text box invites values between steps that make the layout look
    /// subtly wrong — 13px type at 1.07 scale lands on a half pixel and blurs. These
    /// are the same steps the Electron build offered.
    /// </para>
    /// </summary>
    public IReadOnlyList<double> ZoomSteps { get; } = [0.8, 0.9, 1.0, 1.1, 1.25, 1.4, 1.6];

    private double _uiZoom = 1.0;
    public double UiZoom
    {
        get => _uiZoom;
        set
        {
            // Snapped before storing, so a value arriving from a hand-edited file does
            // not become the selected item of a combo box that has no such entry --
            // which would leave the control blank.
            var snapped = SnapZoom(value);
            if (!SetField(ref _uiZoom, snapped) || _loading) return;
            Save(s => s with { UiZoom = snapped });
            OnPropertyChanged(nameof(ZoomLabel));

            // Applied immediately, not on next launch. Choosing a size and seeing
            // nothing happen reads as a broken setting, and the whole point of the
            // control is judging the result by eye.
            _onZoomChanged(snapped);
        }
    }

    public string ZoomLabel => $"{(int)Math.Round(_uiZoom * 100)}%";

    /// <summary>Nearest offered step, so the combo box always has a match.</summary>
    private double SnapZoom(double value)
    {
        if (!double.IsFinite(value)) return 1.0;
        var clamped = Math.Clamp(value, AppSettings.MinZoom, AppSettings.MaxZoom);
        return ZoomSteps.OrderBy(z => Math.Abs(z - clamped)).First();
    }

    // ------------------------------------------------------------------
    // Sessions
    // ------------------------------------------------------------------

    private int _maxConcurrentSessions = 5;
    public int MaxConcurrentSessions
    {
        get => _maxConcurrentSessions;
        set
        {
            // Clamped on the way in rather than rejected. A NumericUpDown can emit 0
            // mid-edit (the user cleared the box), and refusing it would leave the
            // control showing a number the app is not using.
            var clamped = Math.Clamp(value, 1, SessionLimit.MaxPreference);
            if (!SetField(ref _maxConcurrentSessions, clamped) || _loading) return;

            Save(s => s with { MaxConcurrentSessions = clamped });
            OnPropertyChanged(nameof(SessionLimitHint));

            if (clamped != value)
            {
                _toasts.Info($"The maximum is {SessionLimit.MaxPreference}, so the limit was set to that.");
            }
        }
    }

    /// <summary>
    /// Explains which constraint will actually bind at launch.
    /// <para>
    /// Uses the same resolver the launch path enforces with, so the number shown here
    /// and the number that binds can never disagree. The licence seat count is not
    /// known yet in this build, so <c>planSeats</c> is null — and the resolver's
    /// deliberate behaviour there is to fall back to the preference rather than guess,
    /// which is what this text says.
    /// </para>
    /// </summary>
    public string SessionLimitHint
    {
        get
        {
            var resolved = SessionLimit.Resolve(_maxConcurrentSessions, planSeats: null);
            return $"Enforced limit: {resolved.Limit} — {resolved.Reason}.";
        }
    }

    public IReadOnlyList<FingerprintPlatform> Platforms { get; } =
        [FingerprintPlatform.Windows, FingerprintPlatform.Macos, FingerprintPlatform.Linux];

    private FingerprintPlatform _defaultPlatform = FingerprintPlatform.Windows;
    public FingerprintPlatform DefaultPlatform
    {
        get => _defaultPlatform;
        set
        {
            if (!SetField(ref _defaultPlatform, value) || _loading) return;
            Save(s => s with { DefaultPlatform = value });
        }
    }

    private bool _saveCookiesOnClose = true;
    public bool SaveCookiesOnClose
    {
        get => _saveCookiesOnClose;
        set
        {
            if (!SetField(ref _saveCookiesOnClose, value) || _loading) return;
            Save(s => s with { SaveCookiesOnClose = value });
        }
    }

    private bool _closeSessionsOnQuit;
    public bool CloseSessionsOnQuit
    {
        get => _closeSessionsOnQuit;
        set
        {
            if (!SetField(ref _closeSessionsOnQuit, value) || _loading) return;
            Save(s => s with { CloseSessionsOnQuit = value });
        }
    }

    // ------------------------------------------------------------------
    // Browser binary
    // ------------------------------------------------------------------

    public IReadOnlyList<ReleaseChannel> Channels { get; } =
        [ReleaseChannel.Stable, ReleaseChannel.Preview];

    private ReleaseChannel _releaseChannel = ReleaseChannel.Stable;
    public ReleaseChannel ReleaseChannel
    {
        get => _releaseChannel;
        set
        {
            if (!SetField(ref _releaseChannel, value) || _loading) return;
            Save(s => s with { ReleaseChannel = value });
            OnPropertyChanged(nameof(IsPreviewChannel));
        }
    }

    public bool IsPreviewChannel => _releaseChannel == ReleaseChannel.Preview;

    /// <summary>
    /// Pinned browser version, as text.
    /// <para>
    /// Not written through on every keystroke, unlike everything else on this page.
    /// A partially-typed version string is a different pin from the finished one, and
    /// saving "12" on the way to "124.0.6367.60" would have the app briefly believe a
    /// build that does not exist. Committed by <see cref="ApplyVersion"/> instead.
    /// </para>
    /// </summary>
    private string _browserVersionDraft = "";
    public string BrowserVersionDraft
    {
        get => _browserVersionDraft;
        set => SetField(ref _browserVersionDraft, value);
    }

    public void ApplyVersion()
    {
        var trimmed = _browserVersionDraft.Trim();
        Save(s => s with { BrowserVersion = trimmed.Length == 0 ? null : trimmed });

        _toasts.Success(trimmed.Length == 0
            ? "Version unpinned — sessions will use the newest build."
            : $"Pinned to {trimmed}.");
    }

    // ------------------------------------------------------------------
    // Storage
    // ------------------------------------------------------------------

    private string? _profilesDir;

    /// <summary>The override, or null when the default is in use.</summary>
    public string? ProfilesDir
    {
        get => _profilesDir;
        private set
        {
            if (!SetField(ref _profilesDir, value)) return;
            OnPropertyChanged(nameof(EffectiveProfilesDir));
            OnPropertyChanged(nameof(HasCustomProfilesDir));
        }
    }

    /// <summary>The path actually used, resolved through the same helper as launches.</summary>
    public string EffectiveProfilesDir => _paths.ProfileDataDir(_settings.Current);

    public bool HasCustomProfilesDir => !string.IsNullOrWhiteSpace(_profilesDir);

    public string DataDir => _paths.Root;

    public RelayCommand OpenProfilesFolderCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand ResetProfilesDirCommand { get; }
    public AsyncRelayCommand ChangeProfilesDirCommand { get; }

    private async Task ChangeProfilesDirAsync()
    {
        if (FolderPicker is null)
        {
            _toasts.Error("No folder picker is available.");
            return;
        }

        var picked = await FolderPicker();
        if (string.IsNullOrWhiteSpace(picked)) return;   // cancelled

        Save(s => s with { ProfilesDir = picked });
        ProfilesDir = _settings.Current.ProfilesDir;

        // Says plainly that nothing was moved. Silently changing where the app looks
        // would make every existing profile appear to have lost its cookies and
        // logins, which reads as data loss rather than as a settings change.
        _toasts.Warning(
            "Profiles directory changed. Existing profile folders were not moved — " +
            "copy them across yourself if you need their data.");
    }

    private void ResetProfilesDir()
    {
        Save(s => s with { ProfilesDir = null });
        ProfilesDir = null;
        _toasts.Info("Profiles directory reset to the default.");
    }

    /// <summary>
    /// Open a folder in the system file manager.
    /// <para>
    /// The directory is created first: it does not exist until the first profile
    /// launches, and asking the OS to reveal a missing path produces a platform-
    /// specific error rather than a useful one.
    /// </para>
    /// </summary>
    private void Reveal(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            // UseShellExecute is what makes a directory path open in Explorer, Finder
            // or the desktop's file manager; without it .NET tries to execute the path
            // as a program and fails.
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A headless or minimal Linux install has no file manager at all, which is
            // a normal condition here rather than a bug — the path is still useful, so
            // it is shown instead.
            _toasts.Error($"Could not open {path}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Automation
    // ------------------------------------------------------------------

    private bool _automationEnabled;
    public bool AutomationEnabled
    {
        get => _automationEnabled;
        set
        {
            if (!SetField(ref _automationEnabled, value) || _loading) return;

            Save(s => s with { Automation = s.Automation with { Enabled = value } });

            // Re-read rather than assume: enabling with a blank token makes the store
            // generate one, and the field must show the real value or the user will
            // copy an empty string into their script.
            AutomationToken = _settings.Current.Automation.Token;

            _onAutomationChanged();
        }
    }

    private int _automationPort = 7317;
    public int AutomationPort
    {
        get => _automationPort;
        set
        {
            var clamped = value is >= 1024 and <= 65535 ? value : 7317;
            if (!SetField(ref _automationPort, clamped) || _loading) return;
            Save(s => s with { Automation = s.Automation with { Port = clamped } });

            // The listener is bound to the old port until it is restarted, so without
            // this the setting would appear to apply and scripts would keep reaching
            // the previous one.
            _onAutomationChanged();
        }
    }

    private string _automationToken = "";
    public string AutomationToken
    {
        get => _automationToken;
        private set => SetField(ref _automationToken, value);
    }

    public RelayCommand RegenerateTokenCommand { get; }

    private void RegenerateToken()
    {
        var token = AutomationSettings.NewToken();
        Save(s => s with { Automation = s.Automation with { Token = token } });
        AutomationToken = _settings.Current.Automation.Token;

        // The running server holds the old token in memory, so it has to be told.
        _onAutomationChanged();

        // Warned rather than merely confirmed: the old token stops working the moment
        // this is written, so any script already holding it starts failing with a 401
        // and the cause would be invisible from the script's side.
        _toasts.Warning("A new token was generated. Scripts using the old one will be rejected.");
    }

    /// <summary>The base URL to paste into a script, so it need not be assembled by hand.</summary>
    public string AutomationUrl => $"http://127.0.0.1:{_automationPort}";

    // ------------------------------------------------------------------
    // About
    // ------------------------------------------------------------------

    public string VersionLabel =>
        typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string PlatformLabel =>
        $"{HostOs.Describe(HostOs.Current)} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";

    public string RuntimeLabel => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    // ------------------------------------------------------------------
    // Load / save
    // ------------------------------------------------------------------

    /// <summary>Pull every field from the store, without writing anything back.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;

            Theme = s.Theme;
            UiZoom = s.UiZoom;
            MaxConcurrentSessions = s.MaxConcurrentSessions;
            DefaultPlatform = s.DefaultPlatform;
            SaveCookiesOnClose = s.SaveCookiesOnClose;
            CloseSessionsOnQuit = s.CloseSessionsOnQuit;
            ReleaseChannel = s.ReleaseChannel;
            BrowserVersionDraft = s.BrowserVersion ?? "";
            ProfilesDir = s.ProfilesDir;
            AutomationEnabled = s.Automation.Enabled;
            AutomationPort = s.Automation.Port;
            AutomationToken = s.Automation.Token;
        }
        finally
        {
            // In a finally so a throw part-way through cannot leave the page
            // permanently unable to save — every setter would silently no-op.
            _loading = false;
        }

        OnPropertyChanged(nameof(SessionLimitHint));
        OnPropertyChanged(nameof(EffectiveProfilesDir));
        OnPropertyChanged(nameof(IsPreviewChannel));
        OnPropertyChanged(nameof(AutomationUrl));
        OnPropertyChanged(nameof(ZoomLabel));
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        try
        {
            _settings.Update(change);
        }
        catch (Exception ex)
        {
            // Reported, because a failed settings write is invisible otherwise: the UI
            // already shows the new value, so the user believes it was saved and only
            // finds out on the next launch when it reverts.
            _toasts.Error($"Could not save settings: {ex.Message}");
        }
    }
}
