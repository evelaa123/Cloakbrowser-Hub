using CloakHub.Core.Cookies;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// The prefix and SameSite rules that decide whether Chromium keeps a cookie.
/// <para>
/// Every case here is one Chromium enforces at write time and rejects silently.
/// A regression would not fail visibly — the profile would open logged out, which
/// looks exactly like a wrong password, so these are asserted rather than trusted.
/// </para>
/// </summary>
public class CookieSanitiserTests
{
    [Fact]
    public void Host_prefix_becomes_host_only_with_no_domain()
    {
        // __Host- is defined by the absence of a Domain attribute, which can only be
        // expressed as a URL. Leaving the domain set makes Chromium reject the cookie.
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "__Host-GAPS",
            Value = "v",
            Domain = ".google.com",
        });

        Assert.NotNull(result);
        Assert.Null(result.Domain);
        Assert.Equal("https://google.com/", result.Url);
    }

    [Fact]
    public void Host_prefix_discards_a_non_root_path()
    {
        // The prefix rule forces Path=/; keeping the source path would violate it.
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "__Host-x",
            Value = "v",
            Domain = "example.com",
            Path = "/somewhere",
        });

        Assert.NotNull(result);
        Assert.Null(result.Path);
        Assert.Equal("https://example.com/", result.Url);
    }

    [Fact]
    public void Host_prefix_forces_secure_even_when_the_export_said_otherwise()
    {
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "__Host-x",
            Value = "v",
            Domain = "example.com",
            Secure = false,
        });

        Assert.True(result!.Secure);
    }

    [Fact]
    public void Secure_prefix_forces_secure()
    {
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "__Secure-1PSID",
            Value = "v",
            Domain = ".google.com",
            Secure = false,
        });

        Assert.True(result!.Secure);
        // Unlike __Host-, __Secure- keeps its domain and path.
        Assert.Equal(".google.com", result.Domain);
    }

    [Fact]
    public void SameSite_None_forces_secure()
    {
        // Chromium discards SameSite=None without Secure outright.
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "plain",
            Value = "v",
            Domain = "example.org",
            SameSite = CookieSameSite.None,
            Secure = false,
        });

        Assert.True(result!.Secure);
    }

    [Fact]
    public void SameSite_is_inferred_as_None_for_cross_site_identity_providers()
    {
        // Netscape files have no SameSite column, and the implicit Lax stops the
        // cookie being sent on the embedded SSO subrequest that needs it.
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "SID",
            Value = "v",
            Domain = ".google.com",
        });

        Assert.Equal(CookieSameSite.None, result!.SameSite);
        Assert.True(result.Secure);
    }

    [Fact]
    public void An_explicit_SameSite_is_never_overridden_by_inference()
    {
        // The issuer chose Strict deliberately; widening it would send the cookie on
        // cross-site requests a real browser would withhold.
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "SID",
            Value = "v",
            Domain = ".google.com",
            SameSite = CookieSameSite.Strict,
        });

        Assert.Equal(CookieSameSite.Strict, result!.SameSite);
    }

    [Fact]
    public void Ordinary_hosts_keep_an_unspecified_SameSite()
    {
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "a",
            Value = "v",
            Domain = "example.org",
        });

        Assert.Null(result!.SameSite);
    }

    [Theory]
    [InlineData("google.com", true)]
    [InlineData("accounts.google.com", true)]
    [InlineData("google.co.uk", true)]
    [InlineData("amazon.de", true)]
    [InlineData("amazon.co.jp", true)]
    [InlineData("example.org", false)]
    // The patterns are anchored and bounded so a lookalike cannot claim the
    // exemption and be handed SameSite=None on an attacker-controlled domain.
    [InlineData("notgoogle.com", false)]
    [InlineData("google.com.evil.net", false)]
    [InlineData("amazon.de.phish.io", false)]
    [InlineData("myfacebook.com", false)]
    public void Cross_site_matching_is_anchored(string host, bool expected)
    {
        Assert.Equal(expected, CookieSanitiser.LooksCrossSite(host));
    }

    [Fact]
    public void A_cookie_with_no_domain_or_url_is_given_the_files_own_host()
    {
        // Header-format pastes have no domain by construction. Synthesising one keeps
        // the session rather than discarding it.
        var result = CookieSanitiser.Sanitise(
            new BrowserCookie { Name = "a", Value = "v" },
            defaultHost: "example.net");

        Assert.NotNull(result);
        Assert.Contains("example.net", result.Url);
    }

    [Fact]
    public void A_nameless_cookie_is_rejected()
    {
        Assert.Null(CookieSanitiser.Sanitise(new BrowserCookie { Name = "", Value = "v" }));
        Assert.Null(CookieSanitiser.Sanitise(null));
    }

    [Fact]
    public void Value_expiry_and_httpOnly_survive_unchanged()
    {
        var result = CookieSanitiser.Sanitise(new BrowserCookie
        {
            Name = "a",
            Value = "keep me",
            Domain = "example.org",
            Expires = 1900000000,
            HttpOnly = true,
        });

        Assert.Equal("keep me", result!.Value);
        Assert.Equal(1900000000, result.Expires);
        Assert.True(result.HttpOnly);
    }

    [Fact]
    public void FallbackHost_uses_the_first_domain_in_the_file()
    {
        var host = CookieSanitiser.FallbackHost(
        [
            new BrowserCookie { Name = "a", Value = "1" },
            new BrowserCookie { Name = "b", Value = "2", Domain = ".example.com" },
        ]);

        Assert.Equal("example.com", host);
    }

    [Fact]
    public void FallbackHost_has_a_placeholder_when_nothing_has_a_domain()
    {
        Assert.Equal("example.com", CookieSanitiser.FallbackHost([]));
    }
}
