namespace CloakHub.Core.Import;

/// <summary>
/// Copy the session-bearing parts of a browser profile into a Hub profile directory.
/// <para>
/// This is the only import path that actually keeps a logged-in session, and it
/// does so without decrypting anything: Chromium's cookie values are encrypted with
/// a key held by the OS, and the encrypted bytes travel with the profile, so the
/// stealth binary decrypts them exactly as the original browser would.
/// </para>
/// </summary>
public static class ProfileCloner
{
    /// <summary>
    /// Files and folders worth copying.
    /// <para>
    /// A full recursive copy would drag in gigabytes of cache and — much worse —
    /// the <c>SingletonLock</c>/<c>LOCK</c> files that stop Chromium starting at
    /// all. The failure that produces is the nastiest kind: the import reports
    /// success, and the profile then refuses to launch with an error about another
    /// instance that the user cannot find. This list is session state only.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> CloneEntries =
    [
        "Cookies",
        "Cookies-journal",
        "Login Data",
        "Login Data For Account",
        "Web Data",
        "Preferences",
        "Secure Preferences",
        "Local Storage",
        "Session Storage",
        "IndexedDB",
        "Local Extension Settings",
        "Extension State",
        "Extension Rules",
        "databases",
        "Service Worker",
        "Bookmarks",
        "History",
        "Favicons",
        "Network",
        "Sync Data",
        "Trust Tokens",
        "shared_proto_db",
        // Firefox equivalents, so one code path serves both families. Absent
        // entries are skipped, so listing both costs nothing.
        "cookies.sqlite",
        "prefs.js",
        "places.sqlite",
        "logins.json",
        "key4.db",
        "cert9.db",
        "permissions.sqlite",
        "storage",
        "storage.sqlite",
        "sessionstore.jsonlz4",
        "sessionstore-backups",
    ];

    /// <summary>
    /// Lock files that must never survive into the clone.
    /// <para>
    /// Removed after copying rather than filtered during it, because
    /// <c>SingletonLock</c> can also appear inside a copied subdirectory and a
    /// name-based filter on the top level alone would miss it.
    /// </para>
    /// </summary>
    private static readonly string[] LockFiles =
        ["SingletonLock", "SingletonCookie", "SingletonSocket", "LOCK", "lock", ".parentlock"];

    /// <summary>
    /// The profile directory Chromium reads inside a user-data directory.
    /// <para>
    /// Chromium splits the two concepts: <c>--user-data-dir</c> is the container,
    /// and the actual profile lives in a subdirectory beneath it — <c>Default</c>
    /// unless <c>--profile-directory</c> says otherwise. Session state belongs in
    /// that subdirectory, not the container.
    /// </para>
    /// </summary>
    public const string ChromiumProfileDir = "Default";

    /// <summary>
    /// Where session data must be written, given a Hub user-data directory.
    /// <para>
    /// Exists so the importer and the launcher cannot disagree about the layout.
    /// They previously did, and it silently broke every import: the clone wrote
    /// <c>Cookies</c>, <c>Login Data</c> and the rest into the user-data root,
    /// while the browser — launched with <c>--user-data-dir</c> and no
    /// <c>--profile-directory</c> — looked for them one level down in
    /// <c>Default/</c>. It found an empty profile and created a fresh one, so the
    /// import reported success, reported the megabytes it had copied, and the
    /// user got a logged-out browser with no indication anything had gone wrong.
    /// </para>
    /// </summary>
    public static string TargetFor(string userDataDir) =>
        Path.Combine(userDataDir, ChromiumProfileDir);

    /// <summary>
    /// Clone <paramref name="sourceDir"/> into <paramref name="targetDir"/>.
    /// <para>
    /// <paramref name="targetDir"/> is the <i>profile</i> directory, not the
    /// user-data directory — callers holding the latter must go through
    /// <see cref="TargetFor"/>.
    /// </para>
    /// <para>
    /// The source browser must be closed. Chromium holds an exclusive lock on the
    /// cookie DB while running, and copying it live yields a truncated file — which
    /// does not fail the copy, it produces a profile that launches with no cookies
    /// and no explanation. Refusing up front is the only way the user learns the
    /// actual precondition.
    /// </para>
    /// </summary>
    public static CloneResult Clone(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
            return CloneResult.Failed("The source profile folder no longer exists.");

        if (IsBrowserRunning(sourceDir))
        {
            return CloneResult.Failed(
                "That browser profile is currently in use. Close the browser completely and try " +
                "again — copying a live profile produces a truncated cookie database, so the " +
                "imported profile would start logged out.");
        }

        var copied = new List<string>();
        var skipped = new List<string>();
        long bytes = 0;

        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (Exception e)
        {
            return CloneResult.Failed($"Could not create the destination folder: {e.Message}");
        }

        foreach (var entry in CloneEntries)
        {
            var from = Path.Combine(sourceDir, entry);
            var to = Path.Combine(targetDir, entry);

            var isDir = Directory.Exists(from);
            var isFile = !isDir && File.Exists(from);

            if (!isDir && !isFile) continue; // absent is normal; not worth reporting

            try
            {
                bytes += isDir ? CopyDirectory(from, to) : CopyFile(from, to);
                copied.Add(entry);
            }
            catch (Exception e)
            {
                skipped.Add($"{entry} ({e.Message})");
            }
        }

        RemoveLocks(targetDir);

        if (copied.Count == 0)
        {
            return CloneResult.Failed(
                "Nothing could be copied from that profile. It may be an empty folder, or a " +
                "profile whose files this account cannot read.");
        }

        return new CloneResult
        {
            Ok = true,
            Copied = copied,
            Skipped = skipped,
            Bytes = bytes,
        };
    }

    private static long CopyFile(string from, string to)
    {
        var parent = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        File.Copy(from, to, overwrite: true);
        return new FileInfo(to).Length;
    }

    /// <summary>
    /// Recursive copy, iterative so a deep tree cannot overflow the stack.
    /// <para>
    /// IndexedDB in particular nests per-origin and per-store, and a profile that
    /// has visited enough sites goes deeper than is comfortable for recursion on a
    /// UI thread's stack.
    /// </para>
    /// </summary>
    private static long CopyDirectory(string from, string to)
    {
        long bytes = 0;
        var queue = new Queue<(string From, string To)>();
        queue.Enqueue((from, to));

        while (queue.Count > 0)
        {
            var (src, dst) = queue.Dequeue();
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.GetFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(file));
                try
                {
                    File.Copy(file, target, overwrite: true);
                    bytes += new FileInfo(target).Length;
                }
                catch
                {
                    // One unreadable cache shard must not abort a 300 MB copy that
                    // is otherwise fine — the caller sees the entry as copied
                    // because the session data it cares about did land.
                }
            }

            foreach (var sub in Directory.GetDirectories(src))
            {
                // Links are not followed: a profile with a symlinked Cache
                // pointing at a shared directory would otherwise be copied into
                // the clone and then diverge from the original.
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }

                queue.Enqueue((sub, Path.Combine(dst, Path.GetFileName(sub))));
            }
        }

        return bytes;
    }

    private static void RemoveLocks(string dir)
    {
        foreach (var name in LockFiles)
        {
            var path = Path.Combine(dir, name);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
                // A lock we cannot remove is worth reporting only if the launch
                // then fails, and the launch reports it far more precisely.
            }
        }
    }

    /// <summary>
    /// Is the source browser still running on this profile?
    /// <para>
    /// Chromium keeps <c>SingletonLock</c> in the <i>user-data</i> root, one level
    /// above the profile folder — except in single-profile layouts like Opera
    /// Stable, where they are the same directory. Both are checked.
    /// </para>
    /// <para>
    /// The probe is a link/attribute check, not <c>File.Exists</c>:
    /// <c>SingletonLock</c> is a symlink whose target is <c>hostname-pid</c> and
    /// does not resolve to a real file, so <c>File.Exists</c> returns false for a
    /// browser that is very much running. That false negative is what lets a live
    /// profile be copied and silently truncated.
    /// </para>
    /// </summary>
    public static bool IsBrowserRunning(string profileDir)
    {
        var parent = Path.GetDirectoryName(
            profileDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (var dir in new[] { profileDir, parent })
        {
            if (string.IsNullOrEmpty(dir)) continue;

            foreach (var name in new[] { "SingletonLock", "SingletonSocket", ".parentlock" })
            {
                var path = Path.Combine(dir, name);
                try
                {
                    // GetAttributes succeeds for a dangling symlink where Exists
                    // does not, which is exactly the case that matters.
                    _ = File.GetAttributes(path);
                    return true;
                }
                catch
                {
                    // Not locked via this path.
                }
            }
        }

        return false;
    }
}

/// <summary>
/// What a clone actually did.
/// <para>
/// <see cref="Skipped"/> is reported rather than swallowed: a clone that copied
/// cookies but skipped Local Storage produces a profile that is logged in to some
/// sites and not others, and the user can only make sense of that if they are told.
/// </para>
/// </summary>
public sealed record CloneResult
{
    public bool Ok { get; init; }
    public IReadOnlyList<string> Copied { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public long Bytes { get; init; }
    public string? Error { get; init; }

    public double MegaBytes => Math.Round(Bytes / (1024.0 * 1024.0), 1);

    public static CloneResult Failed(string error) => new() { Ok = false, Error = error };
}
