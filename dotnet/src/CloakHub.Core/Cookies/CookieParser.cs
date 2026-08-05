using System.Globalization;
using System.Text.Json;

namespace CloakHub.Core.Cookies;

/// <summary>
/// Reads the cookie formats users actually arrive with.
/// <para>
/// Nobody exports cookies from a documented API — they use whichever extension
/// they installed. So the parsers are deliberately permissive about field names
/// and layout, and strict only about the things that decide whether a cookie
/// works. A file that fails to parse is a session the user cannot move, and they
/// have no way to convert it by hand.
/// </para>
/// </summary>
public static class CookieParser
{
    /// <summary>
    /// Cookies that are HttpOnly in a real browser.
    /// <para>
    /// Netscape exports routinely omit the <c>#HttpOnly_</c> prefix, so every row
    /// parses as <c>httpOnly=false</c>. Chromium sends the value either way, so
    /// restoring the flag is not what makes the login work — it is what stops the
    /// profile being trivially distinguishable, since a real browser never has
    /// <c>document.cookie</c> exposing an auth token that should be HttpOnly.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> HttpOnlyHints = new(StringComparer.Ordinal)
    {
        "SID", "HSID", "SSID", "LSID", "APISID", "SAPISID",
        "__Secure-1PSID", "__Secure-3PSID", "__Secure-1PAPISID", "__Secure-3PAPISID",
        "__Host-1PLSID", "__Host-3PLSID", "__Host-GAPS", "LOGIN_INFO",
        "xs", "c_user", "datr", "sessionid", "auth_token", "li_at", "ESTSAUTH",
    };

    /// <summary>
    /// Detect the format and parse. <paramref name="domain"/> is only consulted for
    /// header-style input, which carries no domain of its own.
    /// </summary>
    public static List<BrowserCookie> Parse(string? text, string? domain = null)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return [];

        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            var json = ParseJson(trimmed);
            if (json.Count > 0) return json;
        }

        var netscape = ParseNetscape(trimmed);
        if (netscape.Count > 0) return netscape;

        return ParseHeader(trimmed, domain);
    }

    /// <summary>
    /// Parse a JSON export: a bare array, <c>{ "cookies": [...] }</c>, or a
    /// Playwright storage state. Field aliases from the common extensions are
    /// accepted, since each spells the same concept differently.
    /// </summary>
    public static List<BrowserCookie> ParseJson(string text)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("cookies", out var nested) &&
                 nested.ValueKind == JsonValueKind.Array)
        {
            array = nested;
        }
        else
        {
            return [];
        }

        var result = new List<BrowserCookie>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = ReadString(item, "name", "Name");
            var value = ReadString(item, "value", "Value");

            // A cookie with no name cannot be addressed; one with a null value is a
            // different thing from one with an empty value, and only the former is a
            // broken record. Both are skipped rather than guessed at.
            if (name is null || value is null) continue;

            var cookie = new BrowserCookie
            {
                Name = name,
                Value = value,
                Path = ReadString(item, "path", "Path") ?? "/",
            };

            var domain = ReadString(item, "domain", "Domain", "host", "hostKey")?.Trim();
            if (!string.IsNullOrEmpty(domain)) cookie.Domain = domain;

            cookie.Expires = ReadExpiry(item) ?? -1;
            cookie.HttpOnly = ReadBool(item, "httpOnly", "HttpOnly", "httponly");
            cookie.Secure = ReadBool(item, "secure", "Secure");
            cookie.SameSite = NormaliseSameSite(ReadString(item, "sameSite", "SameSite", "samesite"));

            if (cookie.Domain is null)
            {
                var url = ReadString(item, "url", "Url");
                if (!string.IsNullOrEmpty(url)) cookie.Url = url;
            }

            result.Add(cookie);
        }

        return result;
    }

    /// <summary>
    /// Parse a Netscape <c>cookies.txt</c>:
    /// <c>domain \t flag \t path \t secure \t expiry \t name \t value</c>.
    /// <c>#</c> begins a comment, except <c>#HttpOnly_</c> which prefixes the domain.
    /// </summary>
    public static List<BrowserCookie> ParseNetscape(string text)
    {
        var result = new List<BrowserCookie>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var httpOnly = false;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                httpOnly = true;
                line = line["#HttpOnly_".Length..];
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 7)
            {
                // Some exports (and anything that has been through a text editor or a
                // chat window) use runs of spaces instead of tabs. The value is the
                // last field and may itself contain spaces, so everything past field
                // six is rejoined rather than truncated at the first space.
                var loose = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (loose.Length < 7) continue;
                parts = [.. loose.Take(6), string.Join(' ', loose.Skip(6))];
            }

            var name = parts[5].Trim();
            if (string.IsNullOrEmpty(name)) continue;

            result.Add(new BrowserCookie
            {
                Name = name,
                Value = parts[6].Trim(),
                Domain = parts[0].Trim(),
                Path = string.IsNullOrWhiteSpace(parts[2]) ? "/" : parts[2].Trim(),
                Secure = string.Equals(parts[3].Trim(), "TRUE", StringComparison.OrdinalIgnoreCase),
                HttpOnly = httpOnly || HttpOnlyHints.Contains(name),
                Expires = ToUnixSeconds(parts[4]) ?? -1,
            });
        }

        return result;
    }

    /// <summary>
    /// Parse a raw <c>Cookie:</c> header or bare <c>name=value; name2=value2</c>.
    /// The format carries no domain, so the caller supplies one.
    /// </summary>
    public static List<BrowserCookie> ParseHeader(string text, string? domain = null)
    {
        var body = text.Trim();
        if (body.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            body = body["cookie:".Length..].Trim();

        if (string.IsNullOrEmpty(body) || !body.Contains('=')) return [];

        var result = new List<BrowserCookie>();
        foreach (var pair in body.Split(';'))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;

            var name = pair[..index].Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var cookie = new BrowserCookie
            {
                Name = name,
                // Not trimmed of anything but whitespace: a cookie value may legitimately
                // contain '=' (base64 padding), so only the first '=' is a separator.
                Value = pair[(index + 1)..].Trim(),
                Path = "/",
                Expires = -1,
                HttpOnly = HttpOnlyHints.Contains(name),
            };

            if (!string.IsNullOrWhiteSpace(domain))
                cookie.Domain = domain.StartsWith('.') ? domain : $".{domain}";

            result.Add(cookie);
        }

        return result;
    }

    /// <summary>
    /// Merge cookie sets, de-duplicating on (name, domain, path). Later sets win, so
    /// a freshly imported file replaces a stale entry rather than colliding with it.
    /// </summary>
    public static List<BrowserCookie> Merge(params IEnumerable<BrowserCookie>[] sets)
    {
        var merged = new Dictionary<string, BrowserCookie>(StringComparer.Ordinal);

        foreach (var set in sets)
        {
            foreach (var cookie in set)
            {
                var key = $"{cookie.Name}\u0000{cookie.Domain ?? cookie.Url ?? ""}\u0000{cookie.Path ?? "/"}";
                merged[key] = cookie;
            }
        }

        return [.. merged.Values];
    }

    // ------------------------------------------------------------------
    // Field readers
    // ------------------------------------------------------------------

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();

                // Some exports write numeric or boolean values for fields that are
                // conceptually strings. Accepting them costs nothing and avoids
                // discarding an otherwise valid cookie.
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
            }
        }

        return null;
    }

    private static bool ReadBool(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String:
                    return string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
                case JsonValueKind.Number:
                    return value.TryGetDouble(out var d) && d != 0;
            }
        }

        return false;
    }

    private static long? ReadExpiry(JsonElement obj)
    {
        foreach (var name in (string[])["expires", "expirationDate", "expiry", "Expires"])
        {
            if (!obj.TryGetProperty(name, out var value)) continue;

            var seconds = value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetDouble(out var d) ? ToUnixSeconds(d) : null,
                JsonValueKind.String => ToUnixSeconds(value.GetString()),
                _ => null,
            };

            if (seconds is not null) return seconds;
        }

        return null;
    }

    private static long? ToUnixSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? ToUnixSeconds(d)
            : null;
    }

    private static long? ToUnixSeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return null;

        // Beyond ~10^12 the value is milliseconds (the JavaScript Date scale, which
        // several extensions export). Storing that as seconds would date the cookie
        // to the year 33000 and, more practically, defeat any expiry comparison.
        return value > 1e12 ? (long)(value / 1000) : (long)value;
    }

    /// <summary>
    /// Normalise the many spellings of SameSite. Returns null for an unrecognised
    /// value so the sanitiser can still infer one, rather than locking in a guess.
    /// </summary>
    public static CookieSameSite? NormaliseSameSite(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var normalised = raw.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "");
        return normalised switch
        {
            "strict" => CookieSameSite.Strict,
            "lax" => CookieSameSite.Lax,

            // "no_restriction" is Cookie-Editor's spelling; "unspecified" is what
            // Chromium's own devtools export writes for a cookie with no attribute.
            "none" or "norestriction" or "unspecified" => CookieSameSite.None,
            _ => null,
        };
    }
}
