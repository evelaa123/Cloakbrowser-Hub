using System.Net;
using System.Text.Json;
using CloakHub.Core.Model;

namespace CloakHub.Core.Network;

/// <summary>
/// Verifies a proxy by using it.
/// <para>
/// A reachability check would only prove the port is open. What every downstream
/// decision actually needs is the <em>exit</em> IP — the address a site will see —
/// because that is what the timezone, locale and WebRTC settings have to agree
/// with. The only way to learn it is to make a real request through the proxy and
/// ask what address arrived.
/// </para>
/// </summary>
public sealed class ProxyChecker(HttpMessageHandler? handler = null, Func<long>? clock = null)
{
    /// <summary>
    /// Geo endpoints, tried in order.
    /// <para>
    /// Three providers rather than one: these are free services with rate limits,
    /// and a single one being down would otherwise make every proxy in the library
    /// look broken at once — which is far worse than a slow check.
    /// </para>
    /// </summary>
    public static readonly string[] Endpoints =
    [
        "http://ip-api.com/json/?fields=status,country,countryCode,regionName,city,timezone,lat,lon,query",
        "https://ipwho.is/",
        "https://ipapi.co/json/",
    ];

    /// <summary>
    /// Long enough for a slow residential proxy, short enough that a dead one does
    /// not hold up a bulk check of the whole library.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Func<long> _now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Run a request through the proxy and report the exit IP and geo data.</summary>
    public async Task<ProxyCheckResult> CheckAsync(ProxyConfig proxy, CancellationToken ct = default)
    {
        var startedAt = _now();

        if (proxy.Kind == ProxyKind.None)
            return new ProxyCheckResult { Ok = false, CheckedAt = startedAt, Error = "No proxy configured." };

        if (!proxy.IsConfigured)
            return new ProxyCheckResult { Ok = false, CheckedAt = startedAt, Error = "Incomplete proxy configuration." };

        HttpMessageHandler transport;
        var ownsTransport = false;

        if (handler is not null)
        {
            transport = handler;
        }
        else
        {
            try
            {
                transport = BuildHandler(proxy);
                ownsTransport = true;
            }
            catch (Exception e)
            {
                return new ProxyCheckResult
                {
                    Ok = false,
                    CheckedAt = _now(),
                    Error = $"Could not create a proxy client: {e.Message}",
                };
            }
        }

        using var client = new HttpClient(transport, disposeHandler: ownsTransport)
        {
            Timeout = Timeout,
        };

        var lastError = "All geo lookup services failed.";

        foreach (var endpoint in Endpoints)
        {
            ct.ThrowIfCancellationRequested();

            var t0 = _now();
            try
            {
                using var response = await client.GetAsync(endpoint, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var geo = ParseGeo(body);

                if (geo is null) throw new HttpRequestException("Lookup returned no IP address.");

                return geo with
                {
                    Ok = true,
                    CheckedAt = _now(),
                    LatencyMs = (int)(_now() - t0),
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The user navigated away or cancelled a bulk check. Not a proxy
                // fault, and must not be recorded as one.
                throw;
            }
            catch (Exception e)
            {
                lastError = Describe(e);
            }
        }

        return new ProxyCheckResult { Ok = false, CheckedAt = _now(), Error = lastError };
    }

    /// <summary>
    /// A handler that routes through the proxy.
    /// <para>
    /// SOCKS5 needs no extra package here: <see cref="WebProxy"/> understands the
    /// scheme directly, so a SOCKS proxy is checked the same way as any other
    /// rather than being reported as uncheckable.
    /// </para>
    /// </summary>
    internal static HttpMessageHandler BuildHandler(ProxyConfig proxy)
    {
        var url = ProxyParser.ToUrl(proxy)
            ?? throw new InvalidOperationException("Incomplete proxy configuration.");

        // Credentials go on the WebProxy rather than staying in the URI, so they are
        // not carried into anything that later stringifies the address.
        var web = new WebProxy(StripCredentials(url));

        if (!string.IsNullOrEmpty(proxy.Username))
            web.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? "");

        return new HttpClientHandler
        {
            Proxy = web,
            UseProxy = true,

            // A direct connection would report the host's own IP and present it as
            // the proxy's. That reads as a healthy green result while the proxy is
            // in fact not being used at all -- the single most dangerous outcome
            // this check can produce, so it is disabled outright.
            UseDefaultCredentials = false,
        };
    }

    private static string StripCredentials(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme == -1) return url;

        var rest = url[(scheme + 3)..];
        var at = rest.LastIndexOf('@');
        return at == -1 ? url : url[..(scheme + 3)] + rest[(at + 1)..];
    }

    /// <summary>
    /// Read whichever field names this provider happened to use.
    /// <para>
    /// The three endpoints disagree on almost every key — <c>query</c> versus
    /// <c>ip</c>, <c>regionName</c> versus <c>region</c>, a timezone that is
    /// sometimes a string and sometimes an object. Normalising here keeps that
    /// mess out of the rest of the app.
    /// </para>
    /// </summary>
    internal static ProxyCheckResult? ParseGeo(string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object) return null;

        // Both providers signal a rejected lookup in the body with a 200 status, so
        // the payload has to be checked as well as the status code.
        if (Str(root, "status") == "fail") return null;
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;

        var ip = Str(root, "ip") ?? Str(root, "query");
        if (string.IsNullOrWhiteSpace(ip)) return null;

        return new ProxyCheckResult
        {
            Ip = ip,
            Country = Str(root, "country") ?? Str(root, "country_name"),
            CountryCode = Str(root, "countryCode") ?? Str(root, "country_code"),
            City = Str(root, "city"),
            Region = Str(root, "regionName") ?? Str(root, "region_name") ?? Str(root, "region"),
            Timezone = Timezone(root),
            Latitude = Num(root, "lat") ?? Num(root, "latitude"),
            Longitude = Num(root, "lon") ?? Num(root, "longitude"),
        };
    }

    private static string? Timezone(JsonElement root)
    {
        if (root.TryGetProperty("timezone", out var tz))
        {
            if (tz.ValueKind == JsonValueKind.String) return tz.GetString();
            if (tz.ValueKind == JsonValueKind.Object) return Str(tz, "id");
        }

        if (root.TryGetProperty("time_zone", out var t) && t.ValueKind == JsonValueKind.Object)
            return Str(t, "name") ?? Str(t, "id");

        return null;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;

        // Some providers nest the country as { name: "Germany" } rather than a
        // plain string.
        if (v.ValueKind == JsonValueKind.Object) return Str(v, "name");

        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    /// <summary>
    /// Turn a network exception into something the user can act on.
    /// <para>
    /// The raw messages name sockets and status codes. What a user needs to know is
    /// which of the four things they typed is wrong, so each condition is mapped to
    /// the field it implicates.
    /// </para>
    /// </summary>
    internal static string Describe(Exception e)
    {
        if (e is TaskCanceledException or TimeoutException)
            return "Timed out — the proxy did not respond in time.";

        if (e is HttpRequestException http)
        {
            if (http.InnerException is System.Net.Sockets.SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.ConnectionRefused =>
                        "Connection refused — check the host and port.",
                    System.Net.Sockets.SocketError.HostNotFound or
                    System.Net.Sockets.SocketError.NoData or
                    System.Net.Sockets.SocketError.TryAgain =>
                        "Host not found — check the proxy address.",
                    System.Net.Sockets.SocketError.ConnectionReset =>
                        "Connection reset by the proxy.",
                    System.Net.Sockets.SocketError.TimedOut =>
                        "Timed out — the proxy did not respond in time.",
                    _ => $"Could not connect through the proxy ({socket.SocketErrorCode}).",
                };
            }

            if (http.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return "Proxy authentication failed — check the username and password.";
        }

        if (e is System.Security.Authentication.AuthenticationException)
            return "TLS error while connecting through the proxy.";

        var message = e.Message.Split('\n')[0].Trim();
        return message.Length == 0 ? "Unknown proxy error." : message;
    }

    /// <summary>
    /// Ask the provider to rotate the exit IP.
    /// <para>
    /// Sent directly rather than through the proxy: rotation endpoints authorise by
    /// the caller's own address, and routing the request through the very proxy
    /// being rotated is how it ends up refused.
    /// </para>
    /// </summary>
    public async Task<RotationResult> RotateAsync(string rotationUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(rotationUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new RotationResult(false, null, "The rotation link must be an http(s) URL.");
        }

        using var client = handler is null
            ? new HttpClient { Timeout = Timeout }
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout };

        try
        {
            using var response = await client.GetAsync(uri, ct).ConfigureAwait(false);
            return new RotationResult(response.IsSuccessStatusCode, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new RotationResult(false, null, Describe(e));
        }
    }
}

/// <summary>Outcome of a rotation request.</summary>
public sealed record RotationResult(bool Ok, int? Status, string? Error);
