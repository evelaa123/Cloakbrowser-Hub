using System.Text.Json;

namespace CloakHub.Core.Storage;

/// <summary>
/// Atomic JSON file persistence.
/// <para>
/// Writes go to a temporary file and are then renamed over the target, because
/// <c>rename</c> is atomic on every filesystem the Hub runs on. A write that is
/// interrupted — power loss, a kill, a full disk — therefore leaves the previous
/// good copy untouched instead of a half-written file. That matters more here
/// than it would for most settings files: <c>profiles.json</c> is the only record
/// of the user's browsing identities, and some of them may have been aged for
/// months to look established.
/// </para>
/// <para>
/// A file that cannot be parsed is moved aside rather than deleted. The user gets
/// a working app back, and the unreadable bytes stay on disk where they can be
/// recovered by hand or by a support request. Silently starting from empty would
/// look identical to the app having thrown their work away.
/// </para>
/// </summary>
public static class JsonStore
{
    /// <summary>
    /// Read and deserialise, or return <paramref name="fallback"/>.
    /// <para>
    /// A missing file is not an error — it is what first launch looks like. Only a
    /// file that exists and cannot be understood triggers quarantine.
    /// </para>
    /// </summary>
    /// <param name="quarantined">
    /// The path the corrupt file was moved to, when that happened. Surfaced so the
    /// caller can tell the user where their data went; a silent recovery is
    /// indistinguishable from data loss from the outside.
    /// </param>
    public static T Read<T>(string path, T fallback, out string? quarantined)
    {
        quarantined = null;

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (FileNotFoundException) { return fallback; }
        catch (DirectoryNotFoundException) { return fallback; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable, but not proven corrupt. Quarantining here would
            // move a perfectly good file aside over a transient condition such as a
            // virus scanner holding a handle, so the fallback is returned and the
            // file is left exactly as it is.
            return fallback;
        }

        // An empty or whitespace-only file is the normal result of a crash between
        // create and write. Treated as absent rather than corrupt: there is nothing
        // in it worth preserving, so quarantining would only litter the directory.
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(raw, ProfileMigration.JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            quarantined = Quarantine(path);
            return fallback;
        }
        catch (NotSupportedException)
        {
            // Thrown for a shape the converters cannot handle at all. Same user-facing
            // situation as malformed JSON, so it gets the same treatment.
            quarantined = Quarantine(path);
            return fallback;
        }
    }

    /// <inheritdoc cref="Read{T}(string, T, out string?)"/>
    public static T Read<T>(string path, T fallback) => Read(path, fallback, out _);

    /// <summary>
    /// Serialise and write atomically.
    /// <para>
    /// The temp file is created beside the target, never in the system temp
    /// directory: <c>rename</c> is only atomic within a single filesystem, and on
    /// Linux <c>/tmp</c> is very often a separate tmpfs mount. A cross-device
    /// rename would fail outright, or silently degrade to copy-then-delete and lose
    /// the atomicity this method exists to provide.
    /// </para>
    /// </summary>
    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, ProfileMigration.JsonOptions);

        // The pid and a GUID keep two processes — or the app and its own diagnostics
        // CLI — from colliding on the same temp name and corrupting each other.
        var tmp = $"{path}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(tmp, json);

            // Flush to disk before the rename. Without this the rename can be
            // durable while the data it points at is not, so a crash immediately
            // after can leave a valid-looking file full of zeroes — worse than the
            // partial write this whole method is designed to prevent.
            using (var handle = File.OpenHandle(tmp, FileMode.Open, FileAccess.Write))
                RandomAccess.FlushToDisk(handle);

            // overwrite: true so this works on Windows, where File.Move refuses an
            // existing destination. On Unix this maps to rename(2) directly.
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Never leave the temp file behind on a failed write: the directory would
            // slowly fill with debris, and a later reader could mistake one for real
            // data. Cleanup failure is swallowed because the original write error is
            // the one worth propagating.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Move an unreadable file aside, returning where it went.
    /// <para>
    /// Returns null when the file could not be moved. That is a real possibility on
    /// Windows if something holds a handle, and the caller must still be able to
    /// carry on with defaults — refusing to start because a broken file cannot be
    /// renamed would turn a recoverable problem into a dead app.
    /// </para>
    /// </summary>
    private static string? Quarantine(string path)
    {
        // A UTC timestamp, so repeated failures accumulate rather than overwrite one
        // another, and so the order is still obvious across a timezone change.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var target = $"{path}.corrupt-{stamp}";

        try
        {
            if (File.Exists(target)) target = $"{path}.corrupt-{stamp}-{Guid.NewGuid():N}";
            File.Move(path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
