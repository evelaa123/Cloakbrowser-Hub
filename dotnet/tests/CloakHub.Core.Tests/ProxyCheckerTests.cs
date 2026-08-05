using System.Net;
using System.Text;
using CloakHub.Core.Model;
using CloakHub.Core.Network;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// A stubbed transport, so the checker is tested without the network.
/// <para>
/// Real requests would make the suite depend on three third-party geo services
/// being up and on this machine having an internet connection — which would turn
/// every unrelated failure into a red test.
/// </para>
/// </summary>
internal sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private int _index;

    public List<string> Requested { get; } = [];

    /// <summary>Thrown instead of responding, when set.</summary>
    public Exception? Throw { get; init; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested.Add(request.RequestUri!.ToString());

        if (Throw is not null) throw Throw;

        var response = _index < responses.Length ? responses[_index] : responses[^1];
        _index++;
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class ProxyCheckerTests
{
    private static ProxyConfig Working => new()
    {
        Kind = ProxyKind.Http,
        Host = "1.2.3.4",
        Port = 8080,
    };

    // ------------------------------------------------------------------
    // Refusals that never touch the network
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_direct_connection_is_not_something_to_check()
    {
        var result = await new ProxyChecker().CheckAsync(new ProxyConfig { Kind = ProxyKind.None });

        Assert.False(result.Ok);
        Assert.Contains("No proxy", result.Error);
    }

    [Fact]
    public async Task An_incomplete_configuration_is_reported_before_any_request()
    {
        var handler = new StubHandler();

        var result = await new ProxyChecker(handler)
            .CheckAsync(new ProxyConfig { Kind = ProxyKind.Http, Host = "1.2.3.4" });

        Assert.False(result.Ok);
        Assert.Empty(handler.Requested);
    }

    // ------------------------------------------------------------------
    // Success
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reports_the_exit_ip_and_location()
    {
        var handler = new StubHandler(StubHandler.Json("""
            {"status":"success","query":"9.8.7.6","country":"Germany","countryCode":"DE",
             "city":"Berlin","regionName":"Berlin","timezone":"Europe/Berlin","lat":52.5,"lon":13.4}
            """));

        var result = await new ProxyChecker(handler).CheckAsync(Working);

        Assert.True(result.Ok);
        Assert.Equal("9.8.7.6", result.Ip);
        Assert.Equal("Germany", result.Country);
        Assert.Equal("DE", result.CountryCode);
        Assert.Equal("Berlin", result.City);
        Assert.Equal("Europe/Berlin", result.Timezone);
        Assert.Equal(52.5, result.Latitude);
    }

    [Fact]
    public async Task Stops_at_the_first_endpoint_that_answers()
    {
        // These are shared free services with rate limits. Querying all three when
        // the first worked would triple the load for no extra information.
        var handler = new StubHandler(StubHandler.Json("""{"ip":"9.8.7.6"}"""));

        await new ProxyChecker(handler).CheckAsync(Working);

        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task Falls_through_to_the_next_endpoint_when_one_fails()
    {
        // One provider being down must not make every proxy in the library look
        // broken, which is exactly what a single-endpoint check would do.
        var handler = new StubHandler(
            StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable),
            StubHandler.Json("""{"ip":"9.8.7.6","success":true}"""));

        var result = await new ProxyChecker(handler).CheckAsync(Working);

        Assert.True(result.Ok);
        Assert.Equal(2, handler.Requested.Count);
    }

    [Fact]
    public async Task All_endpoints_failing_is_reported_once_not_three_times()
    {
        var handler = new StubHandler(StubHandler.Json("{}", HttpStatusCode.BadGateway));

        var result = await new ProxyChecker(handler).CheckAsync(Working);

        Assert.False(result.Ok);
        Assert.Equal(ProxyChecker.Endpoints.Length, handler.Requested.Count);
        Assert.NotNull(result.Error);
    }

    // ------------------------------------------------------------------
    // Payload shapes
    // ------------------------------------------------------------------

    [Fact]
    public void Reads_whichever_field_names_the_provider_used()
    {
        // ip-api says "query", the others say "ip"; regionName vs region_name vs
        // region. Normalising here keeps that mess out of the rest of the app.
        var a = ProxyChecker.ParseGeo("""{"query":"1.1.1.1","regionName":"Bavaria"}""");
        var b = ProxyChecker.ParseGeo("""{"ip":"1.1.1.1","region_name":"Bavaria"}""");
        var c = ProxyChecker.ParseGeo("""{"ip":"1.1.1.1","region":"Bavaria"}""");

        Assert.Equal("1.1.1.1", a?.Ip);
        Assert.Equal("Bavaria", a?.Region);
        Assert.Equal("Bavaria", b?.Region);
        Assert.Equal("Bavaria", c?.Region);
    }

    [Fact]
    public void A_timezone_that_arrives_as_an_object_is_still_read()
    {
        // ipwho.is nests it; ip-api returns a plain string. Missing the nested form
        // would leave the profile's timezone unset with no error shown.
        var nested = ProxyChecker.ParseGeo("""{"ip":"1.1.1.1","timezone":{"id":"Europe/Paris"}}""");
        var flat = ProxyChecker.ParseGeo("""{"ip":"1.1.1.1","timezone":"Europe/Paris"}""");
        var named = ProxyChecker.ParseGeo("""{"ip":"1.1.1.1","time_zone":{"name":"Europe/Paris"}}""");

        Assert.Equal("Europe/Paris", nested?.Timezone);
        Assert.Equal("Europe/Paris", flat?.Timezone);
        Assert.Equal("Europe/Paris", named?.Timezone);
    }

    [Fact]
    public void A_country_that_arrives_as_an_object_is_still_read()
    {
        var nested = ProxyChecker.ParseGeo("""{"ip":"1.1.1.1","country":{"name":"France"}}""");
        Assert.Equal("France", nested?.Country);
    }

    [Fact]
    public void A_rejection_carried_in_a_200_body_is_not_read_as_success()
    {
        // Both providers signal a refused lookup in the payload while returning 200.
        // Trusting the status code alone would report a healthy proxy with no IP.
        Assert.Null(ProxyChecker.ParseGeo("""{"status":"fail","message":"quota"}"""));
        Assert.Null(ProxyChecker.ParseGeo("""{"success":false,"message":"quota"}"""));
    }

    [Fact]
    public void A_response_with_no_ip_is_not_a_successful_check()
    {
        // The exit IP is the entire point. Without it there is nothing to report and
        // nothing downstream can use.
        Assert.Null(ProxyChecker.ParseGeo("""{"country":"Germany"}"""));
    }

    [Fact]
    public void Malformed_json_is_a_failed_lookup_not_a_crash()
    {
        Assert.Null(ProxyChecker.ParseGeo("<html>rate limited</html>"));
        Assert.Null(ProxyChecker.ParseGeo(""));
        Assert.Null(ProxyChecker.ParseGeo("[1,2,3]"));
    }

    // ------------------------------------------------------------------
    // Error messages
    // ------------------------------------------------------------------

    [Fact]
    public void A_timeout_says_so_in_plain_words()
    {
        Assert.Contains("Timed out", ProxyChecker.Describe(new TaskCanceledException()));
    }

    [Fact]
    public void A_refused_connection_points_at_the_host_and_port()
    {
        // Naming the field to check is the difference between a fixable error and a
        // socket code the user has to search for.
        var e = new HttpRequestException("boom",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));

        Assert.Contains("host and port", ProxyChecker.Describe(e));
    }

    [Fact]
    public void An_unresolvable_host_points_at_the_address()
    {
        var e = new HttpRequestException("boom",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));

        Assert.Contains("Host not found", ProxyChecker.Describe(e));
    }

    [Fact]
    public void A_407_points_at_the_credentials()
    {
        var e = new HttpRequestException("denied", null, HttpStatusCode.ProxyAuthenticationRequired);
        Assert.Contains("username and password", ProxyChecker.Describe(e));
    }

    [Fact]
    public void An_unrecognised_error_keeps_only_the_first_line()
    {
        // Stack-like multi-line messages otherwise fill the row and push the useful
        // part off screen.
        var described = ProxyChecker.Describe(new InvalidOperationException("first line\nsecond line"));
        Assert.Equal("first line", described);
    }

    // ------------------------------------------------------------------
    // Rotation
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_rotation_link_must_be_http()
    {
        // The link is fetched. Allowing file:// or a shell-ish scheme here would let
        // a pasted profile trigger something other than an HTTP request.
        var result = await new ProxyChecker().RotateAsync("file:///etc/passwd");

        Assert.False(result.Ok);
        Assert.Contains("http(s)", result.Error);
    }

    [Fact]
    public async Task A_malformed_rotation_link_is_rejected_rather_than_attempted()
    {
        var result = await new ProxyChecker().RotateAsync("not a url");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task A_successful_rotation_reports_the_status()
    {
        var handler = new StubHandler(StubHandler.Json("ok"));

        var result = await new ProxyChecker(handler).RotateAsync("https://provider.example/rotate");

        Assert.True(result.Ok);
        Assert.Equal(200, result.Status);
    }

    [Fact]
    public async Task A_rejected_rotation_is_not_reported_as_success()
    {
        var handler = new StubHandler(StubHandler.Json("no", HttpStatusCode.Forbidden));

        var result = await new ProxyChecker(handler).RotateAsync("https://provider.example/rotate");

        Assert.False(result.Ok);
        Assert.Equal(403, result.Status);
    }

    // ------------------------------------------------------------------
    // Handler construction
    // ------------------------------------------------------------------

    [Fact]
    public void The_handler_actually_routes_through_the_proxy()
    {
        // If UseProxy were ever false the check would report the host's own IP and
        // present it as the proxy's -- a green result for a proxy that is not being
        // used at all, which is the most dangerous outcome this code can produce.
        using var handler = (HttpClientHandler)ProxyChecker.BuildHandler(Working);

        Assert.True(handler.UseProxy);
        Assert.NotNull(handler.Proxy);
    }

    [Fact]
    public void Credentials_are_attached_to_the_proxy_not_left_in_the_address()
    {
        var proxy = Working with { Username = "alice", Password = "s3cret" };

        using var handler = (HttpClientHandler)ProxyChecker.BuildHandler(proxy);
        var web = (WebProxy)handler.Proxy!;

        Assert.NotNull(web.Credentials);
        Assert.DoesNotContain("alice", web.Address!.ToString());
    }
}
