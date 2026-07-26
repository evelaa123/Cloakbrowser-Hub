using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CloakHub.App.Views;

/// <summary>
/// A stand-in for a screen that has not been ported yet.
/// <para>
/// The three strings are styled properties rather than constructor arguments so the
/// parent can set them in XAML, which keeps the "what is missing" text next to the
/// route it belongs to instead of in a separate class per screen.
/// </para>
/// </summary>
public partial class PlaceholderView : UserControl
{
    public static readonly StyledProperty<string> HeadingProperty =
        AvaloniaProperty.Register<PlaceholderView, string>(nameof(Heading), "");

    public static readonly StyledProperty<string> SubheadingProperty =
        AvaloniaProperty.Register<PlaceholderView, string>(nameof(Subheading), "");

    public static readonly StyledProperty<string> DetailProperty =
        AvaloniaProperty.Register<PlaceholderView, string>(nameof(Detail), "");

    public string Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string Subheading
    {
        get => GetValue(SubheadingProperty);
        set => SetValue(SubheadingProperty, value);
    }

    public string Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public PlaceholderView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
