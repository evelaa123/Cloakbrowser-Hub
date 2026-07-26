using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CloakHub.App.Views;

public partial class ProfileEditorView : UserControl
{
    public ProfileEditorView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
