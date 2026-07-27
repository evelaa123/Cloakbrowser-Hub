using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CloakHub.App.ViewModels;

namespace CloakHub.App.Views;

public partial class ProfileEditorView : UserControl
{
    public ProfileEditorView()
    {
        InitializeComponent();

        // The pickers are pushed into the cookie panel from here rather than reached
        // for from the view model, because Avalonia's storage provider hangs off the
        // TopLevel -- which does not exist until the control is attached, and never
        // exists in a unit test. Same arrangement as the settings page.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ProfileEditorViewModel { Cookies: { } cookies }) return;

            cookies.FilePicker = PickCookieFilesAsync;
            cookies.SavePicker = PickExportPathAsync;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Choose one or more cookie export files.
    /// <para>
    /// Multi-select, because a bought account often arrives as a folder of per-domain
    /// exports, and the import merges them. Returns an empty list when cancelled.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> PickCookieFilesAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return [];

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose cookie files to import",
            AllowMultiple = true,
            FileTypeFilter =
            [
                // A permissive first entry, because exports arrive under every
                // extension there is -- .json, .txt, and frequently none at all. The
                // parser detects the format from the content regardless, so filtering
                // strictly here would only hide files that would have imported fine.
                new FilePickerFileType("Cookie exports")
                {
                    Patterns = ["*.json", "*.txt", "*.cookies", "*"],
                },
                FilePickerFileTypes.All,
            ],
        });

        // Non-local files -- a cloud provider entry -- have no path to read, so they
        // are dropped rather than passed on as nulls for the importer to trip over.
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <summary>Choose where to write an export. Null when cancelled.</summary>
    private async Task<string?> PickExportPathAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export cookies",
            SuggestedFileName = suggestedName,
            // Derived from the suggestion so the two exports do not need separate
            // picker configurations that could drift apart.
            DefaultExtension = suggestedName.EndsWith(".txt") ? "txt" : "json",
        });

        return file?.TryGetLocalPath();
    }
}
