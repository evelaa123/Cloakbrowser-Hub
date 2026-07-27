using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace CloakHub.App.Controls;

/// <summary>
/// Two form fields side by side, stacking to one column when they no longer fit.
/// <para>
/// Replaces the <c>Grid ColumnDefinitions="*,*"</c> that this app used for every
/// paired field. A star-sized grid always splits the space it is given, however
/// little that is, so at the smallest supported window the two halves were driven
/// down to roughly 200px each and the labels — "MAXIMUM CONCURRENT SESSIONS",
/// "NEW PROFILES DEFAULT TO" — were clipped mid-word. Nothing overflowed visibly,
/// so the page looked merely cramped rather than broken, and the settings were
/// unreadable exactly on the small screens most likely to be running a profile
/// manager alongside a browser.
/// </para>
/// <para>
/// The breakpoint is measured, not guessed at from the window size: this control
/// stacks when <em>its own</em> allocated width cannot seat both children at
/// <see cref="MinColumnWidth"/>. That keeps it correct inside the sidebar, inside
/// a dialog, and at every interface-scale step — a window-width media query would
/// have to be re-derived for each of those, and would be wrong at 150% zoom where
/// the window is unchanged but the effective space is a third smaller.
/// </para>
/// </summary>
public class FormPair : Panel
{
    /// <summary>
    /// Narrowest a column may be before the pair stacks.
    /// <para>
    /// 240px fits the longest label this app puts above a field without wrapping
    /// it to three lines. Exposed as a property so a caller with shorter labels
    /// can pack them tighter rather than stacking early.
    /// </para>
    /// </summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<FormPair, double>(nameof(MinColumnWidth), 240d);

    /// <summary>Gap between the columns, and between the rows once stacked.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FormPair, double>(nameof(Spacing), 20d);

    static FormPair()
    {
        // Both change the layout rather than just the paint, so a change has to
        // invalidate measure explicitly -- Panel does not assume that for
        // properties it does not know about.
        AffectsMeasure<FormPair>(MinColumnWidthProperty, SpacingProperty);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>True when the last layout pass stacked the children.</summary>
    public bool IsStacked { get; private set; }

    /// <summary>
    /// The breakpoint decision, free of any layout state.
    /// <para>
    /// Separated from <see cref="MeasureOverride"/> so the rule that actually
    /// broke the settings page can be tested without a window, a render loop or
    /// a headless Avalonia harness. Measuring a real <c>TextBlock</c> needs a
    /// text shaper and therefore a platform; this needs neither, so the
    /// regression stays covered by the ordinary unit test run.
    /// </para>
    /// </summary>
    public static bool ShouldStack(double availableWidth, double minColumnWidth, double spacing, int childCount)
    {
        // Fewer than two children can never be a "pair" -- nothing to stack.
        if (childCount < 2) return false;

        // Infinity means a parent is asking for our natural size rather than
        // offering a real width. Stacking there would make us permanently narrow.
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)) return false;

        var gaps = spacing * (childCount - 1);
        return availableWidth < (minColumnWidth * childCount) + gaps;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        if (children.Count == 0) return default;

        var spacing = Spacing;

        // One child behaves as a plain container. Callers do this when a field is
        // conditionally visible, and it must not be forced into half the width.
        if (children.Count == 1)
        {
            children[0].Measure(availableSize);
            IsStacked = false;
            return children[0].DesiredSize;
        }

        IsStacked = ShouldStack(availableSize.Width, MinColumnWidth, spacing, children.Count);

        return IsStacked
            ? MeasureStacked(children, availableSize, spacing)
            : MeasureSideBySide(children, availableSize, spacing);
    }

    private static Size MeasureSideBySide(Avalonia.Controls.Controls children, Size available, double spacing)
    {
        var gaps = spacing * (children.Count - 1);
        var column = double.IsInfinity(available.Width)
            ? double.PositiveInfinity
            : Math.Max(0, (available.Width - gaps) / children.Count);

        double width = 0, height = 0;

        foreach (var child in children)
        {
            child.Measure(new Size(column, available.Height));

            // The tallest child sets the row height: a two-line hint under one
            // field must not clip because its neighbour has a one-line hint.
            height = Math.Max(height, child.DesiredSize.Height);
            width += double.IsInfinity(column) ? child.DesiredSize.Width : column;
        }

        return new Size(width + gaps, height);
    }

    private static Size MeasureStacked(Avalonia.Controls.Controls children, Size available, double spacing)
    {
        double width = 0, height = 0;
        var first = true;

        foreach (var child in children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));

            width = Math.Max(width, child.DesiredSize.Width);
            if (!first) height += spacing;
            height += child.DesiredSize.Height;
            first = false;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        if (children.Count == 0) return finalSize;

        if (children.Count == 1)
        {
            children[0].Arrange(new Rect(finalSize));
            return finalSize;
        }

        var spacing = Spacing;

        if (IsStacked)
        {
            var y = 0d;
            foreach (var child in children)
            {
                var h = child.DesiredSize.Height;
                child.Arrange(new Rect(0, y, finalSize.Width, h));
                y += h + spacing;
            }

            return finalSize;
        }

        var gaps = spacing * (children.Count - 1);
        var column = Math.Max(0, (finalSize.Width - gaps) / children.Count);
        var x = 0d;

        foreach (var child in children)
        {
            // Full height, not DesiredSize: a field whose neighbour is taller
            // should still align its own label to the top of the row.
            child.Arrange(new Rect(x, 0, column, finalSize.Height));
            x += column + spacing;
        }

        return finalSize;
    }
}
