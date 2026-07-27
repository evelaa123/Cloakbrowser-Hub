using Microsoft.Data.Sqlite;

namespace CloakHub.Core.Cookies;

/// <summary>
/// Reads and writes Chromium's own <c>Cookies</c> SQLite database.
/// <para>
/// <b>Why this exists.</b> The Electron build injected cookies through
/// Playwright's <c>BrowserContext.addCookies</c>. This port launches Chromium
/// directly with no driver — that is the point of it, since a driver is an
/// automation surface a site can detect — so there is no context to inject into
/// and no CDP endpoint to talk to. The cookie store is therefore written where
/// Chromium keeps it, before the browser starts.
/// </para>
/// <para>
/// <b>Why it is safe to write plaintext values.</b> Chromium stores each value in
/// either <c>value</c> (plaintext) or <c>encrypted_value</c> (OS-keyring
/// ciphertext), and reads whichever is populated. Writing plaintext avoids
/// depending on DPAPI, libsecret or the macOS keychain — none of which we could
/// satisfy for a profile directory that may be copied between machines. Chromium
/// re-encrypts on its next write, so values only stay plaintext until first use.
/// Since M131 a row with <i>both</i> fields populated is discarded outright
/// (<c>kValuesExistInBothEncryptedAndPlaintext</c>), so <c>encrypted_value</c> is
/// always written empty rather than left untouched.
/// </para>
/// <para>
/// <b>Only while the browser is closed.</b> Chromium keeps the database open and
/// caches cookies in memory, so a write behind a running browser is either locked
/// out or silently overwritten at shutdown. Callers inject before launch.
/// </para>
/// </summary>
public static class ChromiumCookieDb
{
    /// <summary>
    /// Chromium's schema version, matching <c>kCurrentVersionNumber</c> in
    /// <c>sqlite_persistent_cookie_store.cc</c>.
    /// <para>
    /// Declared when creating a database from scratch so Chromium adopts it rather
    /// than treating it as a corrupt file and starting over — which would discard
    /// the session that was just imported. An existing database is never
    /// re-versioned: if a newer Chromium has migrated it, claiming an older version
    /// would trigger exactly the wipe this avoids.
    /// </para>
    /// </summary>
    private const int SchemaVersion = 24;

    /// <summary>Ticks between the Windows epoch (1601-01-01) and the Unix epoch.</summary>
    private const long WindowsToUnixEpochSeconds = 11644473600L;

    /// <summary>
    /// Where the cookie database lives inside a user-data directory.
    /// <para>
    /// <c>Default</c> is the profile subdirectory Chromium uses when launched
    /// without <c>--profile-directory</c>, which is how sessions are started here.
    /// </para>
    /// </summary>
    public static string PathFor(string userDataDir) =>
        Path.Combine(userDataDir, "Default", "Network", "Cookies");

    /// <summary>
    /// Write cookies into the profile's cookie database, creating it if needed.
    /// </summary>
    /// <param name="userDataDir">The profile's Chromium user-data directory.</param>
    /// <param name="cookies">Cookies to write; each is sanitised first.</param>
    /// <param name="replace">
    /// Clear existing rows first. Off by default: an import usually adds an account
    /// to a profile, and wiping cookies the user did not mention is not recoverable.
    /// </param>
    /// <returns>How many rows were written.</returns>
    public static int Write(string userDataDir, IEnumerable<BrowserCookie> cookies, bool replace = false)
    {
        var list = cookies as IList<BrowserCookie> ?? [.. cookies];
        var path = PathFor(userDataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var connection = Open(path);
        EnsureSchema(connection);

        // One transaction for the whole set. Cookies are a session, not a pile of
        // independent rows: a crash midway through would leave a profile holding
        // half a login, which is the state that trips "verify it's you".
        using var transaction = connection.BeginTransaction();

        if (replace)
        {
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM cookies";
            clear.ExecuteNonQuery();
        }

        var defaultHost = CookieSanitiser.FallbackHost(list);
        var now = ToChromiumTime(DateTimeOffset.UtcNow);
        var written = 0;

        // INSERT OR REPLACE, because the unique index covers
        // (host_key, top_frame_site_key, has_cross_site_ancestor, name, path,
        // source_scheme, source_port). Re-importing a refreshed export must update
        // the existing row; a plain INSERT would throw on the first repeat and abort
        // the rest of the session.
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT OR REPLACE INTO cookies (
                creation_utc, host_key, top_frame_site_key, name, value,
                encrypted_value, path, expires_utc, is_secure, is_httponly,
                last_access_utc, has_expires, is_persistent, priority, samesite,
                source_scheme, source_port, last_update_utc, source_type,
                has_cross_site_ancestor
            ) VALUES (
                $creation, $host, '', $name, $value,
                X'', $path, $expires, $secure, $httponly,
                $lastAccess, $hasExpires, $persistent, 1, $samesite,
                $sourceScheme, $sourcePort, $lastUpdate, 0,
                0
            )
            """;

        foreach (var raw in list)
        {
            var cookie = CookieSanitiser.Sanitise(raw, defaultHost);
            if (cookie is null) continue;

            var host = HostKey(cookie);
            if (string.IsNullOrEmpty(host)) continue;

            var persistent = !cookie.IsSession;

            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$creation", now);
            insert.Parameters.AddWithValue("$host", host);
            insert.Parameters.AddWithValue("$name", cookie.Name);
            insert.Parameters.AddWithValue("$value", cookie.Value);
            insert.Parameters.AddWithValue("$path", PathOf(cookie));
            insert.Parameters.AddWithValue("$expires", persistent ? ToChromiumTime(cookie.Expires) : 0L);
            insert.Parameters.AddWithValue("$secure", cookie.Secure ? 1 : 0);
            insert.Parameters.AddWithValue("$httponly", cookie.HttpOnly ? 1 : 0);
            insert.Parameters.AddWithValue("$lastAccess", now);
            insert.Parameters.AddWithValue("$hasExpires", persistent ? 1 : 0);
            insert.Parameters.AddWithValue("$persistent", persistent ? 1 : 0);
            insert.Parameters.AddWithValue("$samesite", ToDbSameSite(cookie.SameSite));

            // source_scheme: 0 unset, 1 non-cryptographic, 2 cryptographic. A Secure
            // cookie claiming an http source is rejected as non-canonical on load.
            insert.Parameters.AddWithValue("$sourceScheme", cookie.Secure ? 2 : 1);

            // 443/80 rather than -1 (unspecified): the unique index includes the port,
            // and a stored -1 would not match the row Chromium later writes for the
            // same cookie, leaving two rows and an ambiguous session.
            insert.Parameters.AddWithValue("$sourcePort", cookie.Secure ? 443 : 80);
            insert.Parameters.AddWithValue("$lastUpdate", now);

            written += insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return written;
    }

    /// <summary>
    /// Read cookies back out of a profile. Rows whose value is encrypted are
    /// returned with an empty value rather than skipped: the caller is usually
    /// exporting or counting, and losing the row entirely would misreport the
    /// session as smaller than it is.
    /// </summary>
    public static List<BrowserCookie> Read(string userDataDir)
    {
        var path = PathFor(userDataDir);
        if (!File.Exists(path)) return [];

        using var connection = Open(path);

        if (!TableExists(connection))
            return [];

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT host_key, name, value, path, expires_utc,
                   is_secure, is_httponly, samesite, is_persistent
            FROM cookies
            """;

        var result = new List<BrowserCookie>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var persistent = reader.GetInt64(8) != 0;
            var expiresUtc = reader.GetInt64(4);

            result.Add(new BrowserCookie
            {
                Domain = reader.GetString(0),
                Name = reader.GetString(1),
                Value = reader.GetString(2),
                Path = reader.GetString(3),
                Expires = persistent ? FromChromiumTime(expiresUtc) : -1,
                Secure = reader.GetInt64(5) != 0,
                HttpOnly = reader.GetInt64(6) != 0,
                SameSite = FromDbSameSite(reader.GetInt64(7)),
            });
        }

        return result;
    }

    /// <summary>Whether a profile has a cookie database with any rows in it.</summary>
    public static int Count(string userDataDir)
    {
        var path = PathFor(userDataDir);
        if (!File.Exists(path)) return 0;

        using var connection = Open(path);
        if (!TableExists(connection)) return 0;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cookies";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // ------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cookies'";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Create the v24 schema when the profile has never been launched.
    /// <para>
    /// Mirrors <c>CreateV24Schema</c> in Chromium exactly, including the unique
    /// index: a database whose columns Chromium does not recognise is treated as
    /// corrupt and replaced, taking the imported session with it.
    /// </para>
    /// </summary>
    private static void EnsureSchema(SqliteConnection connection)
    {
        if (TableExists(connection)) return;

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE cookies(
                creation_utc INTEGER NOT NULL,
                host_key TEXT NOT NULL,
                top_frame_site_key TEXT NOT NULL,
                name TEXT NOT NULL,
                value TEXT NOT NULL,
                encrypted_value BLOB NOT NULL,
                path TEXT NOT NULL,
                expires_utc INTEGER NOT NULL,
                is_secure INTEGER NOT NULL,
                is_httponly INTEGER NOT NULL,
                last_access_utc INTEGER NOT NULL,
                has_expires INTEGER NOT NULL,
                is_persistent INTEGER NOT NULL,
                priority INTEGER NOT NULL,
                samesite INTEGER NOT NULL,
                source_scheme INTEGER NOT NULL,
                source_port INTEGER NOT NULL,
                last_update_utc INTEGER NOT NULL,
                source_type INTEGER NOT NULL,
                has_cross_site_ancestor INTEGER NOT NULL);

            CREATE UNIQUE INDEX cookies_unique_index
                ON cookies(host_key, top_frame_site_key, has_cross_site_ancestor,
                           name, path, source_scheme, source_port);
            """;
        command.ExecuteNonQuery();

        // Chromium reads the version from its own meta table and rebuilds the file if
        // it is missing or too old.
        using var meta = connection.CreateCommand();
        meta.CommandText =
            $"""
             CREATE TABLE IF NOT EXISTS meta(key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY,
                                             value LONGVARCHAR);
             INSERT OR REPLACE INTO meta (key, value) VALUES ('version', '{SchemaVersion}');
             INSERT OR REPLACE INTO meta (key, value) VALUES ('last_compatible_version', '{SchemaVersion}');
             """;
        meta.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // Field conversion
    // ------------------------------------------------------------------

    /// <summary>
    /// The <c>host_key</c> for a cookie: the domain as given, or the host of its URL
    /// for a host-only (<c>__Host-</c>) cookie, which by definition has no domain.
    /// </summary>
    private static string HostKey(BrowserCookie cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie.Domain))
            return cookie.Domain;

        if (!string.IsNullOrWhiteSpace(cookie.Url) &&
            Uri.TryCreate(cookie.Url, UriKind.Absolute, out var uri))
            return uri.Host;

        return "";
    }

    private static string PathOf(BrowserCookie cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie.Path)) return cookie.Path;

        // A cookie expressed as a URL carries its path there; the sanitiser forces "/"
        // for __Host-, so this only ever recovers a genuine path.
        if (!string.IsNullOrWhiteSpace(cookie.Url) &&
            Uri.TryCreate(cookie.Url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.AbsolutePath))
            return uri.AbsolutePath;

        return "/";
    }

    /// <summary>
    /// Chromium stores time as microseconds since 1601-01-01 UTC, not Unix seconds.
    /// Getting this wrong does not fail loudly — it dates every cookie to 1601 and
    /// Chromium expires the entire session on load.
    /// </summary>
    internal static long ToChromiumTime(DateTimeOffset when) =>
        (when.ToUnixTimeSeconds() + WindowsToUnixEpochSeconds) * 1_000_000L;

    internal static long ToChromiumTime(long unixSeconds) =>
        (unixSeconds + WindowsToUnixEpochSeconds) * 1_000_000L;

    internal static long FromChromiumTime(long chromiumTime) =>
        chromiumTime <= 0 ? -1 : (chromiumTime / 1_000_000L) - WindowsToUnixEpochSeconds;

    /// <summary>
    /// Map to Chromium's <c>DBCookieSameSite</c>: -1 unspecified, 0 none, 1 lax,
    /// 2 strict.
    /// </summary>
    internal static int ToDbSameSite(CookieSameSite? sameSite) => sameSite switch
    {
        CookieSameSite.None => 0,
        CookieSameSite.Lax => 1,
        CookieSameSite.Strict => 2,
        _ => -1,
    };

    internal static CookieSameSite? FromDbSameSite(long value) => value switch
    {
        0 => CookieSameSite.None,
        1 => CookieSameSite.Lax,
        2 => CookieSameSite.Strict,
        _ => null,
    };
}
