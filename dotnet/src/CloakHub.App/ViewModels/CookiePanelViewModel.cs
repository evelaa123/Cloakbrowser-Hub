using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CloakHub.Core.Cookies;

namespace CloakHub.App.ViewModels;

/// <summary>
/// The profile editor's Cookies tab.
/// <para>
/// Kept out of <see cref="ProfileEditorViewModel"/> and injected, because it is the
/// one part of the editor that does not work on the draft. Cookies are written
/// straight to the profile's Chromium store, so they are saved the moment the button
/// is pressed and are unaffected by Cancel — the opposite of every other field on the
/// form. Folding them into the draft would have meant either lying about that, or
/// making Save responsible for a SQLite write that can fail long after the user has
/// stopped looking at the dialog.
/// </para>
/// <para>
/// Being a separate object also makes the tab's absence expressible: the editor shows
/// the tab only when one of these is supplied, so a context without a cookie service
/// simply does not offer it.
/// </para>
/// </summary>
public sealed class CookiePanelViewModel : ViewModelBase
{
    private readonly string _profileId;
    private readonly CookieService _cookies;
    private readonly Func<string, bool> _runningProbe;
    private readonly ToastHost _toasts;

    /// <param name="profileId">Profile whose store is being edited.</param>
    /// <param name="cookies">The import/export service.</param>
    /// <param name="isRunning">
    /// Whether the profile's browser is up. Asked repeatedly rather than captured
    /// once: the editor can stay open across a launch, and a stale answer would let
    /// the user write a store Chromium is about to overwrite.
    /// </param>
    /// <param name="toasts">Where results are reported.</param>
    public CookiePanelViewModel(
        string profileId,
        CookieService cookies,
        Func<string, bool> isRunning,
        ToastHost toasts)
    {
        _profileId = profileId;
        _cookies = cookies;
        _runningProbe = isRunning;
        _toasts = toasts;

        ImportCommand = new RelayCommand(Import, () => CanImport);
        ImportFilesCommand = new AsyncRelayCommand(ImportFilesAsync, () => !IsRunning, toasts.Error);
        ExportJsonCommand = new AsyncRelayCommand(
            () => ExportAsync(CookieFormat.Json), () => HasCookies, toasts.Error);
        ExportNetscapeCommand = new AsyncRelayCommand(
            () => ExportAsync(CookieFormat.Netscape), () => HasCookies, toasts.Error);
        ClearCommand = new RelayCommand(Clear, () => HasCookies && !IsRunning);
        RefreshCommand = new RelayCommand(Refresh);

        Refresh();
    }

    // ------------------------------------------------------------------
    // Host-supplied pickers
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens the file-open dialog, returning the chosen paths or an empty list.
    /// Supplied by the view, because Avalonia's storage provider hangs off the
    /// TopLevel — which does not exist in a unit test.
    /// </summary>
    public Func<Task<IReadOnlyList<string>>>? FilePicker { get; set; }

    /// <summary>Opens the save dialog, returning the chosen path or null.</summary>
    public Func<string, Task<string?>>? SavePicker { get; set; }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    public RelayCommand ImportCommand { get; }
    public AsyncRelayCommand ImportFilesCommand { get; }
    public AsyncRelayCommand ExportJsonCommand { get; }
    public AsyncRelayCommand ExportNetscapeCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RefreshCommand { get; }

    // ------------------------------------------------------------------
    // Stored state
    // ------------------------------------------------------------------

    /// <summary>Domains present in the store, and the services recognised in it.</summary>
    public ObservableCollection<string> StoredDomains { get; } = [];

    public ObservableCollection<string> StoredServices { get; } = [];

    private int _count;

    /// <summary>How many cookies the profile currently holds.</summary>
    public int Count
    {
        get => _count;
        private set
        {
            if (!SetField(ref _count, value)) return;
            OnPropertyChanged(nameof(HasCookies));
            OnPropertyChanged(nameof(CountLabel));
            RaiseAll();
        }
    }

    public bool HasCookies => _count > 0;

    public string CountLabel => _count switch
    {
        0 => "No cookies stored",
        1 => "1 cookie stored",
        _ => $"{_count} cookies stored",
    };

    public bool HasStoredDomains => StoredDomains.Count > 0;
    public bool HasStoredServices => StoredServices.Count > 0;

    /// <summary>
    /// True while the profile's browser is up, which blocks every write.
    /// <para>
    /// Recomputed on each refresh rather than being a live subscription. The panel is
    /// only visible while the editor is open, and the service refuses the write
    /// anyway — this exists to explain the disabled buttons, not to enforce anything.
    /// </para>
    /// </summary>
    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanImport));
            RaiseAll();
        }
    }

    // ------------------------------------------------------------------
    // Paste box
    // ------------------------------------------------------------------

    private string _paste = "";

    /// <summary>The pasted payload, in any format the parser recognises.</summary>
    public string Paste
    {
        get => _paste;
        set
        {
            if (!SetField(ref _paste, value)) return;
            Inspect();
        }
    }

    private bool _replace;

    /// <summary>
    /// Whether to clear the store first. Off by default: merging is the recoverable
    /// choice, and a user who pastes a second export expecting it to add to the first
    /// would otherwise silently lose the session they already had.
    /// </summary>
    public bool Replace
    {
        get => _replace;
        set => SetField(ref _replace, value);
    }

    private string _domain = "";

    /// <summary>
    /// Domain to attach to header-format input, which carries none of its own.
    /// Ignored for JSON and Netscape, where every cookie names its domain.
    /// </summary>
    public string Domain
    {
        get => _domain;
        set
        {
            if (!SetField(ref _domain, value)) return;

            // CanImport depends on this whenever the paste is a bare header, so the
            // notification has to be raised here too. Without it the button stays
            // disabled after the domain is typed and the header path is unreachable
            // — the field's only purpose is to unblock exactly that import.
            OnPropertyChanged(nameof(CanImport));
            ImportCommand.RaiseCanExecuteChanged();
        }
    }

    private string _pasteSummary = "";

    /// <summary>What the parser makes of the paste box, shown before importing.</summary>
    public string PasteSummary
    {
        get => _pasteSummary;
        private set
        {
            if (!SetField(ref _pasteSummary, value)) return;
            OnPropertyChanged(nameof(HasPasteSummary));
        }
    }

    public bool HasPasteSummary => !string.IsNullOrEmpty(_pasteSummary);

    private bool _pasteIsError;

    /// <summary>Whether <see cref="PasteSummary"/> reports a problem rather than a count.</summary>
    public bool PasteIsError
    {
        get => _pasteIsError;
        private set => SetField(ref _pasteIsError, value);
    }

    private bool _needsDomain;

    /// <summary>
    /// True when the paste is a raw <c>Cookie:</c> header, which has no domain of its
    /// own. The field is revealed only then — asking for a domain alongside a JSON
    /// export that already names twelve of them would just be confusing.
    /// </summary>
    public bool NeedsDomain
    {
        get => _needsDomain;
        private set => SetField(ref _needsDomain, value);
    }

    /// <summary>
    /// Import is allowed once the paste parses, the browser is down, and — for a bare
    /// header — a domain has been given. Without the last condition the cookies would
    /// land on a placeholder host and belong to nothing.
    /// </summary>
    public bool CanImport =>
        !IsRunning
        && !_pasteIsError
        && !string.IsNullOrWhiteSpace(_paste)
        && (!_needsDomain || !string.IsNullOrWhiteSpace(_domain));

    // ------------------------------------------------------------------
    // Last result
    // ------------------------------------------------------------------

    private string _warning = "";

    /// <summary>
    /// Session cookies that were in the payload but are not in the store afterwards.
    /// <para>
    /// Surfaced prominently because the failure it describes is otherwise invisible:
    /// the import reports a healthy count, and the user only discovers the account is
    /// logged out after launching the browser and being shown a sign-in page.
    /// </para>
    /// </summary>
    public string Warning
    {
        get => _warning;
        private set
        {
            if (!SetField(ref _warning, value)) return;
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(_warning);

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    /// <summary>Re-read the store and the browser's state.</summary>
    public void Refresh()
    {
        IsRunning = ProbeRunning();

        List<BrowserCookie> stored;
        try
        {
            stored = _cookies.Read(_profileId);
        }
        catch (Exception)
        {
            // A profile that has never been launched has no cookie database at all,
            // which is not an error worth a toast — it is the normal state of a new
            // profile, and the empty panel already says so.
            stored = [];
        }

        var domains = stored
            .Select(c => c.HostOnlyDomain)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = stored.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        Refill(StoredDomains, domains);
        Refill(StoredServices, CookieValidator.DetectServices(names, domains));

        OnPropertyChanged(nameof(HasStoredDomains));
        OnPropertyChanged(nameof(HasStoredServices));

        Count = stored.Count;
        RaiseAll();
    }

    private void Import()
    {
        var result = _cookies.ImportText(
            _profileId,
            _paste,
            Replace,
            string.IsNullOrWhiteSpace(_domain) ? null : _domain.Trim());

        Report(result, "paste");
    }

    private async Task ImportFilesAsync()
    {
        if (FilePicker is null) return;

        var paths = await FilePicker();
        if (paths.Count == 0) return;

        var result = _cookies.ImportFiles(
            _profileId,
            paths,
            Replace,
            string.IsNullOrWhiteSpace(_domain) ? null : _domain.Trim());

        Report(result, paths.Count == 1 ? "file" : $"{paths.Count} files");
    }

    private async Task ExportAsync(CookieFormat format)
    {
        if (SavePicker is null) return;

        var suggested = format == CookieFormat.Netscape ? "cookies.txt" : "cookies.json";

        var path = await SavePicker(suggested);
        if (string.IsNullOrEmpty(path)) return;

        var written = _cookies.Export(_profileId, path, format);
        _toasts.Success($"Exported {written} cookie{(written == 1 ? "" : "s")}.");
    }

    private void Clear()
    {
        if (!_cookies.Clear(_profileId))
        {
            _toasts.Error("Close this profile's browser before clearing its cookies.");
            Refresh();
            return;
        }

        Paste = "";
        Warning = "";
        Refresh();
        _toasts.Success("Cleared this profile's cookies.");
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    /// <summary>
    /// Parse the paste box for the pre-import readout, without touching the store.
    /// <para>
    /// Runs on every keystroke, which is affordable because the validator only parses
    /// — it opens no files and no database. The payoff is that a malformed export is
    /// caught while the text is still on screen, rather than after a write that
    /// half-succeeded.
    /// </para>
    /// </summary>
    private void Inspect()
    {
        OnPropertyChanged(nameof(CanImport));

        if (string.IsNullOrWhiteSpace(_paste))
        {
            PasteSummary = "";
            PasteIsError = false;
            NeedsDomain = false;
            ImportCommand.RaiseCanExecuteChanged();
            return;
        }

        var check = CookieValidator.Validate(_paste);

        NeedsDomain = check.Format == CookieFormat.Header;
        PasteIsError = !check.Ok;

        if (!check.Ok)
        {
            PasteSummary = check.Error ?? "That does not look like a cookie export.";
        }
        else
        {
            var format = check.Format switch
            {
                CookieFormat.Json => "JSON",
                CookieFormat.Netscape => "Netscape",
                CookieFormat.Header => "Cookie header",
                _ => "unknown",
            };

            var parts = new List<string>
            {
                $"{check.Count} cookie{(check.Count == 1 ? "" : "s")} · {format}",
            };

            if (check.Domains.Count > 0)
            {
                // Capped, because a browser-wide export runs to hundreds of domains and
                // the readout is meant to confirm "this is the account I meant", not to
                // list the contents.
                var shown = string.Join(", ", check.Domains.Take(4));
                parts.Add(check.Domains.Count > 4
                    ? $"{shown} +{check.Domains.Count - 4} more"
                    : shown);
            }

            if (check.AuthHints.Count > 0)
                parts.Add($"signed in to {string.Join(", ", check.AuthHints)}");

            PasteSummary = string.Join(" — ", parts);
        }

        OnPropertyChanged(nameof(CanImport));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private void Report(CookieImportResult result, string source)
    {
        if (!result.Ok)
        {
            _toasts.Error(result.Error ?? "Import failed.");
            Refresh();
            return;
        }

        Warning = result.MissingCritical.Count == 0
            ? ""
            : "Chromium rejected some session cookies: "
              + string.Join(", ", result.MissingCritical)
              + ". Those accounts will still be signed out — the export is probably "
              + "stale or was taken without HttpOnly cookies.";

        // The paste box is cleared only on success, so a rejected payload stays on
        // screen to be corrected rather than having to be fetched again.
        Paste = "";
        Refresh();

        _toasts.Success(
            $"Imported {result.Imported} cookie{(result.Imported == 1 ? "" : "s")} from {source}; "
            + $"{result.Count} now stored.");
    }

    /// <summary>
    /// Ask the running predicate without letting it take the panel down. It reaches
    /// into the session manager, and a fault there should disable the buttons, not
    /// throw out of a property getter during a refresh.
    /// </summary>
    private bool ProbeRunning()
    {
        try { return _runningProbe(_profileId); }
        catch (Exception) { return false; }
    }

    private void RaiseAll()
    {
        ImportCommand.RaiseCanExecuteChanged();
        ImportFilesCommand.RaiseCanExecuteChanged();
        ExportJsonCommand.RaiseCanExecuteChanged();
        ExportNetscapeCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private static void Refill(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        target.Clear();
        foreach (var v in values) target.Add(v);
    }
}
