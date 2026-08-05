using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloakHub.Core.Automation;
using CloakHub.Core.Model;

namespace CloakHub.Core.Tests;

/// <summary>
/// The automation API can launch browsers and hand out CDP URLs.
/// <para>
/// Every failure worth testing here is silent. An auth check that accepts anything
/// serves working responses — it just serves them to whatever else is on the
/// machine, including JavaScript in a page, which can reach 127.0.0.1. A listener
/// bound to 0.0.0.0 instead of loopback behaves identically from the developer's own
/// browser. Neither produces an error anywhere.
/// </para>
/// </summary>
public class AutomationServerTests : IAsyncLifetime
{
    private const string Token = "test-token-2f8c41d9";

    private readonly FakeHost _host = new();
    private AutomationServer _server = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _server = new AutomationServer(_host);
        _port = FreePort();

        await _server.StartAsync(new AutomationSettings
        {
            Enabled = true,
            Port = _port,
            Token = Token,
        });

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Authentication
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_request_with_no_token_is_refused()
    {
        // The load-bearing test. Everything else in this file assumes the endpoint is
        // shut to unauthenticated callers, and an API that launches browsers with no
        // token is a local privilege-escalation vector rather than a convenience.
        var response = await _client.GetAsync("/profiles");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_host.Started);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    // A prefix of the real token: a comparison that stops at the shorter length
    // accepts this, and nothing about the response would reveal it.
    [InlineData("test-token")]
    // The real token with a suffix, for the mirror-image bug.
    [InlineData(Token + "x")]
    public async Task A_request_with_the_wrong_token_is_refused(string presented)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/profiles");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {presented}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_the_right_token_is_served()
    {
        // The other half of the auth test: a check that refuses everything is just as
        // broken, and would show up as scripts that never work rather than as a
        // security hole.
        var response = await Authed(HttpMethod.Get, "/profiles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_reachable_without_a_token()
    {
        // Deliberately open, so a script can wait for the port to come up before it
        // holds the token. It must report nothing but liveness.
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    // ------------------------------------------------------------------
    // Binding
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_listener_refuses_to_start_without_a_token()
    {
        // A settings file saying "enabled, no token" must not silently become an open
        // endpoint. Generating one here would hide that state from the person who has
        // to trust it.
        await using var server = new AutomationServer(new FakeHost());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.StartAsync(new AutomationSettings
            {
                Enabled = true,
                Port = FreePort(),
                Token = "",
            }));
    }

    [Fact]
    public async Task A_disabled_configuration_binds_nothing()
    {
        await using var server = new AutomationServer(new FakeHost());

        await server.StartAsync(new AutomationSettings
        {
            Enabled = false,
            Port = FreePort(),
            Token = Token,
        });

        Assert.False(server.Running);
        Assert.Null(server.Port);
    }

    [Fact]
    public async Task A_port_already_in_use_is_reported_as_something_the_user_can_fix()
    {
        // The raw platform error is an errno. Surfacing that instead of "pick another
        // port in Settings" leaves the user with nothing to act on.
        await using var second = new AutomationServer(new FakeHost());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            second.StartAsync(new AutomationSettings
            {
                Enabled = true,
                Port = _port,
                Token = Token,
            }));

        Assert.Contains("in use", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Cross-origin
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_preflight_is_refused_and_no_cors_header_is_offered()
    {
        // A page cannot read a reply it has no Access-Control-Allow-Origin for. If
        // that header ever appeared, any site the user visits could drive their
        // browser profiles, and from the developer's side nothing would look wrong.
        using var request = new HttpRequestMessage(HttpMethod.Options, "/profiles");
        request.Headers.TryAddWithoutValidation("Origin", "https://example.com");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task An_authenticated_reply_carries_no_cors_header_either()
    {
        var response = await Authed(HttpMethod.Get, "/profiles");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ------------------------------------------------------------------
    // Routing
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_path_is_a_404_rather_than_a_silent_200()
    {
        // A catch-all that returns 200 makes a script's typo look like a success with
        // an empty result, which is the hardest kind of bug to find from the caller's
        // side.
        var response = await Authed(HttpMethod.Get, "/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_request_for_a_profile_that_does_not_exist_is_a_404()
    {
        var response = await Authed(HttpMethod.Get, "/profiles/missing-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Listing_profiles_reports_which_are_running()
    {
        _host.Profiles.Add(ProfileFactory.NewProfile("One", FingerprintPlatform.Windows));
        _host.Profiles.Add(ProfileFactory.NewProfile("Two", FingerprintPlatform.Linux));
        _host.RunningIds.Add(_host.Profiles[1].Id);

        var response = await Authed(HttpMethod.Get, "/profiles");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var profiles = body.GetProperty("profiles").EnumerateArray().ToList();

        Assert.Equal(2, profiles.Count);
        Assert.False(profiles[0].GetProperty("running").GetBoolean());
        Assert.True(profiles[1].GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Starting_a_session_that_fails_reports_the_reason()
    {
        // A start that fails silently leaves the script connecting to a browser that
        // was never launched, and the timeout it eventually hits names the wrong
        // cause.
        var profile = ProfileFactory.NewProfile("Blocked", FingerprintPlatform.Windows);
        _host.Profiles.Add(profile);
        _host.StartError = "Session limit reached (5).";

        var response = await Authed(HttpMethod.Post, $"/profiles/{profile.Id}/start");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("limit", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private Task<HttpResponseMessage> Authed(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        return _client.SendAsync(request);
    }

    /// <summary>A port the OS says is free right now.</summary>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A host that records what was asked of it and launches nothing.
    /// <para>
    /// The point of the interface: the routing, the auth and the status codes are all
    /// exercised without a browser anywhere near the test.
    /// </para>
    /// </summary>
    private sealed class FakeHost : IAutomationHost
    {
        public List<Profile> Profiles { get; } = [];
        public HashSet<string> RunningIds { get; } = [];
        public List<string> Started { get; } = [];
        public string? StartError { get; set; }

        public IReadOnlyList<Profile> ListProfiles() => Profiles;

        public Profile? GetProfile(string id) => Profiles.FirstOrDefault(p => p.Id == id);

        public Profile CreateProfile(string? name, FingerprintPlatform? platform)
        {
            var profile = ProfileFactory.NewProfile(
                name ?? "API profile", platform ?? FingerprintPlatform.Windows);

            Profiles.Add(profile);
            return profile;
        }

        public Profile? UpdateProfile(string id, Profile patched)
        {
            var index = Profiles.FindIndex(p => p.Id == id);
            if (index < 0) return null;

            Profiles[index] = patched;
            return patched;
        }

        public bool DeleteProfile(string id, bool deleteData) =>
            Profiles.RemoveAll(p => p.Id == id) > 0;

        public Task<string?> StartSessionAsync(string id, CancellationToken cancel)
        {
            if (StartError is not null) return Task.FromResult<string?>(StartError);

            Started.Add(id);
            RunningIds.Add(id);
            return Task.FromResult<string?>(null);
        }

        public Task StopSessionAsync(string id, CancellationToken cancel)
        {
            RunningIds.Remove(id);
            return Task.CompletedTask;
        }

        public AutomationEndpoint? Endpoint(string id) =>
            RunningIds.Contains(id)
                ? new AutomationEndpoint
                {
                    ProfileId = id,
                    ProfileName = GetProfile(id)?.Name ?? "",
                    WsEndpoint = "ws://127.0.0.1:9222/devtools/browser/fake",
                    HttpEndpoint = "http://127.0.0.1:9222",
                    Port = 9222,
                }
                : null;

        public bool IsRunning(string id) => RunningIds.Contains(id);

        public void Log(string message)
        {
            // Nothing to do: the tests assert on responses, not on the log.
        }
    }
}
