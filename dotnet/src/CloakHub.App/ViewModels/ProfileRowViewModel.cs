using System;
using System.Collections.Generic;
using System.Linq;
using CloakHub.Core.Model;

namespace CloakHub.App.ViewModels;

/// <summary>
/// One row in the profiles table.
/// <para>
/// Wraps a <see cref="Profile"/> and adds the live session state, which is not
/// stored. Kept separate from the record because the record is what gets persisted:
/// mixing a transient "is running" flag into it would write session state to disk
/// and make a profile look running after a crash.
/// </para>
/// </summary>
public sealed class ProfileRowViewModel : ViewModelBase
{
    private readonly ProfilesPageViewModel _page;

    public ProfileRowViewModel(Profile profile, ProfilesPageViewModel page)
    {
        Profile = profile;
        _page = page;

        StartCommand = new RelayCommand(() => page.Start(this), () => !IsRunning);
        StopCommand = new RelayCommand(() => page.Stop(this), () => IsRunning);
        EditCommand = new RelayCommand(() => page.Edit(this));
        DuplicateCommand = new RelayCommand(() => page.Duplicate(this));
        DeleteCommand = new RelayCommand(() => page.Delete(this));
    }

    public Profile Profile { get; }

    public string Id => Profile.Id;
    public string Name => Profile.Name;
    public string? Notes => Profile.Notes;

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand DeleteCommand { get; }

    /// <summary>The folder this profile is filed in, or null for the root.</summary>
    public string? FolderId => Profile.FolderId;

    /// <summary>
    /// The entries in this row's "Move to" menu.
    /// <para>
    /// Built on each access rather than captured at construction, so a folder created
    /// after this row was built still appears. The menu is only materialised when
    /// opened, so rebuilding costs nothing.
    /// </para>
    /// </summary>
    public IReadOnlyList<MoveTargetViewModel> MoveTargets =>
        [.. _page.FolderChoices().Select(f => new MoveTargetViewModel(f, this, _page))];

    // ------------------------------------------------------------------
    // Live state
    // ------------------------------------------------------------------

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(IsStopped));
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsStopped => !_isRunning;

    private int? _ordinal;

    /// <summary>The badge number of the live session, or null when stopped.</summary>
    public string InstanceLabel => _ordinal is { } n ? $"#{n}" : "";

    public string StatusLabel => _isRunning ? "Running" : "Idle";

    internal void MarkRunning(int ordinal)
    {
        _ordinal = ordinal;
        IsRunning = true;
        OnPropertyChanged(nameof(InstanceLabel));
    }

    internal void MarkStopped()
    {
        _ordinal = null;
        IsRunning = false;
        OnPropertyChanged(nameof(InstanceLabel));
    }

    // ------------------------------------------------------------------
    // Display
    // ------------------------------------------------------------------

    /// <summary>Proxy summary, or "Direct" when there is none.</summary>
    public string ProxyLabel => Profile.Proxy.Kind == ProxyKind.None
        ? "Direct"
        : $"{Profile.Proxy.Kind.ToString().ToLowerInvariant()}://{Profile.Proxy.Host}:{Profile.Proxy.Port}";

    public bool HasProxy => Profile.Proxy.Kind != ProxyKind.None;

    /// <summary>
    /// Platform, spelled the way the vendor spells it.
    /// <para>
    /// Not <c>ToString()</c> on the enum: that yields "Macos", which is wrong in a UI
    /// whose entire job is convincing a site the profile is a real machine. The enum
    /// name follows C# casing rules; the label follows Apple's.
    /// </para>
    /// </summary>
    public string PlatformLabel => Profile.Fingerprint.Platform switch
    {
        FingerprintPlatform.Macos => "macOS",
        FingerprintPlatform.Windows => "Windows",
        FingerprintPlatform.Linux => "Linux",
        _ => Profile.Fingerprint.Platform.ToString(),
    };

    /// <summary>
    /// Relative last-launch time.
    /// <para>
    /// Relative rather than absolute because the question a user asks of this column
    /// is "is this profile stale", and "3 days ago" answers it at a glance where a
    /// date requires arithmetic.
    /// </para>
    /// </summary>
    public string LastUsedLabel
    {
        get
        {
            if (Profile.LastLaunchedAt is not { } ms) return "Never";

            var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms);

            return elapsed switch
            {
                { TotalMinutes: < 1 } => "Just now",
                { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
                { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h ago",
                { TotalDays: < 30 } => $"{(int)elapsed.TotalDays}d ago",
                _ => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd"),
            };
        }
    }

    /// <summary>
    /// Row accent colour, or the theme's border colour when unset.
    /// <para>
    /// A literal fallback rather than transparent: the swatch is a fixed-width column,
    /// and a transparent one would leave a ragged gap down the table.
    /// </para>
    /// </summary>
    public string ColourHex => string.IsNullOrWhiteSpace(Profile.Colour) ? "#333b4a" : Profile.Colour!;

    public string TagsLabel => Profile.Tags.Count == 0 ? "" : string.Join(", ", Profile.Tags);

    public bool HasTags => Profile.Tags.Count > 0;
}
