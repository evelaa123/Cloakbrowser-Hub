using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CloakHub.App.Views;

public partial class ProxiesView : UserControl
{
    public ProxiesView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
