using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloakHub.Core.Licensing;

/// <summary>
/// Talks to the CloakBrowser license API.
/// <para>
/// Every method returns <c>null</c> for "could not tell" rather than throwing or
/// returning a false. That is the single most important property of this class: a
/// network failure must never be rendered as "your key is invalid", because the
/// user's only reasonable response to that message — buy a new key — is the wrong
/// action, and they will take it.
/// </para>
/// </summary>
public sealed class LicenseClient : IDisposable
{
    public const string ApiBase = "https://cloakbrowser.dev";

    /// <summary>Opens in the system browser; CloakBrowser emails a free key to the GitHub address.</summary>
    public const string GithubSignInUrl = $"{ApiBase}/api/license/free/github/start";

    public const string PricingUrl = $"{ApiBase}/";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public LicenseClient(HttpClient? http = null)
    {
        _ownsClient = http is null;

        // A short timeout on purpose. This call sits in front of the licence panel
        // and, indirectly, of a launch; a user on a captive-portal Wi-Fi where the
        // request hangs rather than fails should wait twelve seconds and get a
        // clear "could not reach the server", not a spinner that never resolves.
        _http = http ?? new HttpClient { Timeout = Timeout };
    }

    /// <summary>
    /// Validate a key.
    /// <para>
    /// Null means the server could not be reached — deliberately distinct from a
    /// result with <c>Valid = false</c>.
    /// </para>
    /// </summary>
    public async Task<LicenseCheck?> ValidateAsync(string key, CancellationToken cancel = default)
    {
        var body = await PostAsync<ValidateResponse>(
            $"{ApiBase}/api/license/validate", key, cancel).ConfigureAwait(false);

        if (body is null) return null;

        return new LicenseCheck
        {
            Valid = body.Valid,
            // The plan defaults to "solo" rather than empty because an older server
            // build omits the field for paid keys, and an empty plan would resolve
            // to unknown seats and silently fall back to the user's preference.
            Plan = string.IsNullOrWhiteSpace(body.Plan) ? "solo" : body.Plan.Trim(),
            Expires = string.IsNullOrWhiteSpace(body.Expires) ? null : body.Expires.Trim(),
        };
    }

    /// <summary>
    /// Concurrent sessions the server currently counts against this key.
    /// <para>
    /// Never cached: a stale seat count is a wrong seat count, and the number's
    /// only purpose is to tell the user why a launch was refused right now.
    /// </para>
    /// </summary>
    public async Task<int?> ActiveSessionsAsync(string key, CancellationToken cancel = default)
    {
        var body = await PostAsync<SessionCountResponse>(
            $"{ApiBase}/api/license/session/count", key, cancel).ConfigureAwait(false);

        return body?.Active;
    }

    private async Task<T?> PostAsync<T>(string url, string key, CancellationToken cancel)
        where T : class
    {
        try
        {
            using var response = await _http
                .PostAsJsonAsync(url, new KeyRequest(key), JsonOpts, cancel)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            return await response.Content
                .ReadFromJsonAsync<T>(JsonOpts, cancel)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A user-initiated cancel is not a server failure, and swallowing it
            // as one would leave the panel showing a spurious error.
            throw;
        }
        catch
        {
            // DNS failure, TLS failure, timeout, malformed JSON: from the user's
            // side these are all "we could not tell", and none of them says
            // anything about whether the key is good.
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private sealed record KeyRequest([property: JsonPropertyName("license_key")] string LicenseKey);

    private sealed class ValidateResponse
    {
        public bool Valid { get; set; }
        public string? Plan { get; set; }

        // Typed as a JsonElement because the server has returned both a string and
        // null here across versions, and a `string?` binding throws on anything
        // else — turning a cosmetic field into a total validation failure.
        [JsonPropertyName("expires")]
        public JsonElement ExpiresRaw { get; set; }

        public string? Expires => ExpiresRaw.ValueKind switch
        {
            JsonValueKind.String => ExpiresRaw.GetString(),
            JsonValueKind.Number => ExpiresRaw.GetRawText(),
            _ => null,
        };
    }

    private sealed class SessionCountResponse
    {
        public int? Active { get; set; }
    }
}

/// <summary>The server's verdict on a key.</summary>
public sealed record LicenseCheck
{
    public bool Valid { get; init; }
    public string Plan { get; init; } = "solo";
    public string? Expires { get; init; }
}
