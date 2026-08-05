using System.Text.Json;
using CloakHub.Core.Storage;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// The atomic-write and corruption-recovery guarantees.
/// <para>
/// These are tested against a real temporary directory rather than an abstracted
/// filesystem, because the guarantee being claimed is about actual
/// <c>rename</c> semantics. A mocked filesystem would only prove that the code
/// calls the methods it calls, which is the part that was never in doubt.
/// </para>
/// </summary>
public sealed class JsonStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cloakhub-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private sealed record Sample(string Name, int Count);

    // ---------------------------------------------------------------------
    // Round trip
    // ---------------------------------------------------------------------

    [Fact]
    public void A_written_value_reads_back_equal()
    {
        var path = Path_("sample.json");
        JsonStore.Write(path, new Sample("hello", 42));

        Assert.Equal(new Sample("hello", 42), JsonStore.Read<Sample?>(path, null));
    }

    [Fact]
    public void A_missing_file_returns_the_fallback_without_quarantining()
    {
        var result = JsonStore.Read(Path_("absent.json"), new Sample("default", 0), out var quarantined);

        // First launch is not an error, and must not litter the directory.
        Assert.Equal(new Sample("default", 0), result);
        Assert.Null(quarantined);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_missing_directory_returns_the_fallback()
    {
        var path = Path.Combine(_dir, "nested", "deep", "sample.json");
        Assert.Equal(new Sample("default", 0), JsonStore.Read(path, new Sample("default", 0)));
    }

    [Fact]
    public void Writing_creates_missing_directories()
    {
        var path = Path.Combine(_dir, "nested", "deep", "sample.json");
        JsonStore.Write(path, new Sample("made", 1));

        Assert.True(File.Exists(path));
    }

    // ---------------------------------------------------------------------
    // Corruption
    // ---------------------------------------------------------------------

    [Fact]
    public void Malformed_json_is_moved_aside_and_its_bytes_preserved()
    {
        var path = Path_("broken.json");
        File.WriteAllText(path, "{ this is not json at all");

        var result = JsonStore.Read(path, new Sample("default", 0), out var quarantined);

        Assert.Equal(new Sample("default", 0), result);
        Assert.NotNull(quarantined);
        Assert.False(File.Exists(path));

        // The point of quarantining rather than deleting: the user's bytes are still
        // on disk and recoverable by hand.
        Assert.Equal("{ this is not json at all", File.ReadAllText(quarantined!));
        Assert.Contains(".corrupt-", quarantined);
    }

    [Fact]
    public void An_empty_file_is_treated_as_absent_rather_than_corrupt()
    {
        // The normal result of a crash between create and write. There is nothing in
        // it worth preserving, so quarantining would only leave debris behind.
        var path = Path_("empty.json");
        File.WriteAllText(path, "   \n  ");

        var result = JsonStore.Read(path, new Sample("default", 0), out var quarantined);

        Assert.Equal(new Sample("default", 0), result);
        Assert.Null(quarantined);
    }

    [Fact]
    public void Two_corruptions_do_not_overwrite_each_others_backups()
    {
        var path = Path_("broken.json");

        File.WriteAllText(path, "first garbage");
        JsonStore.Read(path, new Sample("d", 0), out var first);

        File.WriteAllText(path, "second garbage");
        JsonStore.Read(path, new Sample("d", 0), out var second);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal("first garbage", File.ReadAllText(first!));
        Assert.Equal("second garbage", File.ReadAllText(second!));
    }

    [Fact]
    public void Json_of_the_wrong_shape_is_quarantined()
    {
        // Valid JSON, incompatible type. Same user-facing situation as malformed
        // input: the file cannot be used and must not block startup.
        var path = Path_("wrong-shape.json");
        File.WriteAllText(path, "[1, 2, 3]");

        var result = JsonStore.Read(path, new Sample("default", 0), out var quarantined);

        Assert.Equal(new Sample("default", 0), result);
        Assert.NotNull(quarantined);
    }

    // ---------------------------------------------------------------------
    // Atomicity
    // ---------------------------------------------------------------------

    [Fact]
    public void A_failed_serialisation_leaves_the_previous_file_intact()
    {
        var path = Path_("sample.json");
        JsonStore.Write(path, new Sample("good", 1));

        // A type System.Text.Json cannot serialise, to force a mid-write failure.
        Assert.ThrowsAny<Exception>(() => JsonStore.Write<object>(path, new Unserialisable()));

        // The whole reason for temp-file-then-rename: the good copy survives.
        Assert.Equal(new Sample("good", 1), JsonStore.Read<Sample?>(path, null));
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_files_behind()
    {
        var path = Path_("sample.json");
        JsonStore.Write(path, new Sample("good", 1));

        try { JsonStore.Write<object>(path, new Unserialisable()); } catch { /* expected */ }

        // Debris would accumulate over time and could be mistaken for real data.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void A_successful_write_leaves_no_temp_files_behind()
    {
        var path = Path_("sample.json");
        JsonStore.Write(path, new Sample("a", 1));
        JsonStore.Write(path, new Sample("b", 2));

        Assert.Single(Directory.GetFiles(_dir));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void Overwriting_an_existing_file_succeeds()
    {
        // Guards the Windows path specifically: File.Move refuses an existing
        // destination unless overwrite is requested, so a plain Move would work on
        // Linux and fail on Windows for every save after the first.
        var path = Path_("sample.json");
        JsonStore.Write(path, new Sample("first", 1));
        JsonStore.Write(path, new Sample("second", 2));

        Assert.Equal(new Sample("second", 2), JsonStore.Read<Sample?>(path, null));
    }

    [Fact]
    public void Concurrent_writes_all_complete_and_leave_a_readable_file()
    {
        // Concurrent writers are not serialised by JsonStore itself — the stores above
        // it do that. What must hold regardless is that interleaved renames never
        // leave a torn file: the result has to be one of the values written, whole.
        var path = Path_("sample.json");

        Parallel.For(0, 40, i => JsonStore.Write(path, new Sample($"writer-{i}", i)));

        var final = JsonStore.Read<Sample?>(path, null);
        Assert.NotNull(final);
        Assert.StartsWith("writer-", final!.Name);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    /// <summary>A type that cannot be serialised, to force a write failure.</summary>
    private sealed class Unserialisable
    {
        // A self-reference makes the serialiser throw rather than emit anything.
        public Unserialisable Self => this;
    }
}
