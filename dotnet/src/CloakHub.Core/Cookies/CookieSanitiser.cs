using System.Text.RegularExpressions;

namespace CloakHub.Core.Cookies;

/// <summary>
/// Repairs a parsed cookie into one Chromium will actually store.
/// <para>
/// This is the part of the import that decides whether a session works. Chromium
/// applies RFC 6265bis prefix rules at write time and <b>drops a violating cookie
/// without reporting anything</b> — the row simply never appears. To the user that
/// looks identical to a wrong password: the profile opens, the site shows a login
/// page, and nothing anywhere says a cookie was rejected. Repairing the handful of
/// attributes that exports get wrong is what turns "the import did nothing" into a
/// working session.
/// </para>
/// <para>
/// The rules enforced here:
/// <list type="bullet">
///   <item><c>__Host-</c> must be host-only (no Domain), Secure, and Path=/.</item>
///   <item><c>__Secure-</c> must be Secure.</item>
///   <item><c>SameSite=None</c> is only valid together with Secure.</item>
/// </list>
/// </para>
/// </summary>
public static partial class CookieSanitiser
{
    /// <summary>
    /// Hosts that need <c>SameSite=None</c> when the export did not say.
    /// <para>
    /// These identity providers embed themselves cross-site — SSO popups, consent
    /// iframes, payment frames. Chromium treats a cookie with no SameSite attribute
    /// as Lax, which is not sent on those cross-site subrequests, so the embedded
    /// login fails while the cookie sits in the jar looking imported. Netscape files
    /// have no SameSite column at all, which is exactly how these arrive.
    /// </para>
    /// <para>
    /// A fixed list rather than "None for everything": widening the default would
    /// weaken cookies whose issuer deliberately chose Lax, and a cookie sent on
    /// cross-site requests that the real browser would have withheld is itself a
    /// detectable difference.
    /// </para>
    /// </summary>
    private static readonly Regex[] CrossSiteHosts =
    [
        GoogleRe(), YouTubeRe(), GoogleTldRe(),
        FacebookRe(), InstagramRe(), TikTokRe(),
        XRe(), TwitterRe(), LinkedInRe(),
        MicrosoftOnlineRe(), LiveRe(), MicrosoftRe(),
        PayPalRe(), AmazonRe(), DoubleClickRe(),
        RecaptchaRe(), GstaticRe(),
    ];

    /// <summary>
    /// Normalise and repair a cookie, or return null when it cannot be salvaged.
    /// </summary>
    /// <param name="cookie">The parsed cookie.</param>
    /// <param name="defaultHost">
    /// Host to attach to a cookie that carries neither domain nor URL. Callers pass
    /// the dominant host of the file being imported, so an entry that lost its
    /// domain column lands with its siblings rather than being discarded.
    /// </param>
    public static BrowserCookie? Sanitise(BrowserCookie? cookie, string defaultHost = "example.com")
    {
        if (cookie is null || string.IsNullOrEmpty(cookie.Name)) return null;

        var isHostPrefix = cookie.Name.StartsWith("__Host-", StringComparison.Ordinal);
        var isSecurePrefix = cookie.Name.StartsWith("__Secure-", StringComparison.Ordinal);

        var sourcePath = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path;
        var domain = cookie.Domain?.Trim();

        var result = new BrowserCookie
        {
            Name = cookie.Name,
            Value = cookie.Value ?? "",
            HttpOnly = cookie.HttpOnly,
            Expires = cookie.Expires,
        };

        // Both prefixes imply Secure regardless of what the file claimed.
        var secure = cookie.Secure || isSecurePrefix || isHostPrefix;
        var sameSite = cookie.SameSite;

        if (isHostPrefix)
        {
            // Host-only: expressed as a URL precisely because it must carry no Domain
            // attribute. Path is forced to "/" by the prefix rule, so the source path
            // is deliberately discarded rather than appended.
            var host = string.IsNullOrEmpty(domain) ? defaultHost : domain.TrimStart('.');
            if (string.IsNullOrEmpty(host)) host = defaultHost;
            result.Url = $"https://{host}/";
        }
        else if (!string.IsNullOrEmpty(domain))
        {
            result.Domain = domain;
            result.Path = sourcePath;
        }
        else if (!string.IsNullOrWhiteSpace(cookie.Url))
        {
            result.Url = cookie.Url;
        }
        else
        {
            // Neither domain nor URL. Synthesising one keeps the cookie instead of
            // dropping it: a header-format paste has no domain by construction, and
            // the alternative is losing the whole session.
            result.Url = $"{(secure ? "https" : "http")}://{defaultHost}{sourcePath}";
        }

        if (sameSite is null && LooksCrossSite(result.HostOnlyDomain))
            sameSite = CookieSameSite.None;

        // SameSite=None without Secure is rejected outright by Chromium. Pairing them
        // is not a preference, it is the condition for the cookie existing at all.
        if (sameSite == CookieSameSite.None) secure = true;

        result.Secure = secure;
        result.SameSite = sameSite;

        return result;
    }

    /// <summary>Whether a host is a known cross-site identity provider.</summary>
    public static bool LooksCrossSite(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var bare = host.TrimStart('.');
        return CrossSiteHosts.Any(re => re.IsMatch(bare));
    }

    /// <summary>
    /// The host to fall back on for cookies that carry no domain: the first domain
    /// seen in the same file, so an entry with a missing column lands beside its
    /// siblings rather than on a placeholder host.
    /// </summary>
    public static string FallbackHost(IEnumerable<BrowserCookie> cookies)
    {
        var withDomain = cookies.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Domain));
        return withDomain?.Domain?.TrimStart('.') ?? "example.com";
    }

    // Anchored on a dot-or-start prefix so "notgoogle.com" cannot match "google.com".
    [GeneratedRegex(@"(^|\.)google\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleRe();

    [GeneratedRegex(@"(^|\.)youtube\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeRe();

    [GeneratedRegex(@"(^|\.)google\.[a-z.]+$", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleTldRe();

    [GeneratedRegex(@"(^|\.)facebook\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex FacebookRe();

    [GeneratedRegex(@"(^|\.)instagram\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex InstagramRe();

    [GeneratedRegex(@"(^|\.)tiktok\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex TikTokRe();

    [GeneratedRegex(@"(^|\.)x\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex XRe();

    [GeneratedRegex(@"(^|\.)twitter\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex TwitterRe();

    [GeneratedRegex(@"(^|\.)linkedin\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedInRe();

    [GeneratedRegex(@"(^|\.)microsoftonline\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftOnlineRe();

    [GeneratedRegex(@"(^|\.)live\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex LiveRe();

    [GeneratedRegex(@"(^|\.)microsoft\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftRe();

    [GeneratedRegex(@"(^|\.)paypal\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex PayPalRe();

    [GeneratedRegex(@"(^|\.)amazon\.[a-z.]+$", RegexOptions.IgnoreCase)]
    private static partial Regex AmazonRe();

    [GeneratedRegex(@"(^|\.)doubleclick\.net$", RegexOptions.IgnoreCase)]
    private static partial Regex DoubleClickRe();

    [GeneratedRegex(@"(^|\.)recaptcha\.net$", RegexOptions.IgnoreCase)]
    private static partial Regex RecaptchaRe();

    [GeneratedRegex(@"(^|\.)gstatic\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex GstaticRe();
}
