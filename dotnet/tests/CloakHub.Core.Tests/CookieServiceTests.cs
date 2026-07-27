using CloakHub.Core.Cookies;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// Import, export and the guards around them.
/// </summary>
public sealed class CookieServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"cookiesvc-{Guid.NewGuid():N}");

    private bool _running;

    private CookieService Service => new(
        profileId => Path.Combine(_root, profileId),
        _ => _running);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string GoogleJson =
        """
        [{"name":"SID","value":"s","domain":".google.com","path":"/"},
         {"name":"HSID","value":"h","domain":".google.com","path":"/"},
         {"name":"__Secure-1PSID","value":"p","domain":".google.com","path":"/"}]
        """;

    // ------------------------------------------------------------------
    // Import
    // ------------------------------------------------------------------

    [Fact]
    public void Importing_text_writes_the_cookies_and_reports_what_landed()
    {
        var result = Service.ImportText("p1", GoogleJson);

        Assert.True(result.Ok);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.Imported);
        Assert.Contains("google.com", result.Domains);
        Assert.Contains("Google", result.AuthHints);
        Assert.Empty(result.MissingCritical);
    }

    [Fact]
    public void Importing_is_refused_while_the_browser_is_running()
    {
        // Chromium holds the store open and flushes its in-memory copy at exit, so the
        // write would appear to succeed and then silently vanish.
        _running = true;

        var result = Service.ImportText("p1", GoogleJson);

        Assert.False(result.Ok);
        Assert.Contains("Close this profile's browser", result.Error);
        Assert.Equal(0, Service.Count("p1"));
    }

    [Fact]
    public void An_unrecognised_payload_is_rejected_with_the_supported_formats()
    {
        var result = Service.ImportText("p1", "this is not a cookie file");

        Assert.False(result.Ok);
        Assert.Contains("Unrecognised format", result.Error);
    }

    [Fact]
    public void An_empty_payload_is_rejected()
    {
        Assert.False(Service.ImportText("p1", "   ").Ok);
    }

    [Fact]
    public void A_second_import_adds_to_the_profile_by_default()
    {
        var service = Service;
        service.ImportText("p1", GoogleJson);
        var result = service.ImportText("p1", """[{"name":"a","value":"1","domain":"other.org"}]""");

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Replace_discards_what_was_there()
    {
        var service = Service;
        service.ImportText("p1", GoogleJson);
        var result = service.ImportText(
            "p1", """[{"name":"a","value":"1","domain":"other.org"}]""", replace: true);

        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void Profiles_do_not_share_cookies()
    {
        var service = Service;
        service.ImportText("p1", GoogleJson);

        Assert.Equal(0, service.Count("p2"));
    }

    [Fact]
    public void A_header_paste_takes_the_domain_it_is_given()
    {
        var result = Service.ImportText("p1", "sessionid=abc; csrftoken=def", domain: "instagram.com");

        Assert.True(result.Ok);
        Assert.Contains("instagram.com", result.Domains);
    }

    // ------------------------------------------------------------------
    // Partial sessions
    // ------------------------------------------------------------------

    [Fact]
    public void A_session_cookie_that_did_not_survive_is_reported_by_name()
    {
        // A partial import is worse than a failed one: the user opens the profile
        // believing it is ready and trips a verification check on the account they
        // were trying to protect. "orphan" has no usable host, so it is dropped.
        var payload =
            """
            [{"name":"SID","value":"s","domain":".google.com"},
             {"name":"ct0","value":"c","url":"not-a-url"}]
            """;

        var result = Service.ImportText("p1", payload);

        Assert.True(result.Ok);
        Assert.Contains(result.MissingCritical, m => m.Contains("ct0"));
    }

    [Fact]
    public void Services_absent_from_the_payload_are_not_reported_as_missing()
    {
        // A file with no Facebook cookies is not "missing" all of Facebook's.
        var result = Service.ImportText("p1", GoogleJson);

        Assert.DoesNotContain(result.MissingCritical, m => m.Contains("Facebook"));
    }

    // ------------------------------------------------------------------
    // Files
    // ------------------------------------------------------------------

    [Fact]
    public void Importing_files_merges_them()
    {
        Directory.CreateDirectory(_root);
        var a = Path.Combine(_root, "a.json");
        var b = Path.Combine(_root, "b.json");
        File.WriteAllText(a, """[{"name":"a","value":"1","domain":"x.com"}]""");
        File.WriteAllText(b, """[{"name":"b","value":"2","domain":"y.com"}]""");

        var result = Service.ImportFiles("p1", [a, b]);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Files);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void One_bad_file_does_not_lose_the_good_ones()
    {
        // Someone selecting twenty exports should not lose nineteen to a single
        // unreadable file.
        Directory.CreateDirectory(_root);
        var good = Path.Combine(_root, "good.json");
        File.WriteAllText(good, """[{"name":"a","value":"1","domain":"x.com"}]""");

        var result = Service.ImportFiles("p1", [good, Path.Combine(_root, "missing.json")]);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Files);
    }

    [Fact]
    public void Importing_only_unreadable_files_fails_clearly()
    {
        var result = Service.ImportFiles("p1", [Path.Combine(_root, "nope.json")]);

        Assert.False(result.Ok);
        Assert.Contains("readable cookies", result.Error);
    }

    // ------------------------------------------------------------------
    // Export and clear
    // ------------------------------------------------------------------

    [Fact]
    public void Exported_json_can_be_read_back()
    {
        Directory.CreateDirectory(_root);
        var service = Service;
        service.ImportText("p1", GoogleJson);

        var destination = Path.Combine(_root, "out.json");
        Assert.Equal(3, service.Export("p1", destination, CookieFormat.Json));

        Assert.Equal(3, CookieParser.Parse(File.ReadAllText(destination)).Count);
    }

    [Fact]
    public void Exported_netscape_can_be_read_back_with_httpOnly_intact()
    {
        Directory.CreateDirectory(_root);
        var service = Service;
        service.ImportText("p1", """[{"name":"SID","value":"s","domain":".google.com","httpOnly":true}]""");

        var destination = Path.Combine(_root, "out.txt");
        service.Export("p1", destination, CookieFormat.Netscape);

        var reparsed = CookieParser.Parse(File.ReadAllText(destination));
        Assert.True(Assert.Single(reparsed).HttpOnly);
    }

    [Fact]
    public void Clearing_empties_the_profile()
    {
        var service = Service;
        service.ImportText("p1", GoogleJson);

        Assert.True(service.Clear("p1"));
        Assert.Equal(0, service.Count("p1"));
    }

    [Fact]
    public void Clearing_is_refused_while_the_browser_is_running()
    {
        var service = Service;
        service.ImportText("p1", GoogleJson);
        _running = true;

        Assert.False(service.Clear("p1"));
        Assert.Equal(3, service.Count("p1"));
    }
}
