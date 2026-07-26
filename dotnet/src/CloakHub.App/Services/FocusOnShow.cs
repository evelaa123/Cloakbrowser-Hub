using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
// For GetVisualRoot: Visual.VisualRoot itself is protected, so the extension method
// is the only public way to ask whether a control is still in a live tree.
using Avalonia.VisualTree;

namespace CloakHub.App.Services;

/// <summary>
/// Focuses a control and selects its text whenever it becomes visible.
/// <para>
/// Needed by the inline folder rename. The text box and the label share a cell and
/// are swapped with <c>IsVisible</c>, which means the box is never re-attached to
/// the tree — so <c>AttachedToVisualTree</c> fires once when the row is built and
/// never again. Without this the box appeared but the keyboard focus stayed on
/// whatever was focused before, and typing went somewhere else entirely.
/// </para>
/// <para>
/// An attached property rather than code-behind because the box lives inside a
/// <c>DataTemplate</c>: reaching it from the view's code would mean walking the item
/// containers and re-finding it on every rebuild of the folder list.
/// </para>
/// </summary>
public static class FocusOnShow
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(FocusOnShow));

    static FocusOnShow()
    {
        EnabledProperty.Changed.AddClassHandler<Control, bool>((control, args) =>
        {
            if (args.NewValue.GetValueOrDefault())
            {
                control.PropertyChanged += OnControlPropertyChanged;
                control.AttachedToVisualTree += OnAttached;
            }
            else
            {
                control.PropertyChanged -= OnControlPropertyChanged;
                control.AttachedToVisualTree -= OnAttached;
            }
        });
    }

    public static void SetEnabled(Control control, bool value) =>
        control.SetValue(EnabledProperty, value);

    public static bool GetEnabled(Control control) =>
        control.GetValue(EnabledProperty);

    /// <summary>
    /// Handles the case where the control is already visible when it first appears.
    /// <para>
    /// The folder list is rebuilt on every refresh, and creating a folder puts the new
    /// row straight into rename mode — so by the time its container is built,
    /// <c>IsRenaming</c> is already true and the box is constructed visible. No
    /// <c>IsVisible</c> change ever occurs, so without this the box appeared unfocused
    /// and the typed name went to whatever had focus before (in practice the "+"
    /// button, whose Enter created a second folder).
    /// </para>
    /// </summary>
    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is Control { IsVisible: true } control) FocusSoon(control);
    }

    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != Visual.IsVisibleProperty) return;
        if (sender is not Control control) return;
        if (args.NewValue is not true) return;

        FocusSoon(control);
    }

    private static void FocusSoon(Control control) =>
        // Posted rather than called directly: at the moment the property changes the
        // control has not been laid out yet, and Focus() on a control with no size is
        // dropped. By the next dispatcher pass the layout has run.
        Dispatcher.UIThread.Post(() =>
        {
            // Re-checked: between the post and the callback the row may have been
            // rebuilt by a refresh, leaving this instance detached. Focusing a control
            // that is no longer in the tree steals focus from the one that replaced it.
            if (!control.IsVisible || control.GetVisualRoot() is null) return;

            control.Focus(NavigationMethod.Pointer);

            // Select the existing name, so typing replaces it. The common rename is a
            // fresh "New folder" being given its real name, and requiring the user to
            // clear the placeholder first would make every rename two gestures.
            if (control is TextBox box) box.SelectAll();
        }, DispatcherPriority.Input);
}
