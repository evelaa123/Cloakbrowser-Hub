using System.Text.RegularExpressions;

namespace CloakHub.Core.Cookies;

/// <summary>What a cookie payload turned out to contain.</summary>
/// <param name="Ok">Whether anything usable was found.</param>
/// <param name="Count">How many cookies parsed.</param>
/// <param name="Format">Which format was detected.</param>
/// <param name="Domains">Distinct domains present, sorted.</param>
/// <param name="AuthHints">Services the payload appears to hold a live session for.</param>
/// <param name="SuggestedName">A profile name proposed from the contents.</param>
/// <param name="Error">Why nothing was found, phrased for the user.</param>
public sealed record CookieValidation(
    bool Ok,
    int Count,
    CookieFormat Format,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> AuthHints,
    string SuggestedName,
    string? Error = null)
{
    public static CookieValidation Failed(string error) =>
        new(false, 0, CookieFormat.Unknown, [], [], "", error);
}

/// <summary>
/// Inspects a cookie payload before it is written anywhere.
/// <para>
/// Importing cookies is the one action in the app that is hard to undo and easy
/// to get wrong — the file usually came out of another tool, may be for the wrong
/// account, and the user cannot read it. Reporting what was found first (how many
/// cookies, for which domains, whose session) lets them recognise their own
/// account before it lands in a profile, rather than discovering the mistake as a
/// logged-out browser.
/// </para>
/// </summary>
public static partial class CookieValidator
{
    /// <summary>
    /// Cookie names that indicate a live login, grouped by service.
    /// <para>
    /// Used for diagnostics and the "this file holds a session for X" hint only, and
    /// never to gate an import: the list cannot be complete, and an unrecognised
    /// service is still a perfectly valid session. Refusing what it does not
    /// recognise would make the feature useless for every site not listed here.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> AuthSignatures =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Google"] = ["__Secure-1PSID", "__Secure-3PSID", "SID", "SAPISID", "SSID", "HSID", "LOGIN_INFO"],
            ["Facebook"] = ["c_user", "xs", "fr", "datr"],
            ["Instagram"] = ["sessionid", "ds_user_id"],
            ["TikTok"] = ["sessionid", "sessionid_ss", "sid_tt"],
            ["X"] = ["auth_token", "ct0"],
            ["LinkedIn"] = ["li_at", "JSESSIONID"],
            ["Amazon"] = ["session-id", "x-main", "at-main"],
            ["Reddit"] = ["reddit_session", "token_v2"],
            ["Discord"] = ["__Secure-recent_mfa", "__dcfduid", "__sdcfduid"],
            ["Microsoft"] = ["ESTSAUTH", "ESTSAUTHPERSISTENT", "MSPAuth"],
            ["eBay"] = ["s", "nonsession", "ebay"],
            ["PayPal"] = ["login_email", "LANG", "x-pp-s"],
            ["Twitch"] = ["auth-token", "persistent"],
            ["Shopify"] = ["_shopify_y", "_secure_session_id"],
        };

    /// <summary>
    /// Cookies whose absence after an import means a <i>partial</i> session — the
    /// state that produces "verify it's you" or an instant logout.
    /// <para>
    /// Worth reporting separately because a partial import is worse than a failed
    /// one: the user believes the profile is ready, opens it on the account they
    /// were trying to protect, and triggers a security check from a new IP and a
    /// fresh fingerprint. Knowing beforehand that a key cookie did not survive lets
    /// them re-export instead.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> CriticalCookies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Google"] =
            [
                "__Secure-1PSID", "__Secure-3PSID", "SID", "HSID", "SSID", "SAPISID", "APISID",
                "__Secure-1PSIDTS", "__Secure-3PSIDTS", "LOGIN_INFO",
                "__Host-1PLSID", "__Host-3PLSID", "__Host-GAPS", "LSID",
            ],
            ["Facebook"] = ["c_user", "xs", "datr"],
            ["Instagram"] = ["sessionid", "ds_user_id"],
            ["X"] = ["auth_token", "ct0"],
            ["LinkedIn"] = ["li_at"],
        };

    /// <summary>Inspect pasted or file-read cookie text.</summary>
    public static CookieValidation Validate(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return CookieValidation.Failed("The file is empty.");

        var cookies = new List<BrowserCookie>();
        var format = CookieFormat.Unknown;

        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            cookies = CookieParser.ParseJson(trimmed);
            if (cookies.Count > 0) format = CookieFormat.Json;
        }

        if (cookies.Count == 0)
        {
            cookies = CookieParser.ParseNetscape(trimmed);
            if (cookies.Count > 0) format = CookieFormat.Netscape;
        }

        if (cookies.Count == 0)
        {
            cookies = CookieParser.ParseHeader(trimmed);
            if (cookies.Count > 0) format = CookieFormat.Header;
        }

        if (cookies.Count == 0)
        {
            return CookieValidation.Failed(
                "Unrecognised format. Supported: JSON (Cookie-Editor, EditThisCookie, " +
                "Playwright), Netscape cookies.txt, or a raw Cookie: header.");
        }

        var domains = cookies
            .Select(c => c.Domain?.Trim())
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = cookies.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var authHints = DetectServices(names, domains);

        return new CookieValidation(
            Ok: true,
            Count: cookies.Count,
            Format: format,
            Domains: domains,
            AuthHints: authHints,
            SuggestedName: SuggestName(trimmed, authHints, domains));
    }

    /// <summary>Inspect a file, reporting a read failure in the same shape.</summary>
    public static CookieValidation ValidateFile(string path)
    {
        try
        {
            return Validate(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return CookieValidation.Failed($"Could not read the file: {e.Message}");
        }
    }

    /// <summary>Which known services this cookie set appears to hold a session for.</summary>
    public static List<string> DetectServices(
        IReadOnlySet<string> names,
        IReadOnlyList<string> domains)
    {
        var hits = new List<string>();

        foreach (var (service, signatures) in AuthSignatures)
        {
            var matched = signatures.Count(names.Contains);
            if (matched == 0) continue;

            // A generic name like "sessionid" or "s" exists on half the web, so one
            // match alone would claim an Instagram session for any site that happens
            // to use it. Require either a second signature name or the service's own
            // domain before naming it.
            var domainMatch = domains.Any(d =>
                d.Contains(service, StringComparison.OrdinalIgnoreCase));

            if (matched >= 2 || domainMatch) hits.Add(service);
        }

        return hits;
    }

    /// <summary>
    /// Propose a profile name from the payload.
    /// <para>
    /// An email found anywhere in the file wins: cookie sets are usually one per
    /// account, and the account is what the user is actually naming. Falling back to
    /// the service or the domain still beats "New profile" when importing twenty
    /// files in a row.
    /// </para>
    /// </summary>
    private static string SuggestName(
        string payload,
        IReadOnlyList<string> authHints,
        IReadOnlyList<string> domains)
    {
        var email = EmailRe().Match(payload);
        if (email.Success) return email.Value;

        if (authHints.Count > 0) return $"{authHints[0]} account";

        return domains.Count > 0 ? PrimaryDomain(domains) : "";
    }

    /// <summary>
    /// The most representative domain in a set: the registrable-ish base that the
    /// most entries share, so a file full of <c>.google.com</c> and
    /// <c>accounts.google.com</c> is named for Google rather than whichever
    /// subdomain happened to sort first.
    /// </summary>
    public static string PrimaryDomain(IReadOnlyList<string> domains)
    {
        if (domains.Count == 0) return "";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domains)
        {
            var parts = domain.TrimStart('.').Split('.');
            var basePart = parts.Length >= 2
                ? string.Join('.', parts[^2..])
                : parts[0];

            counts[basePart] = counts.GetValueOrDefault(basePart) + 1;
        }

        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}")]
    private static partial Regex EmailRe();
}
