using CloakHub.Core.Model;
using CloakHub.Core.Network;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// Proxy parsing.
/// <para>
/// The formats here are not hypothetical — each one is a real provider export.
/// Getting a shape wrong does not throw; it produces a proxy pointing at the wrong
/// host, or with the username as the hostname, and the user sees a connection
/// failure they cannot explain from the row.
/// </para>
/// </summary>
public sealed class ProxyParserTests
{
    // ------------------------------------------------------------------
    // Shapes
    // ------------------------------------------------------------------

    [Fact]
    public void Reads_a_bare_host_and_port()
    {
        var p = ProxyParser.ParseLine("1.2.3.4:8080");

        Assert.NotNull(p);
        Assert.Equal(ProxyKind.Http, p.Kind);
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal(8080, p.Port);
        Assert.Null(p.Username);
    }

    [Fact]
    public void Reads_the_provider_standard_host_port_user_pass()
    {
        var p = ProxyParser.ParseLine("gate.provider.com:9000:alice:s3cret");

        Assert.NotNull(p);
        Assert.Equal("gate.provider.com", p.Host);
        Assert.Equal(9000, p.Port);
        Assert.Equal("alice", p.Username);
        Assert.Equal("s3cret", p.Password);
    }

    [Fact]
    public void Reads_the_inverted_user_pass_host_port()
    {
        // Some providers invert it. Shape alone cannot tell the two apart, so the
        // parser decides by which half looks like an endpoint -- and getting this
        // backwards would make "alice" the hostname.
        var p = ProxyParser.ParseLine("alice:s3cret:gate.provider.com:9000");

        Assert.NotNull(p);
        Assert.Equal("gate.provider.com", p.Host);
        Assert.Equal(9000, p.Port);
        Assert.Equal("alice", p.Username);
        Assert.Equal("s3cret", p.Password);
    }

    [Fact]
    public void Reads_credentials_before_an_at_sign()
    {
        var p = ProxyParser.ParseLine("alice:s3cret@1.2.3.4:8080");

        Assert.NotNull(p);
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal("alice", p.Username);
        Assert.Equal("s3cret", p.Password);
    }

    [Fact]
    public void A_password_containing_an_at_sign_does_not_split_the_host_off()
    {
        // Split at the last @, not the first. Splitting at the first would make
        // "pass@1.2.3.4" the host and lose the endpoint entirely.
        var p = ProxyParser.ParseLine("alice:p@ssw0rd@1.2.3.4:8080");

        Assert.NotNull(p);
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal("alice", p.Username);
        Assert.Equal("p@ssw0rd", p.Password);
    }

    [Fact]
    public void Reads_a_password_less_authenticated_proxy()
    {
        var p = ProxyParser.ParseLine("1.2.3.4:8080:token");

        Assert.NotNull(p);
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal("token", p.Username);
        Assert.Null(p.Password);
    }

    [Fact]
    public void Strips_a_leading_label_some_exports_include()
    {
        var p = ProxyParser.ParseLine("US-1 | 1.2.3.4:8080");

        Assert.NotNull(p);
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal(8080, p.Port);
    }

    // ------------------------------------------------------------------
    // Schemes
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("socks5", ProxyKind.Socks5)]
    [InlineData("socks5h", ProxyKind.Socks5)]
    [InlineData("socks", ProxyKind.Socks5)]
    [InlineData("SOCKS4", ProxyKind.Socks5)]
    [InlineData("https", ProxyKind.Https)]
    [InlineData("HTTP", ProxyKind.Http)]
    public void Understands_the_scheme_prefix(string scheme, ProxyKind expected)
    {
        var p = ProxyParser.ParseLine($"{scheme}://1.2.3.4:1080");

        Assert.NotNull(p);
        Assert.Equal(expected, p.Kind);
        Assert.Equal("1.2.3.4", p.Host);
    }

    [Fact]
    public void Socks4_is_folded_into_socks5_rather_than_rejected()
    {
        // Chromium's --proxy-server speaks both, and a user who typed socks4 wants
        // their proxy used, not a validation error about a version number.
        Assert.Equal(ProxyKind.Socks5, ProxyParser.NormaliseKind("socks4"));
    }

    [Fact]
    public void An_unknown_scheme_falls_back_to_http_rather_than_failing()
    {
        Assert.Equal(ProxyKind.Http, ProxyParser.NormaliseKind("gopher"));
    }

    [Fact]
    public void A_scheme_prefix_combines_with_every_shape()
    {
        var p = ProxyParser.ParseLine("socks5://gate.provider.com:1080:alice:s3cret");

        Assert.NotNull(p);
        Assert.Equal(ProxyKind.Socks5, p.Kind);
        Assert.Equal("alice", p.Username);
    }

    // ------------------------------------------------------------------
    // Rejection
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("not-a-proxy")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4:notaport")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:70000")]
    [InlineData(":8080")]
    public void Refuses_a_line_it_cannot_read(string line) =>
        Assert.Null(ProxyParser.ParseLine(line));

    [Fact]
    public void A_port_outside_the_valid_range_is_refused_not_clamped()
    {
        // Clamping would produce a proxy that connects somewhere the user never
        // typed, which is worse than telling them the line is wrong.
        Assert.Null(ProxyParser.ParseLine("1.2.3.4:65536"));
        Assert.NotNull(ProxyParser.ParseLine("1.2.3.4:65535"));
    }

    // ------------------------------------------------------------------
    // Host detection
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("gate.provider.com", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("8080", false)]
    [InlineData("alice", false)]
    [InlineData("", false)]
    public void Distinguishes_a_host_from_a_port_or_username(string token, bool expected) =>
        Assert.Equal(expected, ProxyParser.LooksLikeHost(token));

    // ------------------------------------------------------------------
    // Lists
    // ------------------------------------------------------------------

    [Fact]
    public void Parses_a_block_and_reports_bad_lines_by_number()
    {
        // Line numbers, not just a count: a user pasting two hundred proxies needs
        // to know which three failed, and "197 of 200" is not actionable.
        var text = "1.2.3.4:8080\nrubbish\n5.6.7.8:9090\n";

        var result = ProxyParser.ParseList(text);

        Assert.Equal(2, result.Proxies.Count);
        Assert.True(result.HasFailures);
        Assert.Equal(2, result.Failed[0].Line);
        Assert.Equal("rubbish", result.Failed[0].Text);
    }

    [Fact]
    public void Blank_lines_and_comments_are_skipped_not_counted_as_failures()
    {
        // Provider exports are full of both, and reporting them as errors would bury
        // the real failures.
        var result = ProxyParser.ParseList("# header\n\n1.2.3.4:8080\n\n   \n");

        Assert.Single(result.Proxies);
        Assert.False(result.HasFailures);
    }

    [Fact]
    public void Windows_line_endings_do_not_break_every_line()
    {
        // A paste from a Windows text file otherwise leaves \r on the port, and
        // every single line fails to parse.
        var result = ProxyParser.ParseList("1.2.3.4:8080\r\n5.6.7.8:9090\r\n");

        Assert.Equal(2, result.Proxies.Count);
        Assert.Equal(8080, result.Proxies[0].Port);
    }

    // ------------------------------------------------------------------
    // Formatting
    // ------------------------------------------------------------------

    [Fact]
    public void The_url_form_percent_encodes_credentials()
    {
        // A password with @ or : in it would otherwise be re-parsed as part of the
        // authority and silently truncated.
        var p = new ProxyConfig
        {
            Kind = ProxyKind.Http,
            Host = "1.2.3.4",
            Port = 8080,
            Username = "al ice",
            Password = "p@ss:word",
        };

        var url = ProxyParser.ToUrl(p);

        Assert.Equal("http://al%20ice:p%40ss%3Aword@1.2.3.4:8080", url);
    }

    [Fact]
    public void An_unconfigured_proxy_has_no_url()
    {
        Assert.Null(ProxyParser.ToUrl(new ProxyConfig { Kind = ProxyKind.None }));
        Assert.Null(ProxyParser.ToUrl(new ProxyConfig { Kind = ProxyKind.Http, Host = "x" }));
    }

    [Fact]
    public void The_display_form_never_contains_the_password()
    {
        // This is the string that reaches list rows, toasts and logs. A password in
        // it would end up in every screenshot and support ticket.
        var p = new ProxyConfig
        {
            Kind = ProxyKind.Socks5,
            Host = "1.2.3.4",
            Port = 1080,
            Username = "alice",
            Password = "s3cret",
        };

        var shown = ProxyParser.Describe(p);

        Assert.DoesNotContain("s3cret", shown);
        Assert.DoesNotContain("alice", shown);
        Assert.Contains("1.2.3.4:1080", shown);
        Assert.Contains("socks5", shown);
    }

    [Fact]
    public void A_direct_connection_says_so()
    {
        Assert.Equal("Direct", ProxyParser.Describe(new ProxyConfig { Kind = ProxyKind.None }));
    }

    [Fact]
    public void A_round_trip_through_the_url_form_preserves_everything()
    {
        var original = ProxyParser.ParseLine("socks5://alice:s3cret@gate.provider.com:1080");
        Assert.NotNull(original);

        var reparsed = ProxyParser.ParseLine(ProxyParser.ToUrl(original)!);

        Assert.NotNull(reparsed);
        Assert.Equal(original.Kind, reparsed.Kind);
        Assert.Equal(original.Host, reparsed.Host);
        Assert.Equal(original.Port, reparsed.Port);
    }
}
