using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CloakHub.App.ViewModels;

namespace CloakHub.App.Views;

public partial class ImportView : UserControl
{
    public ImportView()
    {
        InitializeComponent();

        // Pickers are supplied here rather than reached for from the view model,
        // because Avalonia's storage provider hangs off the TopLevel -- which does
        // not exist until the control is attached, and never exists in a unit test.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ImportPageViewModel vm) return;
            vm.FolderPicker = PickFolderAsync;
            vm.ArchivePicker = PickArchiveAsync;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task<string?> PickFolderAsync()
    {
        // Resolved at call time, not cached: TopLevel is null before the control is
        // attached to a window.
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to search for browser profiles",
            AllowMultiple = false,
        });

        // TryGetLocalPath rather than the URI: the scanner uses Directory.EnumerateX,
        // which does not accept a file:// URI. A cloud-provider folder has no local
        // path at all, and returning null there is correct -- it cannot be walked.
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickArchiveAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a profile archive",
            AllowMultiple = false,
            FileTypeFilter =
            [
                // Named explicitly rather than using a wildcard, so the picker itself
                // rules out the formats the extractor would only refuse afterwards.
                new FilePickerFileType("Profile archives")
                {
                    Patterns = ["*.zip", "*.tar.gz", "*.tgz", "*.tar"],
                    MimeTypes = ["application/zip", "application/gzip", "application/x-tar"],
                },
                FilePickerFileTypes.All,
            ],
        });

        return picked.FirstOrDefault()?.TryGetLocalPath();
    }
}
