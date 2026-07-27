using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CloakHub.App.Views;

public partial class LicenseView : UserControl
{
    public LicenseView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
