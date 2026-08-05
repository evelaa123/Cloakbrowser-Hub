using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloakHub.Core.Cookies;

/// <summary>
/// Serialises cookies back out to the formats other tools read.
/// <para>
/// Export is what stops the profile store being a one-way door. A user moving to
/// another machine, taking a backup, or handing an account to a colleague needs
/// the session out in a shape something else understands, and the two formats
/// here are what every other tool accepts.
/// </para>
/// </summary>
public static class CookieWriter
{
    /// <summary>
    /// Serialise to the JSON layout Cookie-Editor and EditThisCookie import.
    /// <para>
    /// Field names follow those extensions rather than Playwright: this file exists
    /// to be re-imported somewhere else, and the extensions are what users have.
    /// <c>expirationDate</c> is emitted for persistent cookies only — writing it as
    /// 0 or -1 makes some importers treat the cookie as already expired and drop it.
    /// </para>
    /// </summary>
    public static string ToJson(IEnumerable<BrowserCookie> cookies)
    {
        var array = new JsonArray();

        foreach (var cookie in cookies)
        {
            var node = new JsonObject
            {
                ["name"] = cookie.Name,
                ["value"] = cookie.Value,
                ["domain"] = cookie.Domain ?? cookie.HostOnlyDomain,
                ["path"] = cookie.Path ?? "/",
                ["secure"] = cookie.Secure,
                ["httpOnly"] = cookie.HttpOnly,
                ["hostOnly"] = string.IsNullOrEmpty(cookie.Domain),
                ["session"] = cookie.IsSession,
            };

            if (!cookie.IsSession) node["expirationDate"] = cookie.Expires;

            if (cookie.SameSite is not null)
            {
                // "no_restriction" rather than "none": Cookie-Editor writes and expects
                // that spelling, and its importer ignores values it does not know,
                // which would quietly downgrade the cookie to Lax.
                node["sameSite"] = cookie.SameSite switch
                {
                    CookieSameSite.None => "no_restriction",
                    CookieSameSite.Lax => "lax",
                    CookieSameSite.Strict => "strict",
                    _ => null,
                };
            }

            array.Add(node);
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Serialise to Netscape <c>cookies.txt</c>, as curl and wget read it.
    /// </summary>
    public static string ToNetscape(IEnumerable<BrowserCookie> cookies)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Netscape HTTP Cookie File");
        builder.AppendLine("# Exported by CloakBrowser Hub");
        builder.AppendLine();

        foreach (var cookie in cookies)
        {
            var domain = cookie.Domain ?? cookie.HostOnlyDomain;
            if (string.IsNullOrEmpty(domain)) continue;

            // The flag column means "send to subdomains too", which in this format is
            // encoded by the leading dot on the domain.
            var includeSubdomains = domain.StartsWith('.') ? "TRUE" : "FALSE";

            var line = string.Join('\t',
                domain,
                includeSubdomains,
                cookie.Path ?? "/",
                cookie.Secure ? "TRUE" : "FALSE",
                cookie.IsSession ? "0" : cookie.Expires.ToString(),
                cookie.Name,
                cookie.Value);

            // The de-facto convention for HttpOnly in this format. Readers that do not
            // understand it skip the line as a comment, which loses the cookie — but
            // writing it without the marker would misrepresent an HttpOnly cookie as
            // script-readable, and the readers that matter here all support it.
            builder.AppendLine(cookie.HttpOnly ? $"#HttpOnly_{line}" : line);
        }

        return builder.ToString();
    }
}
