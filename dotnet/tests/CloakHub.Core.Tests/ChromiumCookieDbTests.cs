using CloakHub.Core.Cookies;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// Writing Chromium's own cookie database.
/// <para>
/// These run against real SQLite files rather than a fake, because what is being
/// verified is the on-disk shape Chromium will read — a mock would only assert
/// that the code calls itself consistently, which is exactly the bug class that
/// matters here. Several assertions check raw column values for the same reason.
/// </para>
/// </summary>
public sealed class ChromiumCookieDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"cookiedb-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // Pooling keeps the file handle open on Windows, which blocks the delete.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static BrowserCookie Cookie(
        string name = "SID",
        string domain = ".example.com",
        string value = "v",
        long expires = 1900000000) =>
        new() { Name = name, Value = value, Domain = domain, Path = "/", Expires = expires };

    // ------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------

    [Fact]
    public void Write_creates_the_database_where_chromium_looks_for_it()
    {
        ChromiumCookieDb.Write(_dir, [Cookie()]);

        Assert.True(File.Exists(Path.Combine(_dir, "Default", "Network", "Cookies")));
    }

    [Fact]
    public void A_new_database_declares_the_schema_version_chromium_expects()
    {
        // Claiming the wrong version makes Chromium treat the file as unusable and
        // replace it, discarding the session that was just imported.
        ChromiumCookieDb.Write(_dir, [Cookie()]);

        Assert.Equal("24", Scalar("SELECT value FROM meta WHERE key='version'"));
        Assert.Equal("24", Scalar("SELECT value FROM meta WHERE key='last_compatible_version'"));
    }

    [Fact]
    public void The_unique_index_matches_chromiums_own()
    {
        ChromiumCookieDb.Write(_dir, [Cookie()]);

        var sql = Scalar("SELECT sql FROM sqlite_master WHERE name='cookies_unique_index'");

        Assert.Contains("host_key", sql);
        Assert.Contains("top_frame_site_key", sql);
        Assert.Contains("has_cross_site_ancestor", sql);
        Assert.Contains("source_scheme", sql);
        Assert.Contains("source_port", sql);
    }

    // ------------------------------------------------------------------
    // Column values Chromium is strict about
    // ------------------------------------------------------------------

    [Fact]
    public void Values_are_written_plaintext_with_an_empty_encrypted_column()
    {
        // Since M131 a row with both columns populated is discarded outright as
        // kValuesExistInBothEncryptedAndPlaintext.
        ChromiumCookieDb.Write(_dir, [Cookie(value: "secret")]);

        Assert.Equal("secret", Scalar("SELECT value FROM cookies"));
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT length(encrypted_value) FROM cookies")));
    }

    [Fact]
    public void Expiry_is_stored_as_microseconds_since_1601()
    {
        // The wrong epoch does not fail loudly: it dates every cookie to 1601 and
        // Chromium expires the entire session on load.
        ChromiumCookieDb.Write(_dir, [Cookie(expires: 1900000000)]);

        var stored = Convert.ToInt64(Scalar("SELECT expires_utc FROM cookies"));

        Assert.Equal((1900000000L + 11644473600L) * 1_000_000L, stored);
    }

    [Fact]
    public void A_secure_cookie_declares_a_cryptographic_source_scheme_and_port_443()
    {
        // A Secure cookie claiming an http source is rejected as non-canonical, and a
        // port of -1 would not match the row Chromium later writes for the same
        // cookie, leaving two rows for one session.
        ChromiumCookieDb.Write(_dir, [new BrowserCookie
        {
            Name = "a", Value = "v", Domain = "x.com", Path = "/", Secure = true,
        }]);

        Assert.Equal(2L, Convert.ToInt64(Scalar("SELECT source_scheme FROM cookies")));
        Assert.Equal(443L, Convert.ToInt64(Scalar("SELECT source_port FROM cookies")));
    }

    [Fact]
    public void A_session_cookie_is_marked_non_persistent_with_no_expiry()
    {
        ChromiumCookieDb.Write(_dir, [Cookie(expires: -1)]);

        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT is_persistent FROM cookies")));
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT has_expires FROM cookies")));
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT expires_utc FROM cookies")));
    }

    [Theory]
    [InlineData(null, -1L)]
    [InlineData(CookieSameSite.None, 0L)]
    [InlineData(CookieSameSite.Lax, 1L)]
    [InlineData(CookieSameSite.Strict, 2L)]
    public void SameSite_maps_to_chromiums_own_encoding(CookieSameSite? sameSite, long expected)
    {
        // "example.org" deliberately: a cross-site host would have None inferred and
        // the null case could not be observed.
        ChromiumCookieDb.Write(_dir, [new BrowserCookie
        {
            Name = "a", Value = "v", Domain = "example.org", Path = "/", SameSite = sameSite,
        }]);

        Assert.Equal(expected, Convert.ToInt64(Scalar("SELECT samesite FROM cookies")));
    }

    // ------------------------------------------------------------------
    // Round trip and merge behaviour
    // ------------------------------------------------------------------

    [Fact]
    public void Cookies_round_trip_through_the_database()
    {
        ChromiumCookieDb.Write(_dir, [new BrowserCookie
        {
            Name = "SID",
            Value = "value",
            Domain = ".example.org",
            Path = "/app",
            Expires = 1900000000,
            HttpOnly = true,
            Secure = true,
            SameSite = CookieSameSite.Lax,
        }]);

        var cookie = Assert.Single(ChromiumCookieDb.Read(_dir));

        Assert.Equal("SID", cookie.Name);
        Assert.Equal("value", cookie.Value);
        Assert.Equal(".example.org", cookie.Domain);
        Assert.Equal("/app", cookie.Path);
        Assert.Equal(1900000000, cookie.Expires);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(CookieSameSite.Lax, cookie.SameSite);
    }

    [Fact]
    public void Re_importing_the_same_cookie_updates_rather_than_duplicating()
    {
        // A refreshed export must replace the stale row. A plain INSERT would throw on
        // the unique index and abort the rest of the session.
        ChromiumCookieDb.Write(_dir, [Cookie(value: "old")]);
        ChromiumCookieDb.Write(_dir, [Cookie(value: "new")]);

        var cookie = Assert.Single(ChromiumCookieDb.Read(_dir));
        Assert.Equal("new", cookie.Value);
    }

    [Fact]
    public void Writing_without_replace_keeps_cookies_already_present()
    {
        // An import usually adds an account to a profile; silently wiping the others
        // is not recoverable.
        ChromiumCookieDb.Write(_dir, [Cookie(name: "first")]);
        ChromiumCookieDb.Write(_dir, [Cookie(name: "second")]);

        Assert.Equal(2, ChromiumCookieDb.Count(_dir));
    }

    [Fact]
    public void Replace_clears_the_store_first()
    {
        ChromiumCookieDb.Write(_dir, [Cookie(name: "first")]);
        ChromiumCookieDb.Write(_dir, [Cookie(name: "second")], replace: true);

        var cookie = Assert.Single(ChromiumCookieDb.Read(_dir));
        Assert.Equal("second", cookie.Name);
    }

    [Fact]
    public void A_host_only_cookie_is_stored_under_the_host_from_its_url()
    {
        ChromiumCookieDb.Write(_dir, [new BrowserCookie
        {
            Name = "__Host-GAPS", Value = "v", Domain = ".google.com",
        }]);

        var cookie = Assert.Single(ChromiumCookieDb.Read(_dir));

        // No leading dot: host-only, as the prefix requires.
        Assert.Equal("google.com", cookie.Domain);
        Assert.Equal("/", cookie.Path);
    }

    [Fact]
    public void A_cookie_with_no_usable_host_is_skipped_rather_than_written_blank()
    {
        var written = ChromiumCookieDb.Write(_dir, [new BrowserCookie
        {
            Name = "orphan", Value = "v", Url = "not-a-url",
        }]);

        Assert.Equal(0, written);
    }

    // ------------------------------------------------------------------
    // Missing and empty stores
    // ------------------------------------------------------------------

    [Fact]
    public void Reading_a_profile_that_has_never_launched_returns_nothing()
    {
        Assert.Empty(ChromiumCookieDb.Read(_dir));
        Assert.Equal(0, ChromiumCookieDb.Count(_dir));
    }

    [Fact]
    public void An_empty_write_still_produces_a_valid_store()
    {
        // This is the path Clear() takes; it must leave a database Chromium can open
        // rather than an empty file it will treat as corrupt.
        ChromiumCookieDb.Write(_dir, []);

        Assert.Equal(0, ChromiumCookieDb.Count(_dir));
        Assert.Equal("24", Scalar("SELECT value FROM meta WHERE key='version'"));
    }

    [Fact]
    public void Time_conversion_round_trips()
    {
        var unix = 1900000000L;
        Assert.Equal(unix, ChromiumCookieDb.FromChromiumTime(ChromiumCookieDb.ToChromiumTime(unix)));
    }

    private string Scalar(string sql)
    {
        using var connection = new SqliteConnection(
            $"Data Source={ChromiumCookieDb.PathFor(_dir)}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? "";
    }
}
