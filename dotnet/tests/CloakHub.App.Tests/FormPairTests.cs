using CloakHub.App.Controls;

namespace CloakHub.App.Tests;

/// <summary>
/// Tests for the breakpoint behind <see cref="FormPair"/>.
/// <para>
/// The settings and profile-editor pages paired their fields with
/// <c>Grid ColumnDefinitions="*,*"</c>. A star-sized grid divides whatever width
/// it is handed, however little that is, so at the smallest supported window the
/// two halves were squeezed to roughly 200px and labels such as "MAXIMUM
/// CONCURRENT SESSIONS" were clipped mid-word. Nothing overflowed and nothing
/// threw, so the page read as merely cramped rather than broken.
/// </para>
/// <para>
/// The numbers below are the real ones: an 880px minimum window, less a 208px
/// sidebar and 44px of padding, leaves 628px of content — and a third of that
/// again at 150% interface scale.
/// </para>
/// </summary>
public class FormPairTests
{
    private const double Min = 240;
    private const double Spacing = 20;

    private static bool Stack(double width, int children = 2) =>
        FormPair.ShouldStack(width, Min, Spacing, children);

    // ------------------------------------------------------------------
    // The reported case.
    // ------------------------------------------------------------------

    [Fact]
    public void Stacks_at_the_smallest_window_and_largest_interface_scale()
    {
        // 628px of content at 150% scale leaves ~418px of effective space, which
        // cannot seat two 240px columns. This is the combination that clipped the
        // labels, and it must stack.
        Assert.True(Stack(628 / 1.5));
    }

    [Fact]
    public void Stays_side_by_side_at_the_default_window_and_scale()
    {
        // The common case must not regress into a tall single column just
        // because the narrow case was fixed. 1180px default window.
        Assert.False(Stack(1180 - 208 - 44));
    }

    [Theory]
    [InlineData(1.0, false)]   // 628px - comfortable, columns of 304px
    [InlineData(1.25, false)]  // 502px - columns of 241px, just clears the 240 floor
    [InlineData(1.5, true)]    // 418px - columns would be 199px; clipped before this fix
    public void Interface_scale_alone_can_trigger_the_breakpoint(double zoom, bool expected)
    {
        // The window is unchanged across these; only the effective space differs.
        // A window-width media query would miss this entirely, which is why the
        // control measures its own allocation instead.
        Assert.Equal(expected, Stack(628 / zoom));
    }

    // ------------------------------------------------------------------
    // The boundary.
    // ------------------------------------------------------------------

    [Fact]
    public void Exactly_enough_room_stays_side_by_side()
    {
        // 240 + 20 + 240. The breakpoint is inclusive so a pair that fits
        // precisely is not stacked for the sake of a rounding error.
        Assert.False(Stack(500));
    }

    [Fact]
    public void One_pixel_short_stacks()
    {
        Assert.True(Stack(499));
    }

    // ------------------------------------------------------------------
    // Degenerate inputs.
    // ------------------------------------------------------------------

    [Fact]
    public void An_unconstrained_width_never_stacks()
    {
        // Infinity means a parent is asking for our natural size, not offering
        // real space. Stacking would make the control permanently narrow inside
        // any horizontally scrolling container.
        Assert.False(Stack(double.PositiveInfinity));
        Assert.False(Stack(double.NaN));
    }

    [Fact]
    public void A_single_child_is_never_stacked()
    {
        // Callers pass one child when the second field is conditionally hidden.
        // It should fill the width rather than be treated as half a pair.
        Assert.False(Stack(100, children: 1));
        Assert.False(Stack(2000, children: 1));
    }

    [Fact]
    public void Three_children_need_room_for_three_columns()
    {
        // 3*240 + 2*20 = 760. The About row uses three items, and the rule has to
        // account for the extra gap rather than assuming a pair.
        Assert.False(Stack(760, children: 3));
        Assert.True(Stack(759, children: 3));
    }

    [Fact]
    public void A_zero_width_stacks_rather_than_dividing_nothing()
    {
        // Happens transiently during the first layout pass; dividing zero into
        // columns is what produced unreadable slivers before.
        Assert.True(Stack(0));
    }
}
