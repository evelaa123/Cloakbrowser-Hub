using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CloakHub.App.Services;
using CloakHub.Core.Branding;
using CloakHub.Core.Cookies;
using CloakHub.Core.Launch;
using CloakHub.Core.Licensing;
using CloakHub.Core.Model;
using CloakHub.Core.Network;
using CloakHub.Core.Platform;
using CloakHub.Core.Storage;

namespace CloakHub.App.ViewModels;

/// <summary>
/// The profiles list — the app's home screen.
/// <para>
/// The table is the primary control surface: start/stop, status, proxy and last-run
/// at a glance, with everything else behind the editor. Mirrors ProfilesPage.tsx.
/// </para>
/// </summary>
public sealed class ProfilesPageViewModel : ViewModelBase
{
    private readonly ProfileStore _store;
    private readonly ProxyStore _proxies;
    private readonly SettingsStore _settings;
    private readonly HubPaths _paths;
    private readonly ToastHost _toasts;
    private readonly SessionManager _sessions;
    private readonly CookieService _cookies;

    /// <summary>
    /// Profiles the UI currently shows as running, with their badge numbers.
    /// <para>
    /// A view-side mirror of the session manager's own table, kept so a row can be
    /// re-rendered without an async call. The manager remains the authority; this is
    /// refreshed from it after every start and stop.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, int> _running = [];

    public ProfilesPageViewModel(
        ProfileStore store,
        ProxyStore proxies,
        SettingsStore settings,
        HubPaths paths,
        ToastHost toasts,
        SessionManager sessions)
    {
        _store = store;
        _proxies = proxies;
        _settings = settings;
        _paths = paths;
        _toasts = toasts;
        _sessions = sessions;

        // Built here rather than injected, because it is defined entirely by two
        // things this page already holds: where a profile's data lives, and whether
        // its browser is up. Passing it in would mean threading the same settings
        // store through the composition root a second time to answer the first.
        _cookies = new CookieService(
            profileId => paths.ProfileDataDir(settings.Current, profileId),
            sessions.IsRunning);

        // The browser can end a session without the Hub asking -- the user closes the
        // last window, or it crashes. Without this the row would sit there claiming to
        // be running and its Stop button would report that nothing is running.
        _sessions.SessionsChanged += (_, _) => Dispatcher.UIThread.Post(SyncRunning);

        CreateCommand = new AsyncRelayCommand(CreateAsync, onError: toasts.Error);
        AddFolderCommand = new RelayCommand(AddFolder);
        CloseEditorCommand = new RelayCommand(CloseEditor);
        Refresh();
    }

    public ObservableCollection<ProfileRowViewModel> Rows { get; } = [];

    /// <summary>The sidebar rows: "All profiles", "Ungrouped", then each folder.</summary>
    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];

    public AsyncRelayCommand CreateCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand CloseEditorCommand { get; }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) ApplyFilter(); }
    }

    public int RunningCount => _running.Count;

    /// <summary>True when there are no profiles at all, as opposed to none matching.</summary>
    public bool IsEmpty => _store.List().Count == 0;

    public bool HasNoMatches => Rows.Count == 0 && !IsEmpty;

    public string CountLabel
    {
        get
        {
            var total = _store.List().Count;
            return $"{total} profile{(total == 1 ? "" : "s")} · {RunningCount} running";
        }
    }

    // ------------------------------------------------------------------
    // Editor
    // ------------------------------------------------------------------

    private ProfileEditorViewModel? _editor;

    /// <summary>
    /// The open editor, or null when the list is showing.
    /// <para>
    /// Held here rather than on the shell because the editor's Save has to refresh
    /// this page, and its Cancel has to leave the page untouched. Owning it means
    /// both are ordinary method calls instead of an event the shell has to relay.
    /// </para>
    /// </summary>
    public ProfileEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (!SetField(ref _editor, value)) return;
            OnPropertyChanged(nameof(IsEditorOpen));
            OnPropertyChanged(nameof(IsListVisible));
        }
    }

    public bool IsEditorOpen => _editor is not null;

    /// <summary>
    /// Whether the list is showing.
    /// <para>
    /// The editor covers the page rather than sitting beside it, so the list is
    /// hidden while it is open. Hidden rather than destroyed: the search text and
    /// folder selection survive a round trip through the editor.
    /// </para>
    /// </summary>
    public bool IsListVisible => _editor is null;

    internal void Edit(ProfileRowViewModel row)
    {
        // Re-read rather than editing the row's captured record. The row was built at
        // the last refresh, and a profile can have changed since -- a launch stamping
        // LastLaunchedAt, another window saving it. Editing the stale copy would write
        // those changes back out.
        var fresh = _store.Get(row.Id);
        if (fresh is null)
        {
            _toasts.Error("That profile no longer exists.");
            Refresh();
            return;
        }

        Editor = new ProfileEditorViewModel(
            fresh,
            _store.Folders(),
            save: SaveFromEditor,
            cancel: CloseEditor,
            savedProxies: _proxies.List(),
            cookies: NewCookiePanel(fresh.Id));
    }

    /// <summary>
    /// A cookie panel bound to one profile.
    /// <para>
    /// A fresh instance per editor rather than a shared one that is re-pointed: the
    /// panel caches the store's contents, and reusing it would show the previous
    /// profile's cookies for the moment before the refresh landed — on a screen whose
    /// whole purpose is to tell the user which account this profile is signed in to.
    /// </para>
    /// </summary>
    private CookiePanelViewModel NewCookiePanel(string profileId) =>
        new(profileId, _cookies, _sessions.IsRunning, _toasts);

    private void SaveFromEditor(Profile profile)
    {
        Save(profile);
        Editor = null;
    }

    private void CloseEditor() => Editor = null;

    // ------------------------------------------------------------------
    // Folder selection
    // ------------------------------------------------------------------

    private FolderScope _scope = FolderScope.All;
    private string? _scopeFolderId;

    /// <summary>
    /// The heading above the table.
    /// <para>
    /// Names the selected folder rather than always saying "Profiles". With a folder
    /// filter active, a fixed heading over a short list reads as profiles having gone
    /// missing; naming the scope explains why the list is short.
    /// </para>
    /// </summary>
    public string ScopeLabel => _scope switch
    {
        FolderScope.Root => "Ungrouped",
        FolderScope.Folder => Folders.FirstOrDefault(f => f.Id == _scopeFolderId)?.Name ?? "Profiles",
        _ => "Profiles",
    };

    internal void SelectFolder(FolderNodeViewModel node)
    {
        _scope = node.Scope;
        _scopeFolderId = node.Id;

        foreach (var folder in Folders)
            folder.IsSelected = ReferenceEquals(folder, node);

        ApplyFilter();
        OnPropertyChanged(nameof(ScopeLabel));
    }

    private void AddFolder()
    {
        // Created with a placeholder name and immediately put into rename mode, rather
        // than prompting first. The folder exists either way, so a cancelled prompt
        // would have been a wasted round trip; this way the common case -- create,
        // type, Enter -- is one uninterrupted gesture.
        var created = _store.AddFolder("New folder");
        Refresh();

        var node = Folders.FirstOrDefault(f => f.Id == created.Id);
        node?.BeginRenameCommand.Execute(null);
    }

    internal void RenameFolder(FolderNodeViewModel node, string name)
    {
        if (node.Id is null) return;

        if (!_store.RenameFolder(node.Id, name))
        {
            _toasts.Error("That folder no longer exists.");
            Refresh();
            return;
        }

        Refresh();
        OnPropertyChanged(nameof(ScopeLabel));
    }

    internal void DeleteFolder(FolderNodeViewModel node)
    {
        if (node.Id is null) return;

        // Counted before the delete, because RemoveFolder is what empties it.
        var affected = _store.CountIn(node.Id);

        if (!_store.RemoveFolder(node.Id))
        {
            _toasts.Error("That folder no longer exists.");
            Refresh();
            return;
        }

        // Reset the scope when the deleted folder was the one being viewed, or the
        // table would filter on an id that no longer exists and show nothing.
        if (_scope == FolderScope.Folder && _scopeFolderId == node.Id)
        {
            _scope = FolderScope.All;
            _scopeFolderId = null;
        }

        Refresh();
        OnPropertyChanged(nameof(ScopeLabel));

        // Says what happened to the contents. Deleting a container that held work is
        // the moment a user most needs telling that the work survived.
        _toasts.Success(affected == 0
            ? $"Deleted folder \"{node.Name}\"."
            : $"Deleted folder \"{node.Name}\" — {affected} profile{(affected == 1 ? "" : "s")} moved to Ungrouped.");
    }

    internal void MoveToFolder(ProfileRowViewModel row, FolderChoice target)
    {
        if (!_store.MoveToFolder(row.Id, target.Id))
        {
            _toasts.Error("That profile or folder no longer exists.");
            Refresh();
            return;
        }

        Refresh();
        _toasts.Success($"Moved \"{row.Name}\" to {target.Name}.");
    }

    /// <summary>The folder list as move-menu choices, with the root entry first.</summary>
    internal IReadOnlyList<FolderChoice> FolderChoices() =>
        [FolderChoice.Root, .. _store.Folders().Select(f => new FolderChoice(f.Id, f.Name))];

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    /// <summary>Re-read from the store and rebuild the rows.</summary>
    public void Refresh()
    {
        RebuildFolders();
        ApplyFilter();
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void RebuildFolders()
    {
        Folders.Clear();

        Folders.Add(FolderNodeViewModel.All(this, _store.List().Count));
        Folders.Add(FolderNodeViewModel.Root(this, _store.CountIn(null)));

        foreach (var folder in _store.Folders())
            Folders.Add(FolderNodeViewModel.For(this, folder, _store.CountIn(folder.Id)));

        // Re-mark the selection: the nodes are rebuilt on every refresh, so the flag
        // has to be reapplied from the scope rather than carried on the objects.
        foreach (var node in Folders)
            node.IsSelected = node.Scope == _scope
                && (node.Scope != FolderScope.Folder || node.Id == _scopeFolderId);
    }

    private void ApplyFilter()
    {
        var query = _query.Trim();

        var matching = _store.List()
            .Where(InScope)
            .Where(p => Matches(p, query))
            // Most recently active first: the profile a user wants next is
            // overwhelmingly the one they used last. Falls back to UpdatedAt so a
            // never-launched profile still sorts sensibly rather than to the bottom.
            .OrderByDescending(p => p.LastLaunchedAt ?? p.UpdatedAt)
            .ToList();

        Rows.Clear();
        foreach (var profile in matching)
        {
            var row = new ProfileRowViewModel(profile, this);
            if (_running.TryGetValue(profile.Id, out var ordinal)) row.MarkRunning(ordinal);
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasNoMatches));
    }

    /// <summary>Whether a profile passes the current folder filter.</summary>
    private bool InScope(Profile p) => _scope switch
    {
        FolderScope.Root => p.FolderId is null,
        FolderScope.Folder => p.FolderId == _scopeFolderId,
        _ => true,
    };

    /// <summary>
    /// Free-text match across the fields a user would search by.
    /// <para>
    /// Includes the proxy host and the platform, not just the name: with dozens of
    /// profiles the useful question is usually "which ones use this proxy" or "which
    /// are on Windows", and neither is answerable by name alone.
    /// </para>
    /// </summary>
    private static bool Matches(Profile p, string query)
    {
        if (query.Length == 0) return true;

        var haystack = string.Join(' ',
            p.Name,
            p.Notes ?? "",
            string.Join(' ', p.Tags),
            p.Proxy.Host ?? "",
            p.Fingerprint.Platform.ToString());

        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    private Task CreateAsync()
    {
        // ProfileFactory rather than a bare record: a default-constructed
        // FingerprintConfig leaves screen, GPU, cores and memory on their Real modes,
        // so the new profile would report the host machine until the user opened the
        // editor and rerolled by hand. The factory pins a coherent, complete identity
        // up front, which is what "new profile" is expected to mean here.
        //
        // The default platform comes from settings, so the decision lives in one place
        // rather than being duplicated per call site.
        var created = _store.Add(ProfileFactory.NewProfile(
            "New profile",
            _settings.Current.DefaultPlatform,
            // Filed into the folder being viewed. Creating inside a folder and finding
            // the profile at the root instead would mean re-filing every one by hand.
            folderId: _scope == FolderScope.Folder ? _scopeFolderId : null));

        Refresh();
        _toasts.Success($"Created \"{created.Name}\" with a fresh fingerprint.");

        // Opened straight away: a profile named "New profile" with a random identity is
        // never the end state, so the next action is always to edit it.
        Editor = new ProfileEditorViewModel(
            created,
            _store.Folders(),
            save: SaveFromEditor,
            cancel: CloseEditor,
            savedProxies: _proxies.List(),
            cookies: NewCookiePanel(created.Id));

        return Task.CompletedTask;
    }

    internal void Duplicate(ProfileRowViewModel row)
    {
        var copy = _store.Duplicate(row.Id);
        if (copy is null)
        {
            _toasts.Error("That profile no longer exists.");
            return;
        }

        Refresh();
        _toasts.Success($"Duplicated as \"{copy.Name}\" with a fresh fingerprint.");
    }

    internal void Delete(ProfileRowViewModel row)
    {
        // Refuses rather than force-killing. Deleting the profile of a live browser
        // would leave a running process writing to a user-data directory the Hub has
        // forgotten, so the file would reappear as an orphan on the next launch.
        if (row.IsRunning)
        {
            _toasts.Warning($"Stop \"{row.Name}\" before deleting it.");
            return;
        }

        if (!_store.Remove(row.Id))
        {
            _toasts.Error("That profile no longer exists.");
            return;
        }

        Refresh();
        _toasts.Success($"Deleted \"{row.Name}\".");
    }

    /// <summary>
    /// Start a session: build the launch flags, then hand off to the session manager.
    /// <para>
    /// The flags are assembled here from the stored profile and settings; everything
    /// after that — the concurrency cap, badge numbering, the binary-override gate and
    /// teardown ordering — belongs to <see cref="SessionManager"/> and is not
    /// duplicated.
    /// </para>
    /// </summary>
    internal async Task StartAsync(ProfileRowViewModel row)
    {
        var profile = _store.Get(row.Id);
        if (profile is null)
        {
            _toasts.Error("That profile no longer exists.");
            return;
        }

        row.MarkBusy("Starting…");
        try
        {
            var error = await LaunchAsync(profile).ConfigureAwait(true);
            if (error is not null) _toasts.Error(error);
        }
        finally
        {
            row.ClearBusy();
        }
    }

    /// <summary>
    /// Start a session and report the failure as a string rather than a toast.
    /// <para>
    /// Shared with the automation API, which has a caller waiting on an HTTP response
    /// and no user to show a toast to. Keeping one implementation is what stops a
    /// scripted launch from being fingerprinted differently to a clicked one — a
    /// second copy of the flag assembly would drift, and the drift would be close to
    /// invisible until a site noticed it.
    /// </para>
    /// </summary>
    internal async Task<string?> LaunchAsync(Profile profile, CancellationToken ct = default)
    {
        var limit = SessionLimit.Resolve(_settings.Current.MaxConcurrentSessions, planSeats: null);

        // Checked before anything changes state, so a refused launch never leaves a
        // half-started row behind.
        if (_running.Count >= limit.Limit)
            return $"Session limit reached ({limit.Limit}) — limited by {limit.Reason}.";

        try
        {
            var result = await _sessions.StartAsync(
                profile,
                await BuildRequestAsync(profile, ct).ConfigureAwait(true),
                baseIcon: AppIcon.Bytes,
                maxSessions: limit.Limit,
                canWriteAssets: true,
                windowsStub: HostOs.FindLauncherStub(),
                ct: ct).ConfigureAwait(true);

            switch (result)
            {
                case SessionResult.Started started:
                    _store.MarkLaunched(profile.Id);
                    SyncRunning();
                    _toasts.Success($"\"{profile.Name}\" is running as instance #{started.Session.Ordinal}.");
                    return null;

                case SessionResult.Failed failed:
                    SyncRunning();
                    return failed.Error;

                default:
                    SyncRunning();
                    return null;
            }
        }
        catch (BrowserNotFoundException e)
        {
            // A first run with no browser downloaded yet is an ordinary state, not a
            // fault, so it gets the instruction rather than a stack trace.
            SyncRunning();
            return e.Message;
        }
        catch (Exception e)
        {
            SyncRunning();
            return $"Could not start \"{profile.Name}\": {e.Message}";
        }
    }

    /// <summary>
    /// Last-resort handler for a command that threw.
    /// <para>
    /// Start and stop already report their own failures; this catches anything that
    /// escaped, so an unexpected exception surfaces as a message instead of tearing
    /// down the app from an async void.
    /// </para>
    /// </summary>
    internal void ReportError(Exception e) => _toasts.Error(e.Message);

    /// <summary>Translate a stored profile into the flags the browser is launched with.</summary>
    private async Task<LaunchRequest> BuildRequestAsync(Profile profile, CancellationToken ct = default)
    {
        var args = FingerprintArgs.Build(profile);
        args.AddRange(PrivacyArgs.Build(profile));

        // The wrapper's default flag set is not used, so the sandbox decision has to
        // be made here. Resolve inspects the host: dropping the sandbox is necessary
        // in containers that forbid unprivileged user namespaces, but it is a real
        // weakening and is never applied speculatively.
        args.AddRange(SandboxArgs.Resolve().Args);

        args.AddRange(profile.Startup.ExtraArgs);

        var settings = _settings.Current;
        var proxy = ResolveProxy(profile);
        var followsIp = profile.Locale.Mode == LocaleMode.Ip;

        // ------------------------------------------------------------------
        // "Follow proxy IP" has to actually resolve something.
        //
        // Previously this mode only set GeoIp on the request, and nothing ever
        // read that flag: no locale and no timezone reached the browser, so
        // Chromium used the host's own. A user on a Vienna VPN therefore got an
        // Austrian exit IP alongside their machine's language and timezone --
        // precisely the mismatch this setting exists to remove, and one that is
        // trivial to detect from a page.
        //
        // The lookup is deliberately NOT gated on having a proxy. The egress IP
        // of a system-wide VPN is exactly the address the site will see, and it
        // is exactly the case that was broken.
        // ------------------------------------------------------------------
        var locale = profile.Locale.Mode == LocaleMode.Manual ? profile.Locale.Locale : null;
        var timezone = profile.Locale.Mode == LocaleMode.Manual ? profile.Locale.Timezone : null;

        if (followsIp)
        {
            var geo = await ResolveGeoAsync(profile, proxy, ct).ConfigureAwait(true);
            var resolved = GeoLocale.Resolve(locale, timezone, geo?.CountryCode, geo?.Timezone);
            locale = resolved.Locale;
            timezone = resolved.Timezone;
        }

        return new LaunchRequest
        {
            Args = args,
            Headless = profile.Startup.Headless,
            Locale = locale,
            Timezone = timezone,
            Proxy = proxy,
            GeoIp = followsIp,
            Humanize = profile.Behaviour.Humanize,
            ExtensionPaths = [.. profile.Startup.ExtensionPaths],
            LicenseKey = ReadLicenseKey(),
            BrowserVersion = string.IsNullOrWhiteSpace(settings.BrowserVersion)
                ? null
                : settings.BrowserVersion,
            ReleaseChannel = settings.ReleaseChannel == ReleaseChannel.Preview ? "preview" : "stable",
        };
    }

    /// <summary>
    /// Exit-IP geo for a launch, cached per endpoint.
    /// <para>
    /// Cached because this sits directly in front of starting a browser. An exit
    /// IP does not move often enough to justify paying a network round-trip on
    /// every launch, and a rotating proxy is re-checked by the proxy page anyway.
    /// The key is the endpoint, so two profiles on the same provider share one
    /// lookup while a profile on a different proxy never reuses another's country.
    /// </para>
    /// <para>
    /// A failed lookup returns null and the launch proceeds. Refusing to start a
    /// browser because a free geo API rate-limited us would be a far worse
    /// outcome than launching without a locale override -- which is exactly the
    /// behaviour this whole feature is replacing.
    /// </para>
    /// </summary>
    private async Task<ProxyCheckResult?> ResolveGeoAsync(
        Profile profile, ProxyConfig? proxy, CancellationToken ct)
    {
        // A configured proxy's own last check already holds the answer, and the
        // user just ran it. Reusing it keeps the common case free.
        if (proxy is not null && !string.IsNullOrWhiteSpace(profile.Proxy.SavedProxyId))
        {
            var saved = _proxies.Get(profile.Proxy.SavedProxyId!);
            if (saved?.LastCheck is { Ok: true, CountryCode.Length: > 0 } fresh)
                return fresh;
        }

        var key = proxy is null
            ? "direct"
            : $"{proxy.Kind}://{proxy.Host}:{proxy.Port}";

        if (_geoCache.TryGetValue(key, out var cached)) return cached;


        try
        {
            // No proxy still means a real lookup: the machine's egress IP is what
            // a system VPN rewrites, and that is the address sites will see.
            var result = await new ProxyChecker()
                .CheckAsync(proxy ?? new ProxyConfig(), ct)
                .ConfigureAwait(true);

            if (!result.Ok) return null;

            _geoCache[key] = result;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Never fatal -- see the summary above.
            return null;
        }
    }

    /// <summary>Exit-IP lookups for this app run, keyed by proxy endpoint.</summary>
    private readonly Dictionary<string, ProxyCheckResult> _geoCache = [];

    /// <summary>
    /// The proxy this profile should launch behind.
    /// <para>
    /// A profile may either carry its own endpoint or reference the shared library.
    /// The library wins when referenced, and is read at launch rather than copied at
    /// assignment time — that is the whole point of the library: rotating a
    /// provider's password updates one entry, not every profile using it.
    /// </para>
    /// <para>
    /// A dangling reference falls back to the profile's own settings rather than
    /// launching direct. Silently using the machine's real IP for a profile the user
    /// configured to be proxied is the one outcome that must not happen quietly.
    /// </para>
    /// </summary>
    private ProxyConfig? ResolveProxy(Profile profile)
    {
        var id = profile.Proxy.SavedProxyId;

        if (!string.IsNullOrWhiteSpace(id))
        {
            var saved = _proxies.Get(id);
            if (saved is not null)
            {
                // The library owns the endpoint and credentials; the profile keeps
                // its own bypass and rotation link, which are per-use rather than
                // per-provider.
                return new ProxyConfig
                {
                    Kind = saved.Kind,
                    Host = saved.Host,
                    Port = saved.Port,
                    Username = saved.Username,
                    Password = saved.Password,
                    Bypass = profile.Proxy.Bypass ?? saved.Bypass,
                    RotationUrl = profile.Proxy.RotationUrl ?? saved.RotationUrl,
                };
            }

            _toasts.Warning(
                $"\"{profile.Name}\" points at a proxy that is no longer in the library. " +
                "Using the settings stored on the profile.");
        }

        return profile.Proxy.IsConfigured ? profile.Proxy : null;
    }

    /// <summary>
    /// The activated licence key, or null.
    /// <para>
    /// Read at launch rather than cached at startup so activating a key takes effect
    /// on the next session instead of the next restart. An unreadable file is treated
    /// as no key: launching unlicensed is a working browser, while refusing to launch
    /// over an unreadable file is not.
    /// </para>
    /// </summary>
    private string? ReadLicenseKey()
    {
        try
        {
            if (!File.Exists(_paths.LicenseFile)) return null;
            var (key, _) = LicenseKeyFile.ReadFile(File.ReadAllBytes(_paths.LicenseFile));
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            return null;
        }
    }

    internal async Task StopAsync(ProfileRowViewModel row)
    {
        if (!_running.ContainsKey(row.Id))
        {
            _toasts.Warning($"\"{row.Name}\" is not running.");
            return;
        }

        row.MarkBusy("Stopping…");
        try
        {
            var result = await _sessions.StopAsync(row.Id).ConfigureAwait(true);
            if (result is SessionResult.Failed failed) _toasts.Error(failed.Error);
        }
        finally
        {
            row.ClearBusy();
            SyncRunning();
        }
    }

    /// <summary>Stop everything, for app shutdown.</summary>
    public void StopAll()
    {
        // Blocking is deliberate here and only here: this runs on the shutdown path,
        // and letting the process exit while browsers are mid-close would skip the
        // flush that writes cookies and session storage back to disk.
        try
        {
            _sessions.StopAllAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Already exiting; a teardown failure has nowhere useful to be reported.
        }

        SyncRunning();
    }

    /// <summary>
    /// Bring the rows in line with the session manager.
    /// <para>
    /// One place that reads the live set and repaints, so a session ending on its own
    /// and one the user stopped both leave the UI in the same state.
    /// </para>
    /// </summary>
    private void SyncRunning()
    {
        var live = _sessions.ListAsync().GetAwaiter().GetResult();

        _running.Clear();
        foreach (var s in live) _running[s.ProfileId] = s.Ordinal;

        foreach (var row in Rows)
        {
            if (_running.TryGetValue(row.Id, out var ordinal)) row.MarkRunning(ordinal);
            else row.MarkStopped();
        }

        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(CountLabel));
    }

    internal Profile? Load(string id) => _store.Get(id);

    internal void Save(Profile profile)
    {
        _store.Update(profile);
        Refresh();
        _toasts.Success($"Saved \"{profile.Name}\".");
    }
}
