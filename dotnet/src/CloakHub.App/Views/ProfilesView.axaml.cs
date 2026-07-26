using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CloakHub.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
