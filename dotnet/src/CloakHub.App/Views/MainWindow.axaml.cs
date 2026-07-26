using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CloakHub.App.Services;

namespace CloakHub.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Set here rather than as Icon="..." in the XAML, so an unreadable asset
        // degrades to a window with no icon instead of throwing out of
        // InitializeComponent and preventing the window from opening at all.
        // See Services/Branding.
        Branding.Apply(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
