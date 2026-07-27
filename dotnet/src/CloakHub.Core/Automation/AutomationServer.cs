using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloakHub.Core.Model;

namespace CloakHub.Core.Automation;

/// <summary>
/// Local automation REST API.
/// <para>
/// Lets a script drive the app the way Dolphin Anty's local API does: list
/// profiles, start one, get back a CDP endpoint, attach Puppeteer / Playwright /
/// Selenium, then stop it. Without this every launch is a manual click, which rules
/// out the entire class of work people buy an anti-detect browser for — bulk account
/// tasks, scheduled checks, scraping under a stable identity.
/// </para>
/// <para><b>Security decisions, all deliberate:</b></para>
/// <list type="bullet">
///   <item><b>Loopback only.</b> This endpoint starts browsers and hands out CDP
///     URLs that permit arbitrary page control and cookie theft, so it must never
///     be reachable off-box. There is intentionally no setting for the host.</item>
///   <item><b>Bearer token on every request, compared in constant time.</b> A web
///     page's own JavaScript can issue requests to 127.0.0.1, so "it is only local"
///     is not a boundary by itself.</item>
///   <item><b>CORS preflight refused outright.</b> No
///     <c>Access-Control-Allow-Origin</c> is ever sent, so a page cannot read a
///     reply; rejecting OPTIONS makes that explicit rather than implicit.</item>
/// </list>
/// </summary>
public sealed class AutomationServer : IAsyncDisposable
{
    private const int MaxBodyBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IAutomationHost _host;

    private HttpListener? _listener;
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private byte[] _tokenHash = [];

    public AutomationServer(IAutomationHost host) => _host = host;

    public bool Running => _listener?.IsListening == true;

    public int? Port { get; private set; }

    /// <summary>
    /// Start listening. Idempotent — restarts cleanly if the port or token changed.
    /// </summary>
    public async Task StartAsync(AutomationSettings settings, CancellationToken cancel = default)
    {
        await StopAsync().ConfigureAwait(false);

        if (!settings.Enabled) return;

        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            // Refused rather than defaulted. An unauthenticated endpoint that can
            // launch browsers is a local privilege-escalation vector, and silently
            // generating a token here would hide a settings file that says
            // "enabled, no token" from the person who has to trust it.
            throw new InvalidOperationException("Refusing to start the automation API without a token.");
        }

        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token));

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{settings.Port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            listener.Close();

            // Reported as an actionable message rather than a raw errno: "address
            // in use" is something the user fixes in Settings, and the platform
            // error code does not tell them that.
            throw new InvalidOperationException(
                e.ErrorCode is 48 or 98 or 183
                    ? $"Port {settings.Port} is already in use. Pick another in Settings."
                    : $"Could not start the automation API on port {settings.Port}: {e.Message}",
                e);
        }

        _listener = listener;
        Port = settings.Port;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _loop = Task.Run(() => AcceptLoopAsync(listener, _stopping.Token), CancellationToken.None);

        _host.Log($"Automation API listening on http://127.0.0.1:{settings.Port}");
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        var stopping = _stopping;
        var loop = _loop;

        _listener = null;
        _stopping = null;
        _loop = null;
        Port = null;

        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            stopping.Dispose();
        }

        if (listener is not null)
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
                // Already torn down.
            }
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // The loop ends by way of the listener closing under it, which
                // surfaces as an exception. That is the expected shutdown path.
            }
        }

        // The token hash is cleared so a stopped server cannot authorise anything
        // even if a stray request were somehow dispatched.
        _tokenHash = [];
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                // The listener was stopped, or the accept failed. Either way there
                // is nothing to serve.
                return;
            }

            // Not awaited: one slow request — a start that takes ten seconds to
            // launch a browser — must not block every other client.
            _ = Task.Run(() => HandleAsync(context, cancel), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancel)
    {
        try
        {
            await RouteAsync(context, cancel).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            try
            {
                await SendAsync(context, 400, new { error = FirstLine(e.Message) }).ConfigureAwait(false);
            }
            catch
            {
                // The client hung up mid-error; nothing further to do.
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // Already closed.
            }
        }
    }

    private async Task RouteAsync(HttpListenerContext context, CancellationToken cancel)
    {
        var request = context.Request;
        var method = request.HttpMethod.ToUpperInvariant();

        // Never advertise CORS. A cross-origin page may still *send* a simple
        // request, but the missing token stops it acting and the absent
        // Access-Control-Allow-Origin stops it reading any reply.
        if (method == "OPTIONS")
        {
            await SendAsync(context, 405, new { error = "Cross-origin requests are not supported." })
                .ConfigureAwait(false);
            return;
        }

        var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0) path = "/";

        // /health is unauthenticated on purpose: it reports nothing but liveness and
        // a version, so a script can wait for the port to come up before it holds
        // the token.
        if (path == "/health" && method == "GET")
        {
            await SendAsync(context, 200, new { ok = true, api = "cloakbrowser-hub", version = 1 })
                .ConfigureAwait(false);
            return;
        }

        if (!Authorised(request))
        {
            // No detail about why. Distinguishing "missing" from "wrong" only helps
            // someone probing the port.
            await SendAsync(context, 401, new { error = "Unauthorized." }).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(request, cancel).ConfigureAwait(false);

        if (path == "/profiles" && method == "GET")
        {
            var profiles = _host.ListProfiles().Select(p => new
            {
                id = p.Id,
                name = p.Name,
                platform = p.Fingerprint.Platform,
                running = _host.IsRunning(p.Id),
            });

            await SendAsync(context, 200, new { profiles }).ConfigureAwait(false);
            return;
        }

        if (path == "/profiles" && method == "POST")
        {
            var spec = Parse<CreateProfileBody>(body);
            var created = _host.CreateProfile(spec?.Name, spec?.Platform);
            await SendAsync(context, 201, new { profile = created }).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/profiles/", StringComparison.Ordinal))
        {
            await ProfileRouteAsync(context, path, method, body, cancel).ConfigureAwait(false);
            return;
        }

        await SendAsync(context, 404, new { error = $"No route for {method} {path}" }).ConfigureAwait(false);
    }

    private async Task ProfileRouteAsync(
        HttpListenerContext context,
        string path,
        string method,
        string? body,
        CancellationToken cancel)
    {
        var rest = path["/profiles/".Length..];
        var slash = rest.IndexOf('/');

        var id = Uri.UnescapeDataString(slash < 0 ? rest : rest[..slash]);
        var action = slash < 0 ? "" : rest[(slash + 1)..];

        var profile = _host.GetProfile(id);
        if (profile is null)
        {
            await SendAsync(context, 404, new { error = $"No profile with id \"{id}\"." }).ConfigureAwait(false);
            return;
        }

        switch (action, method)
        {
            case ("", "GET"):
                await SendAsync(context, 200, new { profile }).ConfigureAwait(false);
                return;

            case ("", "PATCH"):
            {
                var patch = Parse<UpdateProfileBody>(body);
                var updated = _host.UpdateProfile(id, Apply(profile, patch));
                await SendAsync(context, 200, new { profile = updated }).ConfigureAwait(false);
                return;
            }

            case ("", "DELETE"):
            {
                if (_host.IsRunning(id))
                {
                    await SendAsync(context, 409, new { error = "Stop the session before deleting the profile." })
                        .ConfigureAwait(false);
                    return;
                }

                // Data is deleted unless the caller opts out. The inverse default
                // would leave orphaned user-data directories that nothing in the UI
                // can then find or clean up.
                var keepData = string.Equals(
                    context.Request.QueryString["keepData"], "true", StringComparison.OrdinalIgnoreCase);

                await SendAsync(context, 200, new { deleted = _host.DeleteProfile(id, !keepData) })
                    .ConfigureAwait(false);
                return;
            }

            case ("start", "POST"):
            {
                if (_host.IsRunning(id))
                {
                    // Idempotent: a retry after a client-side timeout must not be an
                    // error, so the existing endpoint is returned instead.
                    var existing = _host.Endpoint(id);
                    if (existing is not null)
                    {
                        await SendAsync(context, 200, Endpoint(existing, alreadyRunning: true))
                            .ConfigureAwait(false);
                        return;
                    }

                    await SendAsync(context, 409, new { error = "Profile is already running." })
                        .ConfigureAwait(false);
                    return;
                }

                var error = await _host.StartSessionAsync(id, cancel).ConfigureAwait(false);
                if (error is not null)
                {
                    await SendAsync(context, 500, new { error }).ConfigureAwait(false);
                    return;
                }

                var endpoint = _host.Endpoint(id);
                if (endpoint is null)
                {
                    await SendAsync(context, 500, new
                    {
                        error = "Session started but no CDP endpoint is available. Enable automation " +
                                "for this profile and restart the session.",
                    }).ConfigureAwait(false);
                    return;
                }

                await SendAsync(context, 200, Endpoint(endpoint, alreadyRunning: false)).ConfigureAwait(false);
                return;
            }

            case ("stop", "POST"):
                await _host.StopSessionAsync(id, cancel).ConfigureAwait(false);
                await SendAsync(context, 200, new { stopped = true }).ConfigureAwait(false);
                return;

            case ("endpoint", "GET"):
            {
                var endpoint = _host.Endpoint(id);
                if (endpoint is null)
                {
                    await SendAsync(context, 404, new
                    {
                        error = "That profile has no automation endpoint (not running?).",
                    }).ConfigureAwait(false);
                    return;
                }

                await SendAsync(context, 200, Endpoint(endpoint, alreadyRunning: false)).ConfigureAwait(false);
                return;
            }

            default:
                await SendAsync(context, 404, new { error = $"No route for {method} {path}" })
                    .ConfigureAwait(false);
                return;
        }
    }

    private static object Endpoint(AutomationEndpoint e, bool alreadyRunning) => new
    {
        profileId = e.ProfileId,
        profileName = e.ProfileName,
        wsEndpoint = e.WsEndpoint,
        httpEndpoint = e.HttpEndpoint,
        port = e.Port,
        alreadyRunning,
    };

    /// <summary>
    /// Apply a patch body to a profile.
    /// <para>
    /// Only the fields a script has any business setting remotely. Deserialising the
    /// whole <c>Profile</c> from the request would let a caller rewrite the id or the
    /// timestamps, and a mismatched id would silently create an orphan.
    /// </para>
    /// </summary>
    private static Profile Apply(Profile profile, UpdateProfileBody? patch)
    {
        if (patch is null) return profile;

        var updated = profile;

        if (!string.IsNullOrWhiteSpace(patch.Name))
            updated = updated with { Name = patch.Name.Trim() };

        if (patch.Notes is not null)
            updated = updated with { Notes = patch.Notes };

        if (patch.Status is { } status)
            updated = updated with { Status = status };

        if (patch.Tags is not null)
            updated = updated with { Tags = [.. patch.Tags] };

        if (patch.Proxy is not null)
            updated = updated with { Proxy = patch.Proxy };

        if (patch.Locale is not null)
            updated = updated with { Locale = patch.Locale };

        if (patch.Startup is not null)
            updated = updated with { Startup = patch.Startup };

        return updated;
    }

    private bool Authorised(HttpListenerRequest request)
    {
        if (_tokenHash.Length == 0) return false;

        var header = request.Headers["Authorization"] ?? "";
        var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..]
            : request.Headers["X-Api-Token"] ?? "";

        // Both sides are hashed to a fixed width before comparison. Comparing the
        // raw strings would leak the expected length through the timing of the
        // length check itself, which is the failure that makes a naive
        // "constant-time" compare not one.
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(presentedHash, _tokenHash);
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancel)
    {
        if (request.HttpMethod is "GET" or "DELETE") return null;
        if (!request.HasEntityBody) return null;

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;

        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0) break;

            total += read;

            // Capped so a stray large POST cannot exhaust the app's memory. Checked
            // while reading rather than from Content-Length, which a client is free
            // to understate.
            if (total > MaxBodyBytes) throw new InvalidOperationException("Request body too large.");

            ms.Write(buffer, 0, read);
        }

        var text = Encoding.UTF8.GetString(ms.ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    private static T? Parse<T>(string? body) where T : class
    {
        if (body is null) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Body must be valid JSON.");
        }
    }

    private static async Task SendAsync(HttpListenerContext context, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);

        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;

        // Defence in depth: this API returns JSON only, never a document, so a
        // browser must not be allowed to sniff a response into something renderable.
        response.Headers["X-Content-Type-Options"] = "nosniff";

        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline];
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class CreateProfileBody
    {
        public string? Name { get; set; }
        public FingerprintPlatform? Platform { get; set; }
    }

    private sealed class UpdateProfileBody
    {
        public string? Name { get; set; }
        public string? Notes { get; set; }
        public ProfileStatus? Status { get; set; }
        public List<string>? Tags { get; set; }
        public ProxyConfig? Proxy { get; set; }
        public LocaleConfig? Locale { get; set; }
        public StartupConfig? Startup { get; set; }
    }
}
