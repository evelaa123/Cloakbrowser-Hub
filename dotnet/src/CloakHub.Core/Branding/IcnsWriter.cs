using System.Buffers.Binary;

namespace CloakHub.Core.Branding;

/// <summary>
/// Builds a macOS <c>.icns</c> icon container.
/// <para>
/// Hand-written for the same reason as the <c>.ico</c> writer in
/// <see cref="BadgeRenderer"/>: the container is a short, stable, well-specified
/// structure, and the alternative on a non-mac host is shelling out to
/// <c>iconutil</c>, which does not exist there. Generating the bundle icon must
/// work when the Hub is cross-building assets, so it cannot depend on Apple
/// tooling being present.
/// </para>
/// <para>
/// Layout: the magic <c>icns</c>, a big-endian total file length, then a
/// sequence of chunks. Each chunk is a four-character type, a big-endian length
/// that <em>includes</em> the 8-byte chunk header, and the payload. Modern types
/// take a PNG payload directly, which is what lets this reuse the PNG renders.
/// </para>
/// </summary>
public static class IcnsWriter
{
    /// <summary>
    /// The icon types written, paired with the pixel size each one must contain.
    /// <para>
    /// These are the PNG-capable types. The <c>icp*</c> family covers the small
    /// sizes and <c>ic07</c>/<c>ic08</c> the large ones; together they give the
    /// Dock, Finder and Cmd-Tab switcher something to pick from at every scale.
    /// <c>ic08</c> (256px) is included because the Dock renders large and an
    /// upscaled 128 looks visibly soft next to other apps.
    /// </para>
    /// </summary>
    public static readonly (string Type, int Size)[] Entries =
    [
        ("icp4", 16),
        ("icp5", 32),
        ("icp6", 64),
        ("ic07", 128),
        ("ic08", 256),
    ];

    /// <summary>Sizes an <c>.icns</c> needs rendered.</summary>
    public static int[] Sizes => [.. Entries.Select(e => e.Size)];

    /// <summary>
    /// Assemble an <c>.icns</c> from PNG payloads keyed by pixel size.
    /// </summary>
    /// <param name="pngBySize">
    /// PNG bytes for each size in <see cref="Sizes"/>. A missing size is skipped
    /// rather than treated as fatal: an icon with fewer resolutions still works,
    /// and branding must never be able to abort a launch.
    /// </param>
    public static byte[] Build(IReadOnlyDictionary<int, byte[]> pngBySize)
    {
        var chunks = new List<(string Type, byte[] Png)>();
        foreach (var (type, size) in Entries)
            if (pngBySize.TryGetValue(size, out var png) && png.Length > 0)
                chunks.Add((type, png));

        if (chunks.Count == 0)
            throw new ArgumentException("No icon payloads supplied.", nameof(pngBySize));

        // 8-byte file header, then 8 bytes of chunk header per image.
        var total = 8 + chunks.Sum(c => 8 + c.Png.Length);

        var buffer = new byte[total];
        var span = buffer.AsSpan();

        WriteTag(span[..4], "icns");
        BinaryPrimitives.WriteInt32BigEndian(span[4..8], total);

        var offset = 8;
        foreach (var (type, png) in chunks)
        {
            WriteTag(span.Slice(offset, 4), type);
            // The length field counts its own header, which is the detail most
            // hand-rolled writers get wrong and which makes Finder reject the file.
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(offset + 4, 4), 8 + png.Length);
            png.CopyTo(span[(offset + 8)..]);
            offset += 8 + png.Length;
        }

        return buffer;
    }

    /// <summary>Render and assemble in one step.</summary>
    public static byte[] Build(byte[]? baseIcon, string badgeText)
    {
        var bySize = Sizes.Distinct().ToDictionary(
            s => s,
            s => BadgeRenderer.RenderPng(baseIcon, badgeText, s));
        return Build(bySize);
    }

    private static void WriteTag(Span<byte> target, string tag)
    {
        // ASCII by specification — the four-character codes are all letters and
        // digits, so this cannot silently truncate a multi-byte character.
        for (var i = 0; i < 4; i++) target[i] = (byte)tag[i];
    }
}
