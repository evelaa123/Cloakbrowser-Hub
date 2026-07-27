using System.Formats.Tar;
using System.IO.Compression;

namespace CloakHub.Core.Import;

/// <summary>
/// Unpack an archive so its contents can be scanned for browser profiles.
/// <para>
/// The archive is untrusted input — the whole point of this feature is that the
/// user received it from somewhere else — so two safety properties are enforced
/// rather than assumed:
/// </para>
/// <list type="number">
///   <item><b>Path traversal.</b> An entry named
///     <c>../../../../.ssh/authorized_keys</c> is the well-known "zip slip" attack;
///     every destination is resolved and verified to stay inside the extraction
///     root.</item>
///   <item><b>Resource exhaustion.</b> A zip bomb expands to terabytes from a few
///     KB, so total extracted bytes and entry count are both capped.</item>
/// </list>
/// <para>
/// Symlink and device entries are skipped entirely rather than recreated: a symlink
/// is the other half of a traversal escape — the path check passes, and the write
/// then follows the link out — and a browser profile has no legitimate need for
/// one.
/// </para>
/// <para>
/// <b>Where .NET beats the Node original:</b> the Electron build needed a
/// third-party <c>yauzl</c> dependency and supported <c>.zip</c> only.
/// <c>System.IO.Compression</c> and <c>System.Formats.Tar</c> are both in the base
/// library, so <c>.tar.gz</c> — which is what a Linux or macOS profile backup
/// actually looks like — works here with no extra dependency.
/// </para>
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>Total uncompressed bytes allowed. A browser profile is large, but not this large.</summary>
    public const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Maximum entries, as a guard against millions of tiny files.</summary>
    public const int MaxEntries = 200_000;

    /// <summary>Archive extensions this build can extract.</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
        [".zip", ".tar.gz", ".tgz", ".tar"];

    public static bool IsSupported(string file)
    {
        var lower = file.ToLowerInvariant();
        return SupportedExtensions.Any(ext => lower.EndsWith(ext, StringComparison.Ordinal));
    }

    /// <summary>A fresh temp directory to unpack into.</summary>
    public static string NewExtractionDir() =>
        Path.Combine(Path.GetTempPath(), $"cloakbrowser-hub-import-{Guid.NewGuid():N}");

    /// <summary>
    /// Is <paramref name="candidate"/> inside <paramref name="root"/>?
    /// <para>
    /// Compared on resolved paths with a separator suffix, so <c>/tmp/x-evil</c> is
    /// not accepted as being inside <c>/tmp/x</c> — a prefix test without the
    /// separator is the classic way this check is written wrong.
    /// </para>
    /// </summary>
    public static bool IsInside(string root, string candidate)
    {
        var r = Path.GetFullPath(root);
        var c = Path.GetFullPath(candidate);

        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (string.Equals(r, c, comparison)) return true;

        var sep = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return c.StartsWith(sep, comparison);
    }

    /// <summary>
    /// Decide the on-disk destination for an archive entry, or null to skip it.
    /// <para>
    /// Public because the traversal check is the security-relevant part of this
    /// module and deserves to be asserted directly rather than inferred from
    /// whether an extraction happened to stay put.
    /// </para>
    /// </summary>
    public static string? SafeEntryPath(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;

        // Archive entries use forward slashes by spec, but real archives contain
        // backslashes too — produced by broken Windows tooling — so both are
        // treated as separators. On Linux a backslash is a legal filename
        // character, but accepting "..\..\etc" as one segment would let a Windows
        // -authored archive escape once the file was later opened on Windows.
        var normalised = entryName.Replace('\\', '/');

        // Absolute paths and drive letters are never valid inside an archive.
        if (normalised.StartsWith('/')) return null;
        if (normalised.Length >= 2 && normalised[1] == ':' && char.IsAsciiLetter(normalised[0])) return null;

        var segments = new List<string>();
        foreach (var segment in normalised.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;

            // Rejected outright rather than normalised away: a normalising parser
            // and the OS can disagree about what "a/../../b" means, and the whole
            // class of bug lives in that gap.
            if (segment == "..") return null;

            segments.Add(segment);
        }

        if (segments.Count == 0) return null;

        var dest = Path.Combine([root, .. segments]);
        return IsInside(root, dest) ? dest : null;
    }

    /// <summary>Extract <paramref name="archive"/> into <paramref name="dir"/>, creating it.</summary>
    public static async Task<ExtractResult> ExtractAsync(
        string archive,
        string dir,
        CancellationToken cancel = default)
    {
        if (!File.Exists(archive))
            return ExtractResult.Failed(dir, "That archive no longer exists.");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            return ExtractResult.Failed(dir, $"Could not create a temporary folder: {e.Message}");
        }

        var lower = archive.ToLowerInvariant();
        try
        {
            if (lower.EndsWith(".zip", StringComparison.Ordinal))
                return await ExtractZipAsync(archive, dir, cancel).ConfigureAwait(false);

            if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) ||
                lower.EndsWith(".tgz", StringComparison.Ordinal) ||
                lower.EndsWith(".tar", StringComparison.Ordinal))
            {
                return await ExtractTarAsync(archive, dir, gzip: !lower.EndsWith(".tar", StringComparison.Ordinal), cancel)
                    .ConfigureAwait(false);
            }

            return ExtractResult.Failed(dir,
                $"Unsupported archive type. This build can read: {string.Join(", ", SupportedExtensions)}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return ExtractResult.Failed(dir, $"Could not read the archive: {e.Message}");
        }
    }

    private static async Task<ExtractResult> ExtractZipAsync(
        string archive,
        string dir,
        CancellationToken cancel)
    {
        var state = new Progress(dir);

        using var zip = ZipFile.OpenRead(archive);

        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            if (state.OverBudget(out var stop)) return stop;

            // Directory entries end in '/' by spec and have no content.
            var isDirectory = entry.FullName.Replace('\\', '/').EndsWith('/');

            // The upper 16 bits of ExternalAttributes carry the Unix mode when the
            // archive was written on a Unix host. S_IFLNK is 0xA000.
            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if ((mode & 0xF000) == 0xA000)
            {
                state.Skip($"{entry.FullName} (symlink)");
                continue;
            }

            var dest = SafeEntryPath(dir, entry.FullName);
            if (dest is null)
            {
                state.Skip($"{entry.FullName} (unsafe path)");
                continue;
            }

            if (isDirectory)
            {
                state.MakeDirectory(dest, entry.FullName);
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                await using var source = entry.Open();
                await using var target = File.Create(dest);
                state.Bytes += await CopyCappedAsync(source, target, state, cancel).ConfigureAwait(false);
                state.Entries++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                state.Skip($"{entry.FullName} ({e.Message})");
            }
        }

        return state.Done();
    }

    private static async Task<ExtractResult> ExtractTarAsync(
        string archive,
        string dir,
        bool gzip,
        CancellationToken cancel)
    {
        var state = new Progress(dir);

        await using var file = File.OpenRead(archive);
        await using Stream stream = gzip
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;

        await using var reader = new TarReader(stream);

        while (await reader.GetNextEntryAsync(cancellationToken: cancel).ConfigureAwait(false) is { } entry)
        {
            cancel.ThrowIfCancellationRequested();
            if (state.OverBudget(out var stop)) return stop;

            // Hard links, char/block devices and FIFOs are all ways to write
            // somewhere the path check already approved and then have it mean
            // something else. A profile contains none of them.
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                state.Skip($"{entry.Name} (link)");
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile
                or TarEntryType.Directory))
            {
                state.Skip($"{entry.Name} (unsupported entry type {entry.EntryType})");
                continue;
            }

            var dest = SafeEntryPath(dir, entry.Name);
            if (dest is null)
            {
                state.Skip($"{entry.Name} (unsafe path)");
                continue;
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                state.MakeDirectory(dest, entry.Name);
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                await using var target = File.Create(dest);
                if (entry.DataStream is { } data)
                    state.Bytes += await CopyCappedAsync(data, target, state, cancel).ConfigureAwait(false);

                state.Entries++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                state.Skip($"{entry.Name} ({e.Message})");
            }
        }

        return state.Done();
    }

    /// <summary>
    /// Copy with the byte budget enforced <i>during</i> the copy.
    /// <para>
    /// Checking the total only between entries is not enough: a bomb can be a
    /// single entry whose declared size is a lie, and by the time the copy returned
    /// the disk would already be full. The cap has to bite mid-stream.
    /// </para>
    /// </summary>
    private static async Task<long> CopyCappedAsync(
        Stream source,
        Stream target,
        Progress state,
        CancellationToken cancel)
    {
        var buffer = new byte[81920];
        long written = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0) break;

            if (state.Bytes + written + read > MaxTotalBytes)
                throw new InvalidOperationException("entry exceeds the 4 GB extraction budget");

            await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            written += read;
        }

        return written;
    }

    /// <summary>Delete an extraction directory. Best effort — a leftover temp dir is not fatal.</summary>
    public static void Cleanup(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // The OS clears its temp directory eventually, and failing to tidy up
            // must never turn a successful import into a reported error.
        }
    }

    /// <summary>Mutable accounting shared by both format readers.</summary>
    private sealed class Progress(string dir)
    {
        public int Entries;
        public long Bytes;
        private readonly List<string> _skipped = [];
        private bool _truncated;

        public void Skip(string reason)
        {
            // Bounded: a hostile archive with 200 000 bad entries would otherwise
            // move the memory exhaustion from the disk into this list.
            if (_skipped.Count < 200) _skipped.Add(reason);
        }

        public void MakeDirectory(string dest, string name)
        {
            try
            {
                Directory.CreateDirectory(dest);
            }
            catch
            {
                Skip($"{name} (could not create directory)");
            }
        }

        public bool OverBudget(out ExtractResult result)
        {
            if (Entries >= MaxEntries)
            {
                _truncated = true;
                result = ExtractResult.Failed(dir, $"Archive has more than {MaxEntries:N0} entries; refusing to extract.");
                return true;
            }

            if (Bytes > MaxTotalBytes)
            {
                _truncated = true;
                result = ExtractResult.Failed(dir, "Archive expands to more than 4 GB; refusing to extract.");
                return true;
            }

            result = null!;
            return false;
        }

        public ExtractResult Done() => new()
        {
            Ok = true,
            Dir = dir,
            Entries = Entries,
            Bytes = Bytes,
            Skipped = _skipped,
            Truncated = _truncated,
        };
    }
}

/// <summary>
/// What an extraction produced.
/// <para>
/// <see cref="Skipped"/> is surfaced rather than hidden. An archive that lost half
/// its entries to a traversal check is not an archive the user should import from
/// silently — either it is malicious, or their packing tool is broken, and both are
/// worth knowing before the resulting profile is trusted with an account.
/// </para>
/// </summary>
public sealed record ExtractResult
{
    public bool Ok { get; init; }

    /// <summary>Directory the archive was unpacked into.</summary>
    public required string Dir { get; init; }

    public int Entries { get; init; }
    public long Bytes { get; init; }
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public bool Truncated { get; init; }
    public string? Error { get; init; }

    public static ExtractResult Failed(string dir, string error) =>
        new() { Ok = false, Dir = dir, Error = error };
}
