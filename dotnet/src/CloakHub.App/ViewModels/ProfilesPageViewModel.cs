using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CloakHub.App.Services;
using CloakHub.Core.Branding;
using CloakHub.Core.Licensing;
using CloakHub.Core.Model;
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
    private readonly SettingsStore _settings;
    private readonly HubPaths _paths;
    private readonly ToastHost _toasts;
    private readonly OrdinalAllocator _ordinals = new();

    /// <summary>Live ordinals, so a badge number is released when its session ends.</summary>
    private readonly Dictionary<string, int> _running = [];

    public ProfilesPageViewModel(
        ProfileStore store, SettingsStore settings, HubPaths paths, ToastHost toasts)
    {
        _store = store;
        _settings = settings;
        _paths = paths;
        _toasts = toasts;

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
            cancel: CloseEditor);
    }

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
            cancel: CloseEditor);

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
    /// Start a session.
    /// <para>
    /// Not yet wired to a real browser: the <c>IBrowserLauncher</c> adapter over the
    /// CloakBrowser package is still to be ported. What runs here is everything up to
    /// the launch — limit check, ordinal allocation, badge planning and asset
    /// generation — so the parts that are platform-specific can be exercised now.
    /// The toast says so explicitly rather than reporting a success that did not
    /// happen.
    /// </para>
    /// </summary>
    internal void Start(ProfileRowViewModel row)
    {
        var limit = SessionLimit.Resolve(_settings.Current.MaxConcurrentSessions, planSeats: null);

        if (_running.Count >= limit.Limit)
        {
            // Says which constraint applied. A refused launch that does not explain
            // itself is indistinguishable from a crash.
            _toasts.Warning($"Session limit reached ({limit.Limit}) — limited by {limit.Reason}.");
            return;
        }

        var profile = _store.Get(row.Id);
        if (profile is null)
        {
            _toasts.Error("That profile no longer exists.");
            return;
        }

        // Acquire takes no key: the allocator hands out the lowest free number
        // across all sessions, because the badge answers "which of the windows on my
        // screen is this" -- a per-profile counter would put two #1 windows side by
        // side.
        var ordinal = _ordinals.Acquire();

        try
        {
            var plan = InstanceBadge.Plan(
                HostOs.Current,
                profile,
                ordinal,
                assetRoot: _paths.BrandingDir,
                canWriteAssets: true,
                stubExecutable: HostOs.FindLauncherStub());

            var assets = new BadgeAssetWriter().Write(
                plan,
                browserExecutable: "cloakbrowser",
                baseIcon: AppIcon.Bytes,
                profileName: profile.Name);

            _running[profile.Id] = ordinal;
            row.MarkRunning(ordinal);
            _store.MarkLaunched(profile.Id);

            OnPropertyChanged(nameof(RunningCount));
            OnPropertyChanged(nameof(CountLabel));

            _toasts.Info(
                $"Prepared instance #{ordinal} for \"{profile.Name}\" ({plan.Strategy}). " +
                $"{assets.Written.Count} branding file(s) written. " +
                "The browser launch itself is not wired up yet.");
        }
        catch (Exception)
        {
            // Release the ordinal on failure, or the number is leaked for the lifetime
            // of the process and later sessions start counting from a gap.
            _ordinals.Release(ordinal);
            throw;
        }
    }

    internal void Stop(ProfileRowViewModel row)
    {
        // Removed with its value, because the ordinal -- not the profile id -- is what
        // the allocator needs back, and reading it before the remove would leave a
        // path where the dictionary is cleared but the number stays claimed.
        if (!_running.Remove(row.Id, out var ordinal))
        {
            _toasts.Warning($"\"{row.Name}\" is not running.");
            return;
        }

        _ordinals.Release(ordinal);
        row.MarkStopped();

        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(CountLabel));
    }

    /// <summary>Stop everything, for app shutdown.</summary>
    public void StopAll()
    {
        foreach (var (id, ordinal) in _running.ToList())
        {
            _ordinals.Release(ordinal);
            _running.Remove(id);
        }

        foreach (var row in Rows) row.MarkStopped();

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
