using System.Buffers.Binary;
using CloakHub.Core.Branding;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// The .icns container is hand-written, so its structure is worth verifying
/// rather than trusting. A malformed icon does not throw anywhere — macOS simply
/// shows a blank Dock tile, which is the hardest kind of bug to notice.
/// </summary>
public class IcnsWriterTests
{
    [Fact]
    public void The_declared_file_length_matches_the_actual_length()
    {
        // The header length counts the whole file. Getting this wrong is the single
        // most common way a hand-rolled .icns is silently rejected.
        var icns = IcnsWriter.Build(null, "3");

        Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(icns[..4]));
        Assert.Equal(icns.Length, BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(4, 4)));
    }

    [Fact]
    public void Every_chunk_is_a_png_of_the_size_its_type_declares()
    {
        var icns = IcnsWriter.Build(null, "7");
        var expected = IcnsWriter.Entries.ToDictionary(e => e.Type, e => e.Size);

        var offset = 8;
        var seen = 0;
        while (offset < icns.Length)
        {
            var type = System.Text.Encoding.ASCII.GetString(icns, offset, 4);
            var length = BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(offset + 4, 4));

            // The chunk length includes its own 8-byte header; a writer that omits
            // that produces a file that parses for one chunk and then desynchronises.
            Assert.True(length > 8, $"chunk {type} has no payload");
            Assert.True(offset + length <= icns.Length, $"chunk {type} overruns the file");

            var payload = icns.AsSpan(offset + 8, length - 8);
            Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], payload[..8].ToArray());

            // PNG IHDR carries the real dimensions; the type code must agree with it,
            // or the OS picks the wrong image for a given scale.
            var width = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(20, 4));
            Assert.Equal(expected[type], width);
            Assert.Equal(expected[type], height);

            offset += length;
            seen++;
        }

        // No trailing slack and no truncation: the chunk walk must land exactly on
        // the end of the file.
        Assert.Equal(icns.Length, offset);
        Assert.Equal(IcnsWriter.Entries.Length, seen);
    }

    [Fact]
    public void A_missing_size_is_skipped_rather_than_fatal()
    {
        // An icon with fewer resolutions still works. Branding must never be able
        // to abort a launch, so a gap degrades instead of throwing.
        var partial = new Dictionary<int, byte[]>
        {
            [16] = BadgeRenderer.RenderPng(null, "1", 16),
            [32] = BadgeRenderer.RenderPng(null, "1", 32),
        };

        var icns = IcnsWriter.Build(partial);

        Assert.Equal(icns.Length, BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(4, 4)));
        // Two chunks: 8-byte file header plus per-chunk header and payload.
        var expected = 8 + partial.Values.Sum(v => 8 + v.Length);
        Assert.Equal(expected, icns.Length);
    }

    [Fact]
    public void An_empty_payload_set_is_rejected()
    {
        // Writing a header-only .icns would produce a file that looks valid and
        // renders nothing, so this is one case worth failing loudly on — it can
        // only happen through a programming error, never through user input.
        Assert.Throws<ArgumentException>(() => IcnsWriter.Build(new Dictionary<int, byte[]>()));
    }

    [Fact]
    public void A_zero_length_payload_is_treated_as_absent()
    {
        var mixed = new Dictionary<int, byte[]>
        {
            [16] = [],
            [32] = BadgeRenderer.RenderPng(null, "1", 32),
        };

        var icns = IcnsWriter.Build(mixed);
        Assert.Equal(8 + 8 + mixed[32].Length, icns.Length);
    }
}
