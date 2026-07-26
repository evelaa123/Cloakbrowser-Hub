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
        Refresh();
    }

    public ObservableCollection<ProfileRowViewModel> Rows { get; } = [];

    public AsyncRelayCommand CreateCommand { get; }

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
    // Loading
    // ------------------------------------------------------------------

    /// <summary>Re-read from the store and rebuild the rows.</summary>
    public void Refresh()
    {
        ApplyFilter();
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void ApplyFilter()
    {
        var query = _query.Trim();

        var matching = _store.List().Where(p => Matches(p, query))
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
        // The default platform comes from settings, so the decision lives in one place
        // rather than being duplicated per call site.
        var created = _store.Add(new Profile
        {
            Name = "New profile",
            Fingerprint = new FingerprintConfig { Platform = _settings.Current.DefaultPlatform },
        });

        Refresh();
        _toasts.Success($"Created \"{created.Name}\".");
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
