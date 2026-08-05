using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloakHub.App.Services;
using CloakHub.Core.Import;
using CloakHub.Core.Model;
using CloakHub.Core.Storage;

namespace CloakHub.App.ViewModels;

/// <summary>
/// Bring an existing browser profile into the Hub.
/// <para>
/// The page never decrypts anything. Chromium encrypts cookie values with an
/// OS-held key — DPAPI, the Keychain, a libsecret-derived AES key — so reading them
/// out would need three platform-specific implementations of the one operation most
/// likely to silently produce garbage. Instead the profile's own session-bearing
/// files are copied verbatim and the stealth binary decrypts them exactly as the
/// original browser did. That works identically everywhere and cannot half-succeed.
/// </para>
/// </summary>
public sealed class ImportPageViewModel : ViewModelBase
{
    private readonly ProfileStore _profiles;
    private readonly SettingsStore _settings;
    private readonly HubPaths _paths;
    private readonly ToastHost _toasts;
    private readonly ProfileImporter _importer;

    /// <summary>Called after a successful import so the shell can show the new profile.</summary>
    private readonly Action _onImported;

    public ImportPageViewModel(
        ProfileStore profiles,
        SettingsStore settings,
        HubPaths paths,
        ToastHost toasts,
        Action onImported)
    {
        _profiles = profiles;
        _settings = settings;
        _paths = paths;
        _toasts = toasts;
        _onImported = onImported;

        _importer = new ProfileImporter(
            profiles,
            id => paths.ProfileDataDir(settings.Current, id));

        ScanCommand = new AsyncRelayCommand(RescanAsync, () => !IsBusy, toasts.Error);
        ScanFolderCommand = new AsyncRelayCommand(ScanFolderAsync, () => !IsBusy, toasts.Error);
        ScanArchiveCommand = new AsyncRelayCommand(ScanArchiveAsync, () => !IsBusy, toasts.Error);
        ClearManualCommand = new RelayCommand(ClearManual);
        CancelImportCommand = new RelayCommand(CancelImport);
        ConfirmImportCommand = new AsyncRelayCommand(ConfirmImportAsync, () => !_importing, toasts.Error);
    }

    // ------------------------------------------------------------------
    // Host-supplied pickers
    // ------------------------------------------------------------------

    /// <summary>
    /// Native folder picker, supplied by the view.
    /// <para>
    /// Injected rather than reached for, because Avalonia's storage provider hangs
    /// off the <c>TopLevel</c> — which does not exist until the control is attached,
    /// and never exists in a unit test.
    /// </para>
    /// </summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>Native file picker, filtered to the archive types the extractor understands.</summary>
    public Func<Task<string?>>? ArchivePicker { get; set; }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ScanFolderCommand { get; }
    public AsyncRelayCommand ScanArchiveCommand { get; }
    public RelayCommand ClearManualCommand { get; }
    public RelayCommand CancelImportCommand { get; }
    public AsyncRelayCommand ConfirmImportCommand { get; }

    // ------------------------------------------------------------------
    // Automatic discovery
    // ------------------------------------------------------------------

    public ObservableCollection<DiscoveredRowViewModel> Found { get; } = [];

    private bool _scanned;

    /// <summary>
    /// Scan once, the first time the page is opened.
    /// <para>
    /// Not in the constructor: the walk touches every browser's profile directory on
    /// the machine and there is no reason to pay for it during startup for a user who
    /// never visits this screen.
    /// </para>
    /// </summary>
    public void EnsureScanned()
    {
        if (_scanned) return;
        _scanned = true;
        _ = RescanAsync();
    }

    private async Task RescanAsync()
    {
        Busy = "Looking for browser profiles…";
        try
        {
            // Off the UI thread: this stats thousands of files to size each profile,
            // which on a cold cache with a large Chrome install takes long enough to
            // freeze the window visibly.
            var found = await Task.Run(() => BrowserDiscovery.Discover()).ConfigureAwait(true);

            Found.Clear();
            foreach (var p in found) Found.Add(new DiscoveredRowViewModel(p, this));

            OnPropertyChanged(nameof(HasFound));
            OnPropertyChanged(nameof(NothingFound));
        }
        finally
        {
            Busy = null;
        }
    }

    public bool HasFound => Found.Count > 0;

    /// <summary>Distinct from <see cref="HasFound"/> so the empty state does not flash while scanning.</summary>
    public bool NothingFound => !IsBusy && Found.Count == 0;

    // ------------------------------------------------------------------
    // Manual scans
    // ------------------------------------------------------------------

    public ObservableCollection<DiscoveredRowViewModel> ManualRows { get; } = [];

    private FolderScan? _manual;

    public FolderScan? Manual
    {
        get => _manual;
        private set
        {
            _manual = value;

            ManualRows.Clear();
            foreach (var p in value?.Profiles ?? []) ManualRows.Add(new DiscoveredRowViewModel(p, this));

            OnPropertyChanged(nameof(Manual));
            OnPropertyChanged(nameof(HasManual));
            OnPropertyChanged(nameof(ManualHeading));
            OnPropertyChanged(nameof(ManualRoot));
            OnPropertyChanged(nameof(ManualNote));
            OnPropertyChanged(nameof(HasManualNote));
            OnPropertyChanged(nameof(ManualEmpty));
            OnPropertyChanged(nameof(HasManualRows));
        }
    }

    public bool HasManual => _manual is not null;
    public bool HasManualRows => ManualRows.Count > 0;
    public bool ManualEmpty => _manual is not null && ManualRows.Count == 0;

    public string ManualHeading => _manual?.ExtractedTo is not null
        ? "Profiles found in the archive"
        : "Profiles found in that folder";

    public string ManualRoot => _manual?.Root ?? "";

    public string ManualNote => _manual?.Note ?? "";
    public bool HasManualNote => !string.IsNullOrWhiteSpace(_manual?.Note);

    private async Task ScanFolderAsync()
    {
        if (FolderPicker is null) return;

        var dir = await FolderPicker().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(dir)) return;

        Busy = "Scanning…";
        try
        {
            var scan = await Task.Run(() => FolderScanner.Scan(dir)).ConfigureAwait(true);
            AdoptScan(scan);
        }
        finally
        {
            Busy = null;
        }
    }

    private async Task ScanArchiveAsync()
    {
        if (ArchivePicker is null) return;

        var file = await ArchivePicker().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(file)) return;

        if (!ArchiveExtractor.IsSupported(file))
        {
            _toasts.Error(
                "That file type is not supported. " +
                $"Pick one of: {string.Join(", ", ArchiveExtractor.SupportedExtensions)}.");
            return;
        }

        Busy = "Unpacking…";
        var dir = ArchiveExtractor.NewExtractionDir();

        try
        {
            var extracted = await ArchiveExtractor.ExtractAsync(file, dir).ConfigureAwait(true);

            if (!extracted.Ok)
            {
                // The temp directory is removed on failure. A half-unpacked archive is
                // not a scan result, and leaving it would silently consume the disk
                // every time a corrupt file is opened.
                ArchiveExtractor.Cleanup(dir);
                _toasts.Error(extracted.Error ?? "That archive could not be unpacked.");
                return;
            }

            if (extracted.Skipped.Count > 0)
            {
                // Named, not counted. Entries are skipped because they were symlinks or
                // tried to escape the extraction directory, and a user whose profile is
                // missing needs to know it was refused rather than simply absent.
                _toasts.Warning(
                    $"{extracted.Skipped.Count} archive entr(ies) were skipped: " +
                    string.Join("; ", extracted.Skipped.Take(3)) +
                    (extracted.Skipped.Count > 3 ? " …" : ""));
            }

            var scan = await Task.Run(() => FolderScanner.Scan(dir)).ConfigureAwait(true);
            AdoptScan(scan with { ExtractedTo = dir });
        }
        catch
        {
            ArchiveExtractor.Cleanup(dir);
            throw;
        }
        finally
        {
            Busy = null;
        }
    }

    /// <summary>
    /// Take a scan as the current manual result, releasing whatever it replaces.
    /// </summary>
    private void AdoptScan(FolderScan scan)
    {
        // The previous archive's temp directory is released first. Without this it
        // leaks until the OS clears /tmp -- and a browser profile is hundreds of
        // megabytes, so a few scans is gigabytes.
        if (_manual?.ExtractedTo is { } old && old != scan.ExtractedTo) ArchiveExtractor.Cleanup(old);

        Manual = scan;

        if (scan.Profiles.Count == 0)
        {
            _toasts.Warning(string.IsNullOrWhiteSpace(scan.Note)
                ? "No browser profiles were found there."
                : scan.Note);
        }
        else if (scan.Truncated)
        {
            _toasts.Warning(
                "That folder is very large, so the scan stopped early. " +
                "Pick a more specific folder if your profile is missing.");
        }
    }

    private void ClearManual()
    {
        if (_manual?.ExtractedTo is { } dir) ArchiveExtractor.Cleanup(dir);
        Manual = null;
    }

    /// <summary>
    /// Release any unpacked archive as the app closes.
    /// <para>
    /// Called from shutdown rather than relying on the OS, because on Windows the
    /// temp directory is not cleared on reboot.
    /// </para>
    /// </summary>
    public void OnShutdown() => ArchiveExtractor.Cleanup(_manual?.ExtractedTo);

    // ------------------------------------------------------------------
    // Busy state
    // ------------------------------------------------------------------

    private string? _busy;

    private string? Busy
    {
        get => _busy;
        set
        {
            if (!SetField(ref _busy, value, nameof(BusyLabel))) return;

            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(NothingFound));

            ScanCommand.RaiseCanExecuteChanged();
            ScanFolderCommand.RaiseCanExecuteChanged();
            ScanArchiveCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy => _busy is not null;
    public bool IsIdle => _busy is null;
    public string BusyLabel => _busy ?? "";

    // ------------------------------------------------------------------
    // The import dialogue
    // ------------------------------------------------------------------

    private DiscoveredProfile? _selected;

    public DiscoveredProfile? Selected
    {
        get => _selected;
        private set
        {
            _selected = value;

            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(IsDialogOpen));
            OnPropertyChanged(nameof(DialogTitle));
            OnPropertyChanged(nameof(DialogSubtitle));
            OnPropertyChanged(nameof(SizeWarning));
            OnPropertyChanged(nameof(ShowCopyNotice));
            OnPropertyChanged(nameof(ShowNoCookieWarning));
        }
    }

    public bool IsDialogOpen => _selected is not null;

    public string DialogTitle => _selected is null ? "" : $"Import {_selected.Browser} profile";
    public string DialogSubtitle => _selected?.Name ?? "";

    private string _newName = "";
    public string NewName
    {
        get => _newName;
        set => SetField(ref _newName, value);
    }

    private bool _copyData = true;

    /// <summary>
    /// Copy the session-bearing files, not just the settings.
    /// <para>
    /// On by default because "keep me signed in" is the reason people import at all.
    /// It is still a real trade-off, and the dialogue says so: a cloned profile shares
    /// its cookies <i>and</i> its established fingerprint with the original, so running
    /// both from different IPs is exactly the correlation the Hub exists to prevent.
    /// </para>
    /// </summary>
    public bool CopyData
    {
        get => _copyData;
        set
        {
            if (!SetField(ref _copyData, value)) return;
            OnPropertyChanged(nameof(ShowCopyNotice));
            OnPropertyChanged(nameof(ShowNoCookieWarning));
        }
    }

    public bool ShowCopyNotice => _copyData;

    /// <summary>Shown only when the copy is on but there is nothing to carry over.</summary>
    public bool ShowNoCookieWarning => _copyData && _selected is { HasCookies: false };

    /// <summary>
    /// Forewarning for a large profile.
    /// <para>
    /// The copy is synchronous from the user's point of view, and half a gigabyte of
    /// small files takes long enough that a silent pause reads as a hang.
    /// </para>
    /// </summary>
    public string SizeWarning =>
        _selected is { SizeMb: > 500 } s
            ? $"This profile is about {s.SizeMb:0} MB, so the copy will take a moment. "
            : "";

    private bool _importing;
    public bool IsImporting
    {
        get => _importing;
        private set
        {
            if (!SetField(ref _importing, value)) return;
            OnPropertyChanged(nameof(ImportButtonLabel));
            ConfirmImportCommand.RaiseCanExecuteChanged();
        }
    }

    public string ImportButtonLabel => _importing ? "Importing…" : "Import";

    internal void OpenDialog(DiscoveredProfile profile)
    {
        Selected = profile;
        NewName = $"{profile.Browser} — {profile.Name}";
        CopyData = true;
    }

    private void CancelImport() => Selected = null;

    private async Task ConfirmImportAsync()
    {
        if (_selected is not { } source) return;

        IsImporting = true;
        try
        {
            var platform = _settings.Current.DefaultPlatform;
            var copy = _copyData;
            var name = _newName;

            // Off the UI thread: cloning a profile copies hundreds of megabytes of
            // small files and would otherwise lock the window for the duration.
            var outcome = await Task
                .Run(() => _importer.Import(source, copy, platform, nameOverride: name))
                .ConfigureAwait(true);

            if (!outcome.Ok)
            {
                _toasts.Error(outcome.Error ?? "The profile could not be imported.");
                return;
            }

            Selected = null;

            _toasts.Success(outcome.Copy is { } clone
                ? $"Imported \"{outcome.Profile!.Name}\" — {clone.Copied.Count} item(s), {clone.MegaBytes:0.#} MB."
                : $"Imported \"{outcome.Profile!.Name}\" from the browser settings.");

            if (outcome.Copy is { Skipped.Count: > 0 } skipped)
            {
                _toasts.Warning(
                    $"{skipped.Skipped.Count} item(s) could not be copied: " +
                    string.Join("; ", skipped.Skipped.Take(3)) +
                    (skipped.Skipped.Count > 3 ? " …" : ""));
            }

            _onImported();
        }
        finally
        {
            IsImporting = false;
        }
    }
}

/// <summary>One discovered profile, in either results table.</summary>
public sealed class DiscoveredRowViewModel
{
    private readonly DiscoveredProfile _profile;

    public DiscoveredRowViewModel(DiscoveredProfile profile, ImportPageViewModel page)
    {
        _profile = profile;
        ImportCommand = new RelayCommand(() => page.OpenDialog(profile));
    }

    public string Browser => _profile.Browser;
    public string Name => _profile.Name;
    public string Path => _profile.Path;

    public bool HasCookies => _profile.HasCookies;

    /// <summary>
    /// "Present" or "None" rather than a tick.
    /// <para>
    /// This is the single field that decides whether the import keeps the user signed
    /// in, so it is worth a word.
    /// </para>
    /// </summary>
    public string CookieLabel => _profile.HasCookies ? "Present" : "None";

    /// <summary>
    /// An em dash when the size was not measured, which is not the same as zero.
    /// </summary>
    public string SizeLabel => _profile.SizeMb is { } mb ? $"{mb:0.#} MB" : "—";

    public RelayCommand ImportCommand { get; }
}
