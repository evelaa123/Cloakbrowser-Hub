using System.IO.Compression;
using System.Text;
using CloakHub.Core.Import;

namespace CloakHub.Core.Tests;

/// <summary>
/// Extraction is a place where a mistake writes files outside the directory it was
/// asked to fill.
/// <para>
/// Only the silent cases are asserted here. An extractor that throws is a bug the
/// user reports; one that quietly drops <c>../../</c> and writes over a file in the
/// home directory produces no error at all, and the archive that did it looks
/// ordinary. So these tests are about the paths that <i>succeed</i> when they should
/// not, plus the two limits whose absence only shows up as a full disk.
/// </para>
/// </summary>
public class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"cloakhub-extract-test-{Guid.NewGuid():N}");

    public ArchiveExtractorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A test-run leftover in the temp directory is not worth failing over.
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Path traversal
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("../../escaped.txt")]
    [InlineData("good/../../escaped.txt")]
    [InlineData("./../escaped.txt")]
    // Backslashes are a legal filename character on Linux, so a naive parser treats
    // this as one long segment and lets it through -- then it escapes the moment the
    // extracted tree is opened on Windows.
    [InlineData("..\\escaped.txt")]
    [InlineData("good\\..\\..\\escaped.txt")]
    // Absolute paths and drive letters are never valid inside an archive, and an
    // extractor that simply joins them would write to the literal location.
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    public void An_entry_that_climbs_out_of_the_directory_is_refused(string entry)
    {
        Assert.Null(ArchiveExtractor.SafeEntryPath(_root, entry));
    }

    [Theory]
    [InlineData("Default/Cookies")]
    [InlineData("./Default/Cookies")]
    [InlineData("a/b/c/d.txt")]
    // A file named ".." plus more characters is not a traversal, and rejecting it
    // would silently drop legitimate entries.
    [InlineData("..foo/bar")]
    [InlineData("foo/..bar")]
    public void An_ordinary_entry_is_accepted(string entry)
    {
        var dest = ArchiveExtractor.SafeEntryPath(_root, entry);

        Assert.NotNull(dest);
        Assert.True(ArchiveExtractor.IsInside(_root, dest));
    }

    [Fact]
    public void A_sibling_directory_with_a_shared_prefix_is_not_inside()
    {
        // The classic way this check is written wrong is a bare StartsWith, which
        // accepts "/tmp/x-evil" as being inside "/tmp/x". Nothing about the resulting
        // extraction looks unusual.
        var inside = Path.Combine(_root, "x");
        var sibling = _root + Path.DirectorySeparatorChar + "x-evil";

        Assert.False(ArchiveExtractor.IsInside(inside, sibling));
        Assert.True(ArchiveExtractor.IsInside(inside, Path.Combine(inside, "child")));
    }

    [Fact]
    public void An_entry_with_no_usable_name_is_skipped_rather_than_written()
    {
        // "." and "/" collapse to nothing. Joining an empty segment list onto the
        // root would target the root directory itself, which as a file write is
        // either an error or -- worse -- a silent no-op that reports success.
        Assert.Null(ArchiveExtractor.SafeEntryPath(_root, "."));
        Assert.Null(ArchiveExtractor.SafeEntryPath(_root, "./"));
        Assert.Null(ArchiveExtractor.SafeEntryPath(_root, ""));
        Assert.Null(ArchiveExtractor.SafeEntryPath(_root, "   "));
    }

    // ------------------------------------------------------------------
    // End-to-end: the traversal must not reach the disk
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_zip_holding_a_traversal_entry_extracts_without_escaping()
    {
        var archive = Path.Combine(_root, "evil.zip");
        var target = Path.Combine(_root, "out");
        var outside = Path.Combine(_root, "escaped.txt");

        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "Default/Preferences", "{}");
            Write(zip, "../escaped.txt", "owned");
        }

        var result = await ArchiveExtractor.ExtractAsync(archive, target);

        // The extraction still succeeds. Refusing the whole archive over one bad
        // entry would make a single malformed path lose a user their profile, and
        // the entry itself is reported instead.
        Assert.True(result.Ok);
        Assert.False(File.Exists(outside));
        Assert.True(File.Exists(Path.Combine(target, "Default", "Preferences")));
        Assert.NotEmpty(result.Skipped);
    }

    [Fact]
    public async Task A_skipped_entry_is_named_rather_than_only_counted()
    {
        // A count alone leaves a user whose profile is missing unable to tell whether
        // it was refused or was never in the archive.
        var archive = Path.Combine(_root, "named.zip");
        var target = Path.Combine(_root, "named-out");

        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "../escaped.txt", "owned");
        }

        var result = await ArchiveExtractor.ExtractAsync(archive, target);

        Assert.Contains(result.Skipped, s => s.Contains("escaped.txt", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Supported types
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("profile.zip")]
    [InlineData("profile.ZIP")]
    [InlineData("profile.tar.gz")]
    [InlineData("profile.tgz")]
    [InlineData("profile.tar")]
    public void The_archive_types_the_extractor_handles_are_recognised(string name)
    {
        Assert.True(ArchiveExtractor.IsSupported(name));
    }

    [Theory]
    [InlineData("profile.rar")]
    [InlineData("profile.7z")]
    [InlineData("profile.gz")]
    // ".targz" is not ".tar.gz"; a suffix test written with the dot dropped would
    // accept it and then fail deep inside the reader with an unhelpful message.
    [InlineData("profile.targz")]
    [InlineData("Cookies")]
    public void An_unsupported_archive_type_is_rejected_before_it_is_opened(string name)
    {
        Assert.False(ArchiveExtractor.IsSupported(name));
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
