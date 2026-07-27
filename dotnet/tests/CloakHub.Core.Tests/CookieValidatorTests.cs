using CloakHub.Core.Cookies;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// What the user is shown before an import is committed.
/// <para>
/// The import is hard to undo and the file is unreadable by eye, so this report is
/// the only chance to notice it is the wrong account.
/// </para>
/// </summary>
public class CookieValidatorTests
{
    [Fact]
    public void A_json_export_is_recognised_and_counted()
    {
        var result = CookieValidator.Validate(
            """[{"name":"a","value":"1","domain":".example.com"}]""");

        Assert.True(result.Ok);
        Assert.Equal(CookieFormat.Json, result.Format);
        Assert.Equal(1, result.Count);
        Assert.Contains(".example.com", result.Domains);
    }

    [Fact]
    public void A_netscape_file_is_recognised()
    {
        var result = CookieValidator.Validate(
            "# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tTRUE\t0\ta\t1\n");

        Assert.Equal(CookieFormat.Netscape, result.Format);
    }

    [Fact]
    public void A_header_paste_is_recognised()
    {
        Assert.Equal(CookieFormat.Header, CookieValidator.Validate("a=1; b=2").Format);
    }

    [Fact]
    public void An_empty_payload_says_so_plainly()
    {
        var result = CookieValidator.Validate("");

        Assert.False(result.Ok);
        Assert.Equal("The file is empty.", result.Error);
    }

    [Fact]
    public void An_unrecognised_payload_lists_what_is_supported()
    {
        // The user has to fix this themselves, so the message names the formats
        // rather than only reporting failure.
        var result = CookieValidator.Validate("nothing resembling a cookie");

        Assert.False(result.Ok);
        Assert.Contains("Cookie-Editor", result.Error);
        Assert.Contains("Netscape", result.Error);
    }

    // ------------------------------------------------------------------
    // Service detection
    // ------------------------------------------------------------------

    [Fact]
    public void Two_signature_names_are_enough_to_name_a_service()
    {
        var result = CookieValidator.Validate(
            """
            [{"name":"c_user","value":"1","domain":".facebook.com"},
             {"name":"xs","value":"2","domain":".facebook.com"}]
            """);

        Assert.Contains("Facebook", result.AuthHints);
    }

    [Fact]
    public void A_single_generic_name_alone_does_not_claim_a_service()
    {
        // "sessionid" is used across half the web; claiming an Instagram session for
        // any site that happens to use it would be worse than saying nothing.
        var result = CookieValidator.Validate(
            """[{"name":"sessionid","value":"1","domain":".unrelated-site.org"}]""");

        Assert.Empty(result.AuthHints);
    }

    [Fact]
    public void A_generic_name_on_the_services_own_domain_does_count()
    {
        var result = CookieValidator.Validate(
            """[{"name":"sessionid","value":"1","domain":".instagram.com"}]""");

        Assert.Contains("Instagram", result.AuthHints);
    }

    // ------------------------------------------------------------------
    // Suggested name
    // ------------------------------------------------------------------

    [Fact]
    public void An_email_in_the_payload_becomes_the_suggested_name()
    {
        // Cookie sets are one per account, and the account is what is being named.
        var result = CookieValidator.Validate(
            """[{"name":"a","value":"user@example.com","domain":".example.com"}]""");

        Assert.Equal("user@example.com", result.SuggestedName);
    }

    [Fact]
    public void Without_an_email_the_service_name_is_used()
    {
        var result = CookieValidator.Validate(
            """
            [{"name":"c_user","value":"1","domain":".facebook.com"},
             {"name":"xs","value":"2","domain":".facebook.com"}]
            """);

        Assert.Equal("Facebook account", result.SuggestedName);
    }

    [Fact]
    public void Otherwise_the_dominant_domain_is_used()
    {
        var result = CookieValidator.Validate(
            """[{"name":"a","value":"1","domain":".shop.example.org"}]""");

        Assert.Equal("example.org", result.SuggestedName);
    }

    [Fact]
    public void The_dominant_domain_wins_over_a_one_off_subdomain()
    {
        var domains = new[] { "a.example.org", "b.example.org", "other.net" };

        Assert.Equal("example.org", CookieValidator.PrimaryDomain(domains));
    }

    [Fact]
    public void Validating_a_missing_file_reports_the_read_failure()
    {
        var result = CookieValidator.ValidateFile(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"));

        Assert.False(result.Ok);
        Assert.Contains("Could not read the file", result.Error);
    }
}
