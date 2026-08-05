using SkiaSharp;

namespace CloakHub.Core.Branding;

/// <summary>
/// Draws the instance number onto the app icon.
/// <para>
/// Every rule in this class was settled by rendering the result and looking at
/// it at 1:1, not by reasoning about ratios. 16x16 — the size a Windows taskbar
/// and a Linux panel actually draw — is the binding case, and two earlier
/// versions of this code were wrong in ways only the render exposed.
/// </para>
/// <para>
/// The shape adapts to the caption: a circle for a single glyph, a pill for
/// two or three. That is not decoration. A circle's usable width is its inner
/// diameter, so at 16px it fits exactly one digit; the pill spends the icon's
/// full width instead and fits "42" legibly at 20px, where the circle cannot.
/// </para>
/// </summary>
public static class BadgeRenderer
{
    /// <summary>Icon sizes Windows expects inside a multi-resolution .ico.</summary>
    public static readonly int[] IcoSizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>
    /// Smallest font em size, in pixels, at which a badge caption still reads.
    /// <para>
    /// Two earlier attempts at this threshold were wrong, and both were wrong in
    /// the same way — they guessed at a proxy instead of measuring the thing that
    /// actually fails. First came <c>MinSizeForMultiCharCaption = 24</c>, keyed on
    /// icon size, which ignored that a pill fits "42" at 20px. Then came a
    /// minimum-pixels-per-glyph rule, which cannot distinguish 16px "1" from 16px
    /// "12": both lay out at 6px of advance per glyph, yet the first is crisp and
    /// the second is mush.
    /// </para>
    /// <para>
    /// The em size separates every observed case cleanly. Measured across the
    /// proof sheet, legible renders bottom out at 8.13px em and illegible ones top
    /// out at 7.82px, so the boundary sits in that gap. This is the right variable
    /// because it is the one the rasteriser sees: below roughly 8px a bold
    /// condensed digit no longer has enough vertical room for a stem and a counter
    /// to survive antialiasing, whatever the badge width allows.
    /// </para>
    /// </summary>
    public const float MinLegibleEmPx = 8f;

    /// <summary>Badge shape, chosen by caption length.</summary>
    private enum Shape { Circle, Pill }

    /// <summary>
    /// Geometry is size-dependent, and that is a correction rather than a
    /// refinement: a single ratio pair does not work.
    /// <para>
    /// The first version used one set of numbers for every size (0.52 diameter,
    /// 0.06 ring). Rendered and inspected at 16px the digit was illegible mush: a
    /// 52% badge leaves an 8px circle, the 1px ring eats two of those pixels, and
    /// a glyph in the remaining ~6px has nowhere to go.
    /// </para>
    /// <para>
    /// So small sizes get a proportionally larger badge and a thinner ring. The
    /// badge is more intrusive on the icon at 16px, which is the right trade: an
    /// unreadable number is worth nothing, and at that size the icon is a
    /// coloured blob anyway.
    /// </para>
    /// </summary>
    private static (float Extent, float Ring) Geometry(int size) => size switch
    {
        <= 20 => (0.74f, 0.045f),   // taskbar / panel: legibility wins over the icon
        <= 32 => (0.62f, 0.055f),
        _     => (0.52f, 0.060f),   // large: badge can be discreet
    };

    /// <summary>Fraction of the badge's inner width the caption may occupy.</summary>
    private const float CaptionWidthRatio = 0.86f;

    /// <summary>
    /// The badge's outer and inner rectangles for a given size and caption.
    /// <para>
    /// Both shapes are anchored to the bottom-right corner and inset by half a
    /// ring so the stroke is never clipped by the icon edge. The circle is square;
    /// the pill keeps that height but spans the icon's full width, which is the
    /// whole reason it fits more glyphs.
    /// </para>
    /// </summary>
    private static (SKRect Outer, SKRect Inner) Layout(int size, Shape shape)
    {
        var (extent, ringRatio) = Geometry(size);
        var ring = MathF.Max(1f, size * ringRatio);

        var height = size * extent;
        var width = shape == Shape.Pill ? size : height;
        var outer = new SKRect(size - width, size - height, size, size);
        outer.Inflate(-ring / 2f, -ring / 2f);

        var inner = outer;
        inner.Inflate(-ring, -ring);
        return (outer, inner);
    }

    /// <summary>
    /// Lay the caption out and report the em size it ends up at.
    /// <para>
    /// This is deliberately the single place where caption sizing happens, shared
    /// by <see cref="CaptionFits"/> and the renderer. Keeping the predicate and
    /// the drawing on one code path means the two cannot disagree — a check that
    /// approximates what the renderer does is a check that eventually lies.
    /// </para>
    /// </summary>
    private static float FitCaption(SKFont font, string text, SKRect inner)
    {
        // Start from the badge height, then shrink only if the caption overruns the
        // available width. Height is the limit for "1", width for "99+".
        font.Size = inner.Height * 0.95f;
        var measured = font.MeasureText(text);
        var maxWidth = inner.Width * CaptionWidthRatio;
        if (measured > maxWidth)
            font.Size *= maxWidth / MathF.Max(measured, 0.01f);
        return font.Size;
    }

    /// <summary>Bold condensed face used for captions, or the default if absent.</summary>
    private static SKFont CaptionFont() => new()
    {
        // Condensed rather than normal width: at these sizes the extra ~15% of
        // horizontal room per glyph is the difference between "42" reading and
        // "42" merging, and condensed bold keeps the stem weight that carries
        // legibility.
        Typeface = SKTypeface.FromFamilyName(
            null, SKFontStyleWeight.Bold, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
            ?? SKTypeface.Default,
    };

    /// <summary>
    /// Whether <paramref name="text"/> can be drawn legibly on a badge of this
    /// size, or must degrade to a plain dot.
    /// <para>
    /// A dot is the honest output when the caption cannot fit: a smudge reads as
    /// a rendering fault rather than as information. The dot still marks the
    /// window as a Hub session, and the exact number stays in the window title
    /// and the Hub's own session list, so nothing is actually lost.
    /// </para>
    /// </summary>
    public static bool CaptionFits(string text, int size)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var (_, inner) = Layout(size, ShapeFor(text));
        using var font = CaptionFont();
        return FitCaption(font, text, inner) >= MinLegibleEmPx;
    }

    private static Shape ShapeFor(string text) => text.Length > 1 ? Shape.Pill : Shape.Circle;

    /// <summary>
    /// Render one badged PNG.
    /// </summary>
    /// <param name="baseIcon">Source icon bytes (PNG). Null draws the badge on a transparent canvas.</param>
    /// <param name="text">Badge caption, e.g. "3" or "99+".</param>
    /// <param name="size">Output edge length in pixels.</param>
    /// <param name="accent">Badge fill colour.</param>
    public static byte[] RenderPng(byte[]? baseIcon, string text, int size, SKColor? accent = null)
    {
        var fill = accent ?? new SKColor(0x2F, 0x81, 0xF7);   // the Hub's accent blue

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // ---- base icon -------------------------------------------------
        // Wrapped in a catch, not just a null check: SKBitmap.Decode throws
        // ArgumentNullException on undecodable bytes rather than returning null
        // (it fails when constructing the codec). A corrupt or truncated icon file
        // must degrade to a badge-only render — branding is cosmetic and must
        // never be able to abort a launch.
        if (baseIcon is { Length: > 0 })
        {
            SKBitmap? bitmap = null;
            try { bitmap = SKBitmap.Decode(baseIcon); }
            catch (Exception) { /* unreadable icon — fall through to badge only */ }

            if (bitmap is not null)
            {
                using (bitmap)
                {
                    // SKSamplingOptions rather than the obsolete paint overload:
                    // SkiaSharp 3 removed SKPaint.FilterQuality. Linear + mipmap is
                    // the right choice here because the source icon is usually
                    // larger than the target (256 -> 16), i.e. a downscale.
                    var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                    canvas.DrawBitmap(bitmap, new SKRect(0, 0, size, size), sampling);
                }
            }
        }

        // ---- badge shape -----------------------------------------------
        // A caption that cannot be drawn legibly is dropped and the badge becomes
        // a plain dot; see CaptionFits for why that beats drawing a smudge.
        var caption = CaptionFits(text, size) ? text : string.Empty;
        // A dot keeps the circle: a pill with nothing in it would read as a
        // truncated label rather than as a deliberate marker.
        var (rect, inner) = Layout(size, ShapeFor(caption));

        // A contrasting ring is what makes the badge readable on top of an icon of
        // unknown colour; without it a blue badge vanishes into a blue logo.
        using (var ringPaint = new SKPaint { IsAntialias = true, Color = SKColors.White })
            canvas.DrawRoundRect(new SKRoundRect(rect, rect.Height / 2f), ringPaint);

        using (var fillPaint = new SKPaint { IsAntialias = true, Color = fill })
            canvas.DrawRoundRect(new SKRoundRect(inner, inner.Height / 2f), fillPaint);

        // ---- caption ---------------------------------------------------
        if (caption.Length == 0)
        {
            using var dotImage = surface.Snapshot();
            using var dotData = dotImage.Encode(SKEncodedImageFormat.Png, 100);
            return dotData.ToArray();
        }

        using var font = CaptionFont();
        using var textPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };

        FitCaption(font, caption, inner);
        font.MeasureText(caption, out var bounds);
        canvas.DrawText(caption, inner.MidX - bounds.MidX, inner.MidY - bounds.MidY,
            SKTextAlign.Left, font, textPaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Build a Windows <c>.ico</c> containing every size in <see cref="IcoSizes"/>.
    /// <para>
    /// Hand-written rather than taken from a library: the ICO container is a
    /// 6-byte header plus one 16-byte directory entry per image, and every image
    /// may be a PNG payload (Vista+). Adding a dependency for 40 lines of
    /// well-specified structure is a worse trade than writing it, and this way the
    /// exact set of sizes is under our control — the 16px entry is the one that
    /// decides whether the badge is legible in the taskbar.
    /// </para>
    /// </summary>
    public static byte[] BuildIco(byte[]? baseIcon, string text, SKColor? accent = null)
    {
        var images = IcoSizes.Select(s => RenderPng(baseIcon, text, s, accent)).ToList();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((ushort)0);                  // reserved
        w.Write((ushort)1);                  // type: 1 = icon
        w.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var size = IcoSizes[i];
            // 256 is encoded as 0 in the directory — the field is a single byte.
            w.Write((byte)(size >= 256 ? 0 : size));   // width
            w.Write((byte)(size >= 256 ? 0 : size));   // height
            w.Write((byte)0);                          // palette size
            w.Write((byte)0);                          // reserved
            w.Write((ushort)1);                        // colour planes
            w.Write((ushort)32);                       // bits per pixel
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var png in images) w.Write(png);

        w.Flush();
        return ms.ToArray();
    }
}
