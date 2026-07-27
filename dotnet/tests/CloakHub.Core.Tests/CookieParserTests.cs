using CloakHub.Core.Cookies;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// Parsing the formats users actually arrive with.
/// <para>
/// Weighted towards malformed and unusual input rather than the happy path,
/// because a file that fails to parse is a session the user cannot move and has
/// no way to convert by hand.
/// </para>
/// </summary>
public class CookieParserTests
{
    // ------------------------------------------------------------------
    // JSON
    // ------------------------------------------------------------------

    [Fact]
    public void Json_reads_a_bare_array()
    {
        var cookies = CookieParser.ParseJson(
            """[{"name":"a","value":"1","domain":".example.com","path":"/"}]""");

        var cookie = Assert.Single(cookies);
        Assert.Equal("a", cookie.Name);
        Assert.Equal("1", cookie.Value);
        Assert.Equal(".example.com", cookie.Domain);
    }

    [Fact]
    public void Json_reads_a_playwright_storage_state()
    {
        // Playwright wraps cookies alongside an "origins" key holding localStorage.
        var cookies = CookieParser.ParseJson(
            """{"cookies":[{"name":"a","value":"1","domain":"x.com"}],"origins":[]}""");

        Assert.Single(cookies);
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("Domain")]
    [InlineData("host")]
    [InlineData("hostKey")]
    public void Json_accepts_the_domain_aliases_extensions_use(string field)
    {
        var cookies = CookieParser.ParseJson(
            $$"""[{"name":"a","value":"1","{{field}}":".example.com"}]""");

        Assert.Equal(".example.com", Assert.Single(cookies).Domain);
    }

    [Fact]
    public void Json_converts_millisecond_expiries_to_seconds()
    {
        // Several extensions export the JavaScript Date scale. Stored as-is this
        // dates the cookie to the year 33000 and defeats any expiry comparison.
        var cookies = CookieParser.ParseJson(
            """[{"name":"a","value":"1","domain":"x.com","expirationDate":1900000000000}]""");

        Assert.Equal(1900000000, Assert.Single(cookies).Expires);
    }

    [Fact]
    public void Json_treats_a_zero_expiry_as_a_session_cookie()
    {
        var cookies = CookieParser.ParseJson(
            """[{"name":"a","value":"1","domain":"x.com","expirationDate":0}]""");

        Assert.Equal(-1, Assert.Single(cookies).Expires);
        Assert.True(Assert.Single(cookies).IsSession);
    }

    [Fact]
    public void Json_keeps_an_empty_value_but_drops_a_missing_one()
    {
        // An empty value is a real cookie state; a missing one is a broken record.
        var cookies = CookieParser.ParseJson(
            """
            [{"name":"empty","value":"","domain":"x.com"},
             {"name":"absent","domain":"x.com"}]
            """);

        Assert.Equal("empty", Assert.Single(cookies).Name);
    }

    [Fact]
    public void Json_returns_nothing_for_malformed_input()
    {
        Assert.Empty(CookieParser.ParseJson("{not json"));
    }

    [Fact]
    public void Json_reads_a_url_when_there_is_no_domain()
    {
        var cookies = CookieParser.ParseJson(
            """[{"name":"a","value":"1","url":"https://example.com/"}]""");

        var cookie = Assert.Single(cookies);
        Assert.Null(cookie.Domain);
        Assert.Equal("https://example.com/", cookie.Url);
    }

    // ------------------------------------------------------------------
    // Netscape
    // ------------------------------------------------------------------

    [Fact]
    public void Netscape_reads_a_tab_separated_file()
    {
        var cookies = CookieParser.ParseNetscape(
            "# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tTRUE\t1900000000\tSID\tvalue\n");

        var cookie = Assert.Single(cookies);
        Assert.Equal("SID", cookie.Name);
        Assert.Equal("value", cookie.Value);
        Assert.True(cookie.Secure);
        Assert.Equal(1900000000, cookie.Expires);
    }

    [Fact]
    public void Netscape_honours_the_HttpOnly_prefix()
    {
        var cookies = CookieParser.ParseNetscape(
            "#HttpOnly_.example.com\tTRUE\t/\tTRUE\t1900000000\tmy_token\tvalue\n");

        Assert.True(Assert.Single(cookies).HttpOnly);
        Assert.Equal(".example.com", cookies[0].Domain);
    }

    [Fact]
    public void Netscape_skips_ordinary_comments()
    {
        var cookies = CookieParser.ParseNetscape(
            "# a comment\n# another\n.example.com\tTRUE\t/\tFALSE\t0\ta\t1\n");

        Assert.Single(cookies);
    }

    [Fact]
    public void Netscape_falls_back_to_whitespace_when_tabs_were_lost()
    {
        // Files pasted through a chat window or reformatted by an editor arrive with
        // runs of spaces. Rejecting them would be rejecting a valid session.
        var cookies = CookieParser.ParseNetscape(
            ".example.com   TRUE   /   TRUE   1900000000   SID   value\n");

        Assert.Equal("SID", Assert.Single(cookies).Name);
    }

    [Fact]
    public void Netscape_keeps_spaces_inside_a_value_on_a_space_separated_line()
    {
        // The value is the last field and may contain spaces, so it is rejoined
        // rather than truncated at the first one.
        var cookies = CookieParser.ParseNetscape(
            ".example.com   TRUE   /   TRUE   1900000000   SID   two words\n");

        Assert.Equal("two words", Assert.Single(cookies).Value);
    }

    [Fact]
    public void Netscape_restores_HttpOnly_for_known_session_cookies()
    {
        // Exports frequently omit the prefix, leaving an auth token that JavaScript
        // could read — which a real browser would never expose.
        var cookies = CookieParser.ParseNetscape(
            ".google.com\tTRUE\t/\tTRUE\t1900000000\tSID\tvalue\n");

        Assert.True(Assert.Single(cookies).HttpOnly);
    }

    [Fact]
    public void Netscape_ignores_lines_with_too_few_fields()
    {
        Assert.Empty(CookieParser.ParseNetscape(".example.com\tTRUE\t/\n"));
    }

    [Fact]
    public void Netscape_handles_windows_line_endings()
    {
        var cookies = CookieParser.ParseNetscape(
            ".example.com\tTRUE\t/\tTRUE\t1900000000\tSID\tvalue\r\n");

        Assert.Equal("value", Assert.Single(cookies).Value);
    }

    // ------------------------------------------------------------------
    // Header
    // ------------------------------------------------------------------

    [Fact]
    public void Header_reads_semicolon_separated_pairs()
    {
        var cookies = CookieParser.ParseHeader("a=1; b=2", "example.com");

        Assert.Equal(2, cookies.Count);
        Assert.Equal(".example.com", cookies[0].Domain);
    }

    [Fact]
    public void Header_strips_a_leading_Cookie_label()
    {
        var cookies = CookieParser.ParseHeader("Cookie: a=1; b=2");

        Assert.Equal(2, cookies.Count);
        Assert.Equal("a", cookies[0].Name);
    }

    [Fact]
    public void Header_keeps_equals_signs_inside_a_value()
    {
        // Base64 padding is the common case; splitting on every '=' would corrupt it.
        var cookies = CookieParser.ParseHeader("token=abc==");

        Assert.Equal("abc==", Assert.Single(cookies).Value);
    }

    [Fact]
    public void Header_returns_nothing_without_a_pair()
    {
        Assert.Empty(CookieParser.ParseHeader("no pairs here"));
    }

    // ------------------------------------------------------------------
    // Format detection and merging
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_prefers_json_then_netscape_then_header()
    {
        Assert.Single(CookieParser.Parse("""[{"name":"a","value":"1","domain":"x.com"}]"""));
        Assert.Single(CookieParser.Parse(".x.com\tTRUE\t/\tTRUE\t0\ta\t1"));
        Assert.Single(CookieParser.Parse("a=1"));
    }

    [Fact]
    public void Parse_falls_through_when_json_holds_no_cookies()
    {
        // A JSON array of the wrong shape must not stop the other parsers running.
        Assert.Empty(CookieParser.Parse("[]"));
    }

    [Fact]
    public void Merge_deduplicates_on_name_domain_and_path_with_later_winning()
    {
        var older = new BrowserCookie { Name = "a", Value = "old", Domain = "x.com", Path = "/" };
        var newer = new BrowserCookie { Name = "a", Value = "new", Domain = "x.com", Path = "/" };

        var merged = CookieParser.Merge([older], [newer]);

        Assert.Equal("new", Assert.Single(merged).Value);
    }

    [Fact]
    public void Merge_keeps_the_same_name_on_different_paths()
    {
        var root = new BrowserCookie { Name = "a", Value = "1", Domain = "x.com", Path = "/" };
        var scoped = new BrowserCookie { Name = "a", Value = "2", Domain = "x.com", Path = "/admin" };

        Assert.Equal(2, CookieParser.Merge([root], [scoped]).Count);
    }

    [Theory]
    [InlineData("no_restriction", CookieSameSite.None)]
    [InlineData("unspecified", CookieSameSite.None)]
    [InlineData("None", CookieSameSite.None)]
    [InlineData("lax", CookieSameSite.Lax)]
    [InlineData("STRICT", CookieSameSite.Strict)]
    public void SameSite_spellings_normalise(string raw, CookieSameSite expected)
    {
        Assert.Equal(expected, CookieParser.NormaliseSameSite(raw));
    }

    [Fact]
    public void SameSite_returns_null_when_unknown_so_it_can_still_be_inferred()
    {
        Assert.Null(CookieParser.NormaliseSameSite("nonsense"));
        Assert.Null(CookieParser.NormaliseSameSite(null));
    }
}
