using System.Text;
using CloakHub.Core.Model;
using CloakHub.Core.Network;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// The proxy flags, and the relay that stands behind an authenticated one.
/// </summary>
public sealed class ProxyArgsTests
{
    private static ProxyConfig Http => new()
    {
        Kind = ProxyKind.Http,
        Host = "1.2.3.4",
        Port = 8080,
    };

    // ------------------------------------------------------------------
    // Flags
    // ------------------------------------------------------------------

    [Fact]
    public void A_direct_connection_produces_no_flags()
    {
        // Not an empty --proxy-server. Chromium treats that as a malformed proxy and
        // fails every request rather than going direct.
        using var launch = ProxyArgs.Build(new ProxyConfig { Kind = ProxyKind.None });
        Assert.Empty(launch.Args);
    }

    [Fact]
    public void An_incomplete_proxy_produces_no_flags()
    {
        using var launch = ProxyArgs.Build(new ProxyConfig { Kind = ProxyKind.Http, Host = "1.2.3.4" });
        Assert.Empty(launch.Args);
    }

    [Fact]
    public void An_unauthenticated_proxy_is_passed_straight_through()
    {
        // No relay needed, so none is created -- an unnecessary listening socket per
        // session would be pure cost.
        using var launch = ProxyArgs.Build(Http);

        Assert.Contains("--proxy-server=http://1.2.3.4:8080", launch.Args);
        Assert.Null(launch.Relay);
    }

    [Fact]
    public void Socks5_carries_its_credentials_inline()
    {
        // SOCKS5 inline auth has always worked in Chromium, so the relay would add a
        // hop for nothing.
        using var launch = ProxyArgs.Build(new ProxyConfig
        {
            Kind = ProxyKind.Socks5,
            Host = "1.2.3.4",
            Port = 1080,
            Username = "alice",
            Password = "s3cret",
        });

        Assert.Contains("--proxy-server=socks5://alice:s3cret@1.2.3.4:1080", launch.Args);
        Assert.Null(launch.Relay);
    }

    [Fact]
    public void An_authenticated_http_proxy_goes_through_a_loopback_relay()
    {
        // Older Chromium builds read user:pass@host as a hostname and drop the
        // credentials entirely, which surfaces as every page failing with 407. The
        // relay makes the behaviour identical on every binary.
        using var launch = ProxyArgs.Build(Http with { Username = "alice", Password = "s3cret" });

        Assert.NotNull(launch.Relay);

        var flag = launch.Args.Single(a => a.StartsWith("--proxy-server=", StringComparison.Ordinal));

        Assert.StartsWith("--proxy-server=http://127.0.0.1:", flag);
        Assert.DoesNotContain("alice", flag);
        Assert.DoesNotContain("s3cret", flag);
    }

    [Fact]
    public void Special_characters_in_credentials_are_percent_encoded()
    {
        // Chromium's parser truncates a password at certain characters. Passing it
        // through raw silently produces a shorter, wrong password and a 407 the user
        // cannot explain.
        var encoded = ProxyArgs.Encode(new ProxyConfig
        {
            Kind = ProxyKind.Socks5,
            Host = "1.2.3.4",
            Port = 1080,
            Username = "al ice",
            Password = "p@ss=word",
        });

        Assert.Contains("al%20ice", encoded);
        Assert.Contains("p%40ss%3Dword", encoded);
    }

    [Fact]
    public void A_bypass_list_is_forwarded()
    {
        using var launch = ProxyArgs.Build(Http with { Bypass = "*.internal,10.0.0.0/8" });

        var flag = launch.Args.Single(a => a.StartsWith("--proxy-bypass-list=", StringComparison.Ordinal));

        Assert.Contains("*.internal", flag);
        Assert.Contains("10.0.0.0/8", flag);
    }

    [Fact]
    public void Loopback_is_always_added_to_the_bypass_list()
    {
        // Without it the automation API and any local tooling would be routed out
        // through the proxy and back, which fails -- and is slow when it does not.
        using var launch = ProxyArgs.Build(Http with { Bypass = "*.internal" });

        var flag = launch.Args.Single(a => a.StartsWith("--proxy-bypass-list=", StringComparison.Ordinal));
        Assert.Contains("<-loopback>", flag);
    }

    [Fact]
    public void No_bypass_setting_means_no_bypass_flag()
    {
        using var launch = ProxyArgs.Build(Http);
        Assert.DoesNotContain(launch.Args, a => a.StartsWith("--proxy-bypass-list", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Relay
    // ------------------------------------------------------------------

    [Fact]
    public void The_relay_listens_on_loopback_only()
    {
        // It carries the user's proxy credentials. Bound to every interface it would
        // be an open authenticated proxy for anyone on the network, so the address is
        // deliberately not configurable.
        using var relay = new ProxyRelay(Http with { Username = "alice", Password = "s3cret" });
        relay.Start();

        Assert.StartsWith("127.0.0.1:", relay.Endpoint);
        Assert.InRange(relay.Port, 1024, 65535);
    }

    [Fact]
    public void Two_relays_never_collide_on_a_port()
    {
        // Several profiles can start at once, and a fixed or counted port would make
        // the second one fail with "address in use".
        using var a = new ProxyRelay(Http with { Username = "alice" });
        using var b = new ProxyRelay(Http with { Username = "bob" });

        a.Start();
        b.Start();

        Assert.NotEqual(a.Port, b.Port);
    }

    [Fact]
    public void Starting_twice_is_harmless()
    {
        using var relay = new ProxyRelay(Http with { Username = "alice" });

        relay.Start();
        var first = relay.Port;
        relay.Start();

        Assert.Equal(first, relay.Port);
    }

    [Fact]
    public void Disposing_releases_the_port()
    {
        var relay = new ProxyRelay(Http with { Username = "alice" });
        relay.Start();
        relay.Dispose();

        // Idempotent, because the session teardown path can reach this more than
        // once and a second Dispose must not throw during shutdown.
        relay.Dispose();
    }

    [Fact]
    public void The_relay_adds_the_authorization_header()
    {
        var header = "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n";

        var rewritten = ProxyRelay.WithCredentials(
            header, Http with { Username = "alice", Password = "s3cret" });

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret"));

        Assert.Contains($"Proxy-Authorization: Basic {expected}", rewritten);
    }

    [Fact]
    public void The_request_line_stays_first()
    {
        // Inserting before it would produce a request no proxy can parse.
        var header = "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n";

        var rewritten = ProxyRelay.WithCredentials(header, Http with { Username = "alice" });

        Assert.StartsWith("CONNECT example.com:443 HTTP/1.1\r\n", rewritten);
    }

    [Fact]
    public void An_existing_authorization_header_is_replaced_not_duplicated()
    {
        // Some proxies reject a request carrying two of them outright.
        var header =
            "CONNECT example.com:443 HTTP/1.1\r\n" +
            "Proxy-Authorization: Basic stale\r\n" +
            "Host: example.com:443\r\n\r\n";

        var rewritten = ProxyRelay.WithCredentials(header, Http with { Username = "alice" });

        Assert.DoesNotContain("stale", rewritten);
        Assert.Equal(1, rewritten.Split("Proxy-Authorization:").Length - 1);
    }

    [Fact]
    public void An_empty_password_still_produces_a_valid_header()
    {
        var header = "CONNECT example.com:443 HTTP/1.1\r\n\r\n";

        var rewritten = ProxyRelay.WithCredentials(header, Http with { Username = "token" });
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("token:"));

        Assert.Contains(expected, rewritten);
    }
}
