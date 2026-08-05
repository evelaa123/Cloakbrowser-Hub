using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CloakHub.Core.Binaries;
using CloakHub.Core.Licensing;
using CloakHub.Core.Storage;

namespace CloakHub.App.ViewModels;

/// <summary>
/// Licence key and stealth browser binary.
/// <para>
/// The two share a page because from the user's side they are one decision: which
/// key you hold determines which build you get. Separating them is how "why is my
/// Chromium old?" becomes an unanswerable question — the cause would be on a screen
/// the user has no reason to connect to the symptom.
/// </para>
/// </summary>
public sealed class LicensePageViewModel : ViewModelBase, IDisposable
{
    private readonly LicenseService _license;
    private readonly BinaryInstaller _binaries;
    private readonly SettingsStore _settings;
    private readonly ToastHost _toasts;

    /// <summary>How many sessions this app currently has open, for the seat display.</summary>
    private readonly Func<int> _localSessions;

    /// <summary>
    /// Raised whenever the resolved tier changes.
    /// <para>
    /// The sidebar badge is on every screen, so it cannot poll this page. Without the
    /// callback the badge would keep saying "No key" until the next navigation, which
    /// makes a successful activation look like it did nothing.
    /// </para>
    /// </summary>
    private readonly Action _onChanged;

    /// <summary>Cancels an in-flight download when the user asks it to stop.</summary>
    private CancellationTokenSource? _download;

    public LicensePageViewModel(
        LicenseService license,
        BinaryInstaller binaries,
        SettingsStore settings,
        ToastHost toasts,
        Func<int> localSessions,
        Action onChanged)
    {
        _license = license;
        _binaries = binaries;
        _settings = settings;
        _toasts = toasts;
        _localSessions = localSessions;
        _onChanged = onChanged;

        ActivateCommand = new AsyncRelayCommand(ActivateAsync, () => !IsActivating, toasts.Error);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing, toasts.Error);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsDownloading, toasts.Error);
        CancelDownloadCommand = new RelayCommand(CancelDownload);
        SignInCommand = new RelayCommand(() => Open(LicenseClient.GithubSignInUrl));
        PricingCommand = new RelayCommand(() => Open(LicenseClient.PricingUrl));
        OpenBinaryFolderCommand = new RelayCommand(OpenBinaryFolder);

        AskRemoveCommand = new RelayCommand(() => IsRemoveOpen = true);
        CancelRemoveCommand = new RelayCommand(() => IsRemoveOpen = false);
        ConfirmRemoveCommand = new AsyncRelayCommand(RemoveAsync, onError: toasts.Error);

        // Rendered from whatever the service already knows, so the panel is populated
        // before the first network call returns rather than flashing "No key".
        _state = license.Current;
        _binary = binaries.Inspect();
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    public AsyncRelayCommand ActivateCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand SignInCommand { get; }
    public RelayCommand PricingCommand { get; }
    public RelayCommand OpenBinaryFolderCommand { get; }
    public RelayCommand AskRemoveCommand { get; }
    public RelayCommand CancelRemoveCommand { get; }
    public AsyncRelayCommand ConfirmRemoveCommand { get; }

    // ------------------------------------------------------------------
    // Licence state
    // ------------------------------------------------------------------

    private LicenseState _state;

    private LicenseState State
    {
        get => _state;
        set
        {
            _state = value;

            // One notification burst for the whole snapshot. The tier, seat count and
            // session counts are only meaningful together, and a panel that showed a
            // new tier beside a stale seat count would misstate what a launch will do.
            OnPropertyChanged(nameof(TierText));
            OnPropertyChanged(nameof(MaskedKey));
            OnPropertyChanged(nameof(SessionsText));
            OnPropertyChanged(nameof(CheckedText));
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ErrorIsWarning));
            OnPropertyChanged(nameof(ExpiryText));
            OnPropertyChanged(nameof(HasExpiry));
            OnPropertyChanged(nameof(HasKey));
            OnPropertyChanged(nameof(FromEnvironment));

            _onChanged();
        }
    }

    public string TierText => _state.Tier switch
    {
        LicenseTier.Pro => $"Pro — {_state.Plan ?? "paid plan"}",
        LicenseTier.Free => "Free key",
        // Distinguished from "No key" on purpose: an offline user with a valid key
        // must not be told they are unlicensed.
        LicenseTier.Unknown => "Key present, unverified",
        _ => "No key",
    };

    public string MaskedKey => _state.MaskedKey ?? "none";

    public bool HasKey => _state.HasKey;

    /// <summary>
    /// True when the key comes from an environment variable.
    /// <para>
    /// Worth saying, because "Remove key" then deletes a file that is not the one in
    /// effect and the tier would not change — which looks like the button is broken.
    /// </para>
    /// </summary>
    public bool FromEnvironment => _state.FromEnvironment;

    /// <summary>Local, server-side and allowed session counts in one line.</summary>
    public string SessionsText
    {
        get
        {
            var seats = _state.Seats is { } s ? s.ToString() : _state.Valid ? "unlimited" : "—";
            var server = _state.ActiveSessions is { } a ? $" · {a} on server" : "";
            return $"{_state.LocalSessions} local{server} / {seats}";
        }
    }

    public string CheckedText => _state.CheckedAt is { } at ? Ago(at) : "never";

    public string ErrorText => _state.Error ?? "";
    public bool HasError => !string.IsNullOrWhiteSpace(_state.Error);

    /// <summary>
    /// An unreachable server is a warning; a rejected key is an error.
    /// <para>
    /// The two call for opposite actions — wait, versus go and find a different key —
    /// and colouring them the same is the failure this distinction exists to prevent.
    /// </para>
    /// </summary>
    public bool ErrorIsWarning => _state.Unreachable || _state.Valid;

    public string ExpiryText => _state.Expires is { Length: > 0 } e
        ? $"This key expires on {e}."
        : "";

    public bool HasExpiry => _state.Expires is { Length: > 0 };

    // ------------------------------------------------------------------
    // Key entry
    // ------------------------------------------------------------------

    private string _keyInput = "";
    public string KeyInput
    {
        get => _keyInput;
        set => SetField(ref _keyInput, value);
    }

    private bool _activating;
    public bool IsActivating
    {
        get => _activating;
        private set
        {
            if (!SetField(ref _activating, value)) return;
            OnPropertyChanged(nameof(ActivateLabel));
            ActivateCommand.RaiseCanExecuteChanged();
        }
    }

    public string ActivateLabel => _activating ? "Validating…" : "Activate";

    private async Task ActivateAsync()
    {
        if (string.IsNullOrWhiteSpace(_keyInput))
        {
            _toasts.Warning("Paste your license key first.");
            return;
        }

        IsActivating = true;
        try
        {
            var state = await _license
                .ActivateAsync(_keyInput, _localSessions())
                .ConfigureAwait(true);

            State = state;

            if (state.Valid)
            {
                KeyInput = "";
                _toasts.Success(state.Tier == LicenseTier.Pro
                    ? $"Pro license activated ({state.Plan})."
                    : "Free license activated. You now get the latest browser build.");
            }
            else if (state.Unreachable)
            {
                // Saved anyway. The binary reads the same file directly and does not
                // need our approval, so an offline user pasting a key they know is
                // good must still end up with it stored.
                KeyInput = "";
                _toasts.Warning(
                    "The key was saved but could not be checked — the license server was unreachable.");
            }
            else
            {
                _toasts.Error(state.Error ?? "That license key was rejected.");
            }

            // The tier decides which build is offered, so the binary panel is stale
            // the moment the key changes.
            RefreshBinary();
        }
        finally
        {
            IsActivating = false;
        }
    }

    // ------------------------------------------------------------------
    // Re-check
    // ------------------------------------------------------------------

    private bool _refreshing;
    public bool IsRefreshing
    {
        get => _refreshing;
        private set
        {
            if (!SetField(ref _refreshing, value)) return;
            OnPropertyChanged(nameof(RefreshLabel));
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string RefreshLabel => _refreshing ? "Checking…" : "Re-check";

    private bool _loaded;

    /// <summary>
    /// Check once, the first time the page is opened.
    /// <para>
    /// Not at startup: the licence server is a network round trip and nothing on the
    /// launch path needs it — the seat limit falls back to the user's own preference
    /// when unknown, and the binary reads the key file itself.
    /// </para>
    /// </summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            State = await _license.RefreshAsync(_localSessions()).ConfigureAwait(true);
            RefreshBinary();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // ------------------------------------------------------------------
    // Binary
    // ------------------------------------------------------------------

    private BinaryState _binary;

    private BinaryState Binary
    {
        get => _binary;
        set
        {
            _binary = value;

            OnPropertyChanged(nameof(BinaryStatusText));
            OnPropertyChanged(nameof(BinaryVersion));
            OnPropertyChanged(nameof(BinaryBuildText));
            OnPropertyChanged(nameof(BinaryPlatform));
            OnPropertyChanged(nameof(BinaryPath));
            OnPropertyChanged(nameof(HasBinaryPath));
            OnPropertyChanged(nameof(BinaryError));
            OnPropertyChanged(nameof(HasBinaryError));
            OnPropertyChanged(nameof(DownloadLabel));
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(UpdateText));
        }
    }

    private void RefreshBinary() => Binary = _binaries.Inspect();

    public string BinaryStatusText => _binary.Installed ? "Installed" : "Not installed";
    public string BinaryVersion => _binary.Version ?? "—";
    public string BinaryBuildText => _binary.Tier == BinaryTier.Pro ? "Pro (latest)" : "Free";
    public string BinaryPlatform => _binary.Platform ?? "—";
    public string BinaryPath => _binary.Path ?? "";
    public bool HasBinaryPath => !string.IsNullOrWhiteSpace(_binary.Path);

    public string BinaryError => _binary.Error ?? "";
    public bool HasBinaryError => !string.IsNullOrWhiteSpace(_binary.Error);

    public bool UpdateAvailable => _binary.UpdateAvailable;

    public string UpdateText => _binary.UpdateAvailable
        ? $"Version {_binary.Latest} is available. The installed build stays in place until you download — " +
          "a browser update can shift fingerprint surfaces, so profiles a site already trusts are not " +
          "moved off their build without asking."
        : "";

    private bool _downloading;
    public bool IsDownloading
    {
        get => _downloading;
        private set
        {
            if (!SetField(ref _downloading, value)) return;
            OnPropertyChanged(nameof(DownloadLabel));
            OnPropertyChanged(nameof(IsNotDownloading));
            DownloadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsNotDownloading => !_downloading;

    public string DownloadLabel =>
        _downloading ? "Downloading…" : _binary.Installed ? "Re-download" : "Download browser";

    private string _progressText = "";

    /// <summary>
    /// Live download progress.
    /// <para>
    /// A first download is a few hundred megabytes. Without a byte count the button
    /// simply reads "Downloading…" for two minutes, which is indistinguishable from
    /// a hang and is the point at which users kill the app.
    /// </para>
    /// </summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    private async Task DownloadAsync()
    {
        _download?.Cancel();
        _download?.Dispose();
        _download = new CancellationTokenSource();

        IsDownloading = true;
        ProgressText = "Starting…";

        var progress = new Progress<DownloadProgress>(p =>
            Dispatcher.UIThread.Post(() => ProgressText = p.Label));

        try
        {
            var settings = _settings.Current;

            var state = await _binaries.EnsureAsync(
                licenseKey: _license.Store.Read(),
                versionPin: settings.BrowserVersion,
                preview: settings.ReleaseChannel == Core.Model.ReleaseChannel.Preview,
                progress: progress,
                cancel: _download.Token).ConfigureAwait(true);

            Binary = state;

            if (state.Error is { Length: > 0 } error) _toasts.Error(error);
            else if (state.Installed) _toasts.Success($"Browser {state.Version} is ready.");
        }
        catch (OperationCanceledException)
        {
            _toasts.Info("Download stopped.");
            RefreshBinary();
        }
        finally
        {
            IsDownloading = false;
            ProgressText = "";
        }
    }

    private void CancelDownload() => _download?.Cancel();

    private void OpenBinaryFolder()
    {
        var path = _binary.CacheDir ?? _binary.Path;
        if (string.IsNullOrWhiteSpace(path)) return;
        Open(path);
    }

    // ------------------------------------------------------------------
    // Removing the key
    // ------------------------------------------------------------------

    private bool _removeOpen;
    public bool IsRemoveOpen
    {
        get => _removeOpen;
        private set => SetField(ref _removeOpen, value);
    }

    private Task RemoveAsync()
    {
        IsRemoveOpen = false;

        State = _license.Clear(_localSessions());
        RefreshBinary();

        _toasts.Success(_state.FromEnvironment
            // The env var still wins on the next read, so saying "removed" alone
            // would be a lie the user could immediately disprove.
            ? "The key file was deleted, but CLOAKBROWSER_LICENSE_KEY is still set in this environment."
            : "License key removed.");

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Open a URL or folder with the system handler.</summary>
    private void Open(string target)
    {
        try
        {
            // UseShellExecute is what routes this to the browser or file manager;
            // without it .NET tries to execute the string as a program.
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A headless or minimal Linux install has no handler at all, which is a
            // normal condition here rather than a bug -- the target is still useful,
            // so it is shown instead.
            _toasts.Error($"Could not open {target}: {ex.Message}");
        }
    }

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;

        if (span < TimeSpan.FromSeconds(45)) return "just now";
        if (span < TimeSpan.FromMinutes(90)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromHours(36)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }

    public void Dispose()
    {
        _download?.Cancel();
        _download?.Dispose();
    }
}
