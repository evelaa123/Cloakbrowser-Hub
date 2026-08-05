using CloakHub.Core.Model;

namespace CloakHub.App.ViewModels;

/// <summary>
/// What a sidebar row selects.
/// <para>
/// Three cases rather than "a nullable folder id", because null is already taken:
/// a profile with <c>FolderId == null</c> is genuinely at the root, so null cannot
/// also mean "no filter applied". Conflating them made <em>All profiles</em> and
/// <em>Ungrouped</em> the same row, which is wrong the moment one profile is filed.
/// </para>
/// </summary>
public enum FolderScope
{
    /// <summary>Every profile, whatever folder it is in.</summary>
    All,

    /// <summary>Only profiles not filed in any folder.</summary>
    Root,

    /// <summary>One named folder.</summary>
    Folder,
}

/// <summary>
/// One row in the folders sidebar.
/// <para>
/// Renaming happens in place rather than through a modal. The folder list is the
/// one part of the UI a user reorganises in bursts — creating three folders and
/// naming them in a row — and a dialog per rename turns that into six extra
/// clicks. Inline editing keeps the surrounding names visible while typing, which
/// is exactly the context needed to pick a consistent name.
/// </para>
/// </summary>
public sealed class FolderNodeViewModel : ViewModelBase
{
    private readonly ProfilesPageViewModel _page;

    private FolderNodeViewModel(ProfilesPageViewModel page, FolderScope scope, string? id, string name, int count)
    {
        _page = page;
        Scope = scope;
        Id = id;
        Name = name;
        Count = count;

        SelectCommand = new RelayCommand(() => page.SelectFolder(this));
        BeginRenameCommand = new RelayCommand(BeginRename, () => Scope == FolderScope.Folder);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        DeleteCommand = new RelayCommand(() => page.DeleteFolder(this), () => Scope == FolderScope.Folder);
    }

    public static FolderNodeViewModel All(ProfilesPageViewModel page, int count) =>
        new(page, FolderScope.All, null, "All profiles", count);

    public static FolderNodeViewModel Root(ProfilesPageViewModel page, int count) =>
        new(page, FolderScope.Root, null, "Ungrouped", count);

    public static FolderNodeViewModel For(ProfilesPageViewModel page, ProfileFolder folder, int count) =>
        new(page, FolderScope.Folder, folder.Id, folder.Name, count);

    public FolderScope Scope { get; }

    /// <summary>The folder id, or null for the two synthetic rows.</summary>
    public string? Id { get; }

    public string Name { get; }

    public int Count { get; }

    /// <summary>
    /// The count, as text.
    /// <para>
    /// Rendered even when zero. An empty folder that shows no number looks like a
    /// row that failed to load its count; "0" says the folder is simply empty.
    /// </para>
    /// </summary>
    public string CountLabel => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>False for the two synthetic rows, which cannot be renamed or deleted.</summary>
    public bool CanEdit => Scope == FolderScope.Folder;

    public RelayCommand SelectCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand CommitRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand DeleteCommand { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    // ------------------------------------------------------------------
    // Inline rename
    // ------------------------------------------------------------------

    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (!SetField(ref _isRenaming, value)) return;
            OnPropertyChanged(nameof(IsNotRenaming));
        }
    }

    /// <summary>
    /// The inverse, for the label's visibility.
    /// <para>
    /// A property rather than <c>!IsRenaming</c> in the binding: the label and the
    /// text box occupy the same cell, and a converter-free negation keeps the swap
    /// to one fact rather than two bindings that could disagree.
    /// </para>
    /// </summary>
    public bool IsNotRenaming => !_isRenaming;

    private string _renameDraft = "";

    /// <summary>The in-progress name. Discarded unless the rename is committed.</summary>
    public string RenameDraft
    {
        get => _renameDraft;
        set => SetField(ref _renameDraft, value);
    }

    private void BeginRename()
    {
        RenameDraft = Name;
        IsRenaming = true;
    }

    private void CommitRename()
    {
        IsRenaming = false;

        // Nothing typed, or nothing changed: treated as a cancel rather than as a
        // write of the same value, so the folder's file is not rewritten for a
        // no-op and the toast does not claim a rename that did not happen.
        var typed = _renameDraft.Trim();
        if (typed.Length == 0 || typed == Name) return;

        _page.RenameFolder(this, typed);
    }
}

/// <summary>
/// One entry in a row's "Move to" menu.
/// <para>
/// Carries its own command rather than the menu binding back up to the row. A
/// submenu item's DataContext is the folder, not the profile, so reaching the row's
/// command from there means walking the visual tree from inside a popup — which is
/// both unreadable and dependent on the exact control the flyout happens to be
/// hosted in. Building the target with the command already bound keeps the menu a
/// plain list.
/// </para>
/// </summary>
public sealed class MoveTargetViewModel
{
    public MoveTargetViewModel(FolderChoice folder, ProfileRowViewModel row, ProfilesPageViewModel page)
    {
        Name = folder.Name;

        // Disabled for the folder the profile is already in, rather than hidden. A
        // menu whose entries move around depending on the row is harder to use than
        // one with a stable shape and the current location greyed out.
        IsEnabled = row.FolderId != folder.Id;

        MoveCommand = new RelayCommand(() => page.MoveToFolder(row, folder), () => IsEnabled);
    }

    public string Name { get; }

    public bool IsEnabled { get; }

    public RelayCommand MoveCommand { get; }
}
