using System.Net;
using System.Text;
using CloakHub.Core.Licensing;

namespace CloakHub.Core.Tests;

/// <summary>
/// Tests for <see cref="LicenseClient"/> against canned server responses.
/// <para>
/// These exist because of a shipped bug that no other test could have caught.
/// <c>ValidateResponse</c> declared <c>ExpiresRaw</c> under the JSON name
/// "expires" alongside a computed property called <c>Expires</c>; with
/// case-insensitive matching, System.Text.Json rejected the entire type. Every
/// validate call threw, the blanket <c>catch</c> converted the throw to null,
/// and the licence panel rendered null as "Could not reach the license server".
/// </para>
/// <para>
/// The result was a total licensing failure that looked exactly like a network
/// outage — on a request that had in fact returned HTTP 200. Nothing short of
/// deserializing a realistic body through the real client would have exposed it,
/// which is precisely what these tests do.
/// </para>
/// </summary>
public class LicenseClientTests
{
    /// <summary>Returns one canned response, and records what was sent.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public string? SentBody { get; private set; }
        public Uri? SentUri { get; private set; }

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentUri = request.RequestUri;
            SentBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static LicenseClient ClientFor(StubHandler handler) =>
        new(new HttpClient(handler));

    // ------------------------------------------------------------------
    // The regression itself.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reads_a_valid_response_instead_of_reporting_it_unreachable()
    {
        // The exact body the live server returns for a good paid key.
        var handler = new StubHandler("""{"valid":true,"plan":"solo","expires":"2027-01-01"}""");
        using var client = ClientFor(handler);

        var check = await client.ValidateAsync("cb_test");

        // Null here is what the panel turns into "could not reach the server".
        Assert.NotNull(check);
        Assert.True(check!.Valid);
        Assert.Equal("solo", check.Plan);
        Assert.Equal("2027-01-01", check.Expires);
        Assert.Null(client.LastFailure);
    }

    [Fact]
    public async Task Reads_a_rejected_key_as_invalid_not_as_unreachable()
    {
        // The live server's answer for an unknown key: HTTP 200, valid=false.
        // Conflating this with a network failure hides a real "your key is bad".
        var handler = new StubHandler("""{"valid":false,"plan":"unknown","expires":null}""");
        using var client = ClientFor(handler);

        var check = await client.ValidateAsync("cb_bogus");

        Assert.NotNull(check);
        Assert.False(check!.Valid);
        Assert.Null(check.Expires);
    }

    // ------------------------------------------------------------------
    // The `expires` field is the part that varies across server versions,
    // and the reason it was a JsonElement in the first place.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"valid":true,"plan":"solo","expires":"2027-01-01"}""", "2027-01-01")]
    [InlineData("""{"valid":true,"plan":"solo","expires":null}""", null)]
    [InlineData("""{"valid":true,"plan":"solo"}""", null)]
    [InlineData("""{"valid":true,"plan":"solo","expires":1893456000}""", "1893456000")]
    public async Task Survives_every_shape_the_server_has_used_for_expires(string body, string? expected)
    {
        using var client = ClientFor(new StubHandler(body));

        var check = await client.ValidateAsync("cb_test");

        Assert.NotNull(check);
        Assert.Equal(expected, check!.Expires);
    }

    [Fact]
    public async Task An_unexpected_expires_shape_does_not_lose_the_verdict()
    {
        // An object where a string was expected must cost us the date, not the
        // whole answer — losing the answer is what reads as an outage.
        using var client = ClientFor(
            new StubHandler("""{"valid":true,"plan":"team","expires":{"at":"2027-01-01"}}"""));

        var check = await client.ValidateAsync("cb_test");

        Assert.NotNull(check);
        Assert.True(check!.Valid);
        Assert.Equal("team", check.Plan);
        Assert.Null(check.Expires);
    }

    // ------------------------------------------------------------------
    // Request shape. The server rejects anything but `license_key`.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sends_the_key_under_the_field_name_the_server_requires()
    {
        // Posting {"key":...} gets a 422 from the live API.
        var handler = new StubHandler("""{"valid":true,"plan":"solo"}""");
        using var client = ClientFor(handler);

        await client.ValidateAsync("cb_secret");

        Assert.Equal("""{"license_key":"cb_secret"}""", handler.SentBody);
    }

    [Fact]
    public async Task Posts_validation_to_the_validate_endpoint()
    {
        var handler = new StubHandler("""{"valid":true,"plan":"solo"}""");
        using var client = ClientFor(handler);

        await client.ValidateAsync("cb_test");

        Assert.Equal($"{LicenseClient.ApiBase}/api/license/validate", handler.SentUri?.ToString());
    }

    // ------------------------------------------------------------------
    // Failure reporting: null must be explainable.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_server_error_is_reported_as_a_server_error()
    {
        using var client = ClientFor(
            new StubHandler("""{"error":"nope"}""", HttpStatusCode.InternalServerError));

        Assert.Null(await client.ValidateAsync("cb_test"));
        Assert.Contains("500", client.LastFailure);
    }

    [Fact]
    public async Task Malformed_json_is_named_as_such_rather_than_blamed_on_the_network()
    {
        // The user's connection is fine; saying otherwise sends them to debug
        // the wrong machine entirely.
        using var client = ClientFor(new StubHandler("this is not json"));

        Assert.Null(await client.ValidateAsync("cb_test"));
        Assert.NotNull(client.LastFailure);
        Assert.Contains("could not be read", client.LastFailure);
    }

    [Fact]
    public async Task A_genuine_network_failure_leaves_the_reason_unset()
    {
        // No LastFailure means the caller falls back to its "could not reach the
        // license server" wording, which is accurate here and only here.
        using var client = new LicenseClient(new HttpClient(new ThrowingHandler()));

        Assert.Null(await client.ValidateAsync("cb_test"));
        Assert.Null(client.LastFailure);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no such host");
    }

    // ------------------------------------------------------------------
    // Session count.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reads_the_active_session_count()
    {
        using var client = ClientFor(new StubHandler("""{"active":3}"""));

        Assert.Equal(3, await client.ActiveSessionsAsync("cb_test"));
    }

    [Fact]
    public async Task A_forbidden_session_count_yields_unknown_rather_than_zero()
    {
        // The live server answers 403 for a key it does not recognise. Reporting
        // that as 0 active sessions would silently hand out a free seat.
        using var client = ClientFor(
            new StubHandler("""{"valid":false,"error":"invalid_key"}""", HttpStatusCode.Forbidden));

        Assert.Null(await client.ActiveSessionsAsync("cb_test"));
    }

    [Fact]
    public async Task A_cancelled_call_propagates_rather_than_looking_like_an_outage()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = ClientFor(new StubHandler("""{"valid":true}"""));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ValidateAsync("cb_test", cts.Token));
    }
}
