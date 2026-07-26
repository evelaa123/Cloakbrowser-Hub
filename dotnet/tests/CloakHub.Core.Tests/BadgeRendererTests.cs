using CloakHub.Core.Branding;
using SkiaSharp;

namespace CloakHub.Core.Tests;

public class BadgeRendererTests
{
    private static byte[] SolidIcon(int size, SKColor colour)
    {
        var info = new SKImageInfo(size, size);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(colour);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Renders_a_decodable_png_at_the_requested_size()
    {
        var png = BadgeRenderer.RenderPng(null, "1", 64);
        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(64, decoded.Height);
    }

    [Fact]
    public void Renders_at_the_size_that_actually_matters()
    {
        // 16px is what a Windows taskbar and a Linux panel render. If the badge
        // survives nowhere else, it has to survive here.
        var png = BadgeRenderer.RenderPng(null, "9", 16);
        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(16, decoded.Width);
    }

    [Fact]
    public void Badge_lands_in_the_bottom_right_quadrant()
    {
        var png = BadgeRenderer.RenderPng(null, "1", 64);
        using var bmp = SKBitmap.Decode(png);

        // The top-left corner must stay untouched (transparent, since no base
        // icon was supplied) — otherwise the badge is covering the icon it is
        // supposed to annotate.
        Assert.Equal(0, bmp.GetPixel(2, 2).Alpha);
        // The bottom-right must be painted.
        Assert.True(bmp.GetPixel(56, 56).Alpha > 0, "badge should be drawn bottom-right");
    }

    [Fact]
    public void Base_icon_is_preserved_outside_the_badge()
    {
        var red = SolidIcon(64, SKColors.Red);
        var png = BadgeRenderer.RenderPng(red, "1", 64);
        using var bmp = SKBitmap.Decode(png);

        var topLeft = bmp.GetPixel(4, 4);
        Assert.Equal(255, topLeft.Alpha);
        Assert.True(topLeft.Red > 200 && topLeft.Green < 60, $"expected red base, got {topLeft}");
    }

    [Fact]
    public void Badge_has_a_contrasting_ring_so_it_reads_on_a_same_coloured_icon()
    {
        // A blue badge on a blue logo would otherwise vanish. The white ring is
        // the only thing separating them, so it is worth asserting rather than
        // trusting by eye.
        var blue = SolidIcon(64, new SKColor(0x2F, 0x81, 0xF7));
        var png = BadgeRenderer.RenderPng(blue, "1", 64, new SKColor(0x2F, 0x81, 0xF7));
        using var bmp = SKBitmap.Decode(png);

        // Scan the diagonal approaching the badge centre; a near-white pixel must
        // exist between the icon body and the badge fill.
        var foundLight = false;
        for (var i = 30; i < 64; i++)
        {
            var p = bmp.GetPixel(i, i);
            if (p.Red > 230 && p.Green > 230 && p.Blue > 230) { foundLight = true; break; }
        }
        Assert.True(foundLight, "expected a light ring between the icon and the badge fill");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData("42")]
    [InlineData("99+")]
    public void All_caption_shapes_render(string text)
    {
        foreach (var size in new[] { 16, 32, 64, 256 })
        {
            var png = BadgeRenderer.RenderPng(null, text, size);
            using var bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);
            Assert.Equal(size, bmp.Width);
        }
    }

    [Fact]
    public void A_wide_caption_still_fits_inside_the_badge()
    {
        // "99+" is three glyphs in a badge sized for one or two: the renderer
        // shrinks the font to fit rather than letting it spill.
        //
        // This asserts containment by scanning for ink rather than by probing one
        // hardcoded pixel. The earlier version checked (20,60) was transparent,
        // which silently encoded "the badge is a circle" — when the badge became a
        // pill for multi-glyph captions that point moved legitimately inside it and
        // the test failed while the behaviour was correct. A test should pin the
        // rule (ink stays within the badge band), not the shape that satisfied it.
        const int size = 64;
        var png = BadgeRenderer.RenderPng(null, "99+", size);
        using var bmp = SKBitmap.Decode(png);

        // The badge is anchored bottom-right and is at most 74% of the icon tall,
        // so with a generous margin every drawn pixel must sit below the midline.
        for (var y = 0; y < size / 4; y++)
            for (var x = 0; x < size; x++)
                Assert.Equal(0, bmp.GetPixel(x, y).Alpha);

        // And the caption really was drawn — otherwise the scan above passes
        // trivially on an empty canvas.
        Assert.True(CountOpaque(bmp) > 0, "expected the badge to have been drawn");
    }

    private static int CountOpaque(SKBitmap bmp)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 0) n++;
        return n;
    }

    // ---------------------------------------------------------------------
    // Legibility rules. These encode the thresholds that the rendered proof
    // sheet established, so a future "tidy-up" of the ratios cannot quietly
    // reintroduce the unreadable 16px badge.

    [Theory]
    [InlineData("1", 16)]     // one glyph always fits, even in a taskbar icon
    [InlineData("9", 16)]
    [InlineData("42", 24)]    // two glyphs need a pill, and fit from 20px up
    [InlineData("99+", 32)]   // three glyphs need real room
    public void Captions_that_fit_are_drawn(string text, int size)
        => Assert.True(BadgeRenderer.CaptionFits(text, size),
            $"\"{text}\" should be legible at {size}px");

    [Theory]
    [InlineData("99+", 16)]   // ~3px per glyph: mush
    [InlineData("99+", 20)]   // verified illegible on the comparison sheet
    [InlineData("12", 16)]    // a 16px circle fits exactly one digit
    public void Captions_that_cannot_fit_degrade_to_a_dot(string text, int size)
    {
        Assert.False(BadgeRenderer.CaptionFits(text, size));

        // The dot is a real badge, not a blank canvas: it still marks the window.
        var dot = BadgeRenderer.RenderPng(null, text, size);
        using var bmp = SKBitmap.Decode(dot);
        Assert.True(CountOpaque(bmp) > 0, "the dot fallback must still draw a badge");

        // And it carries no caption ink, so it cannot be a smudge. A dot is drawn
        // with the fill colour ringed in white; a caption would add white pixels
        // inside the fill, so compare against a single-glyph render of the same
        // size, which must contain strictly more white.
        var withText = BadgeRenderer.RenderPng(null, "1", size);
        using var textBmp = SKBitmap.Decode(withText);
        Assert.True(CountWhite(textBmp) > CountWhite(bmp),
            "the dot fallback should have less white ink than a captioned badge");
    }

    private static int CountWhite(SKBitmap bmp)
    {
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha > 200 && p.Red > 230 && p.Green > 230 && p.Blue > 230) n++;
            }
        return n;
    }

    [Fact]
    public void Legibility_is_monotonic_in_size()
    {
        // A caption that fits at some size must fit at every larger size. This is
        // the invariant the per-glyph rule exists to guarantee; a size-banded
        // ratio table can violate it by accident.
        foreach (var text in new[] { "1", "12", "99+" })
        {
            var fitted = false;
            foreach (var size in BadgeRenderer.IcoSizes)
            {
                var fits = BadgeRenderer.CaptionFits(text, size);
                if (fitted) Assert.True(fits, $"\"{text}\" regressed at {size}px");
                fitted |= fits;
            }
            Assert.True(fitted, $"\"{text}\" never fits at any icon size");
        }
    }

    [Fact]
    public void A_broken_base_icon_degrades_to_a_badge_only_render()
    {
        // Branding is cosmetic; a corrupt icon file must not stop a launch.
        var png = BadgeRenderer.RenderPng([1, 2, 3, 4], "1", 32);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        Assert.Equal(32, bmp.Width);
    }

    // ---------------------------------------------------------------------
    // ICO container. Hand-written, so the structure is worth verifying.
    // ---------------------------------------------------------------------

    [Fact]
    public void Ico_has_a_valid_header_and_one_entry_per_size()
    {
        var ico = BadgeRenderer.BuildIco(null, "3");
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));   // reserved
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));   // type = icon
        Assert.Equal(BadgeRenderer.IcoSizes.Length, BitConverter.ToUInt16(ico, 4));
    }

    [Fact]
    public void Ico_directory_offsets_and_lengths_stay_inside_the_file()
    {
        var ico = BadgeRenderer.BuildIco(null, "3");
        var count = BitConverter.ToUInt16(ico, 4);

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            Assert.True(offset + length <= ico.Length,
                $"entry {i} points past the end of the file ({offset}+{length} > {ico.Length})");
            Assert.True(length > 0, $"entry {i} has no payload");
        }
    }

    [Fact]
    public void Every_ico_entry_holds_a_decodable_png_of_the_declared_size()
    {
        var ico = BadgeRenderer.BuildIco(null, "7");
        var count = BitConverter.ToUInt16(ico, 4);

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var declared = ico[entry] == 0 ? 256 : ico[entry];
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);

            using var bmp = SKBitmap.Decode(ico.AsSpan(offset, length).ToArray());
            Assert.NotNull(bmp);
            Assert.Equal(declared, bmp.Width);
        }
    }

    [Fact]
    public void Ico_encodes_256_as_zero_in_the_directory()
    {
        // The width/height fields are a single byte each, so 256 must be written
        // as 0. Writing 256 truncates to 0 anyway, but relying on that is how the
        // largest icon silently goes missing in some readers.
        var ico = BadgeRenderer.BuildIco(null, "1");
        var count = BitConverter.ToUInt16(ico, 4);
        var index = Array.IndexOf(BadgeRenderer.IcoSizes, 256);
        if (index < 0) return;   // 256 not in the set; nothing to assert
        Assert.True(index < count);
        Assert.Equal(0, ico[6 + 16 * index]);
    }

    [Fact]
    public void Ico_includes_the_16px_size()
    {
        Assert.Contains(16, BadgeRenderer.IcoSizes);
    }
}
