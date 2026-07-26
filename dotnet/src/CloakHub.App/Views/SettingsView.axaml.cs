using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CloakHub.App.ViewModels;

namespace CloakHub.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // The picker is supplied here rather than reached for from the view model,
        // because Avalonia's storage provider hangs off the TopLevel -- which does not
        // exist until the control is attached, and never exists in a unit test.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsPageViewModel vm) vm.FolderPicker = PickFolderAsync;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Show the native folder picker.
    /// <para>
    /// Returns null when cancelled, which the caller treats as "leave the setting
    /// alone" — distinct from an empty string, which would mean "clear it".
    /// </para>
    /// </summary>
    private async Task<string?> PickFolderAsync()
    {
        // Resolved at call time, not cached: TopLevel is null before the control is
        // attached to a window, so capturing it in the constructor would give null.
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where profile data is stored",
            AllowMultiple = false,
        });

        var picked = folders.FirstOrDefault();
        if (picked is null) return null;

        // TryGetLocalPath rather than the URI: the launch code passes this to
        // Process.Start and Directory.CreateDirectory, neither of which accepts a
        // file:// URI. A non-local folder (a cloud provider) has no local path at all,
        // and returning null there is correct -- a browser cannot use it.
        return picked.TryGetLocalPath();
    }

    private void OnApplyVersion(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsPageViewModel vm) vm.ApplyVersion();
    }
}
