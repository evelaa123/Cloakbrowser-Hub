namespace CloakHub.Core.Cookies;

/// <summary>
/// One cookie, in the shape every import format is normalised into.
/// <para>
/// A mutable class rather than a record: the parsers build a cookie field by
/// field as the columns of a Netscape line or the keys of a JSON object arrive,
/// and <see cref="CookieSanitiser"/> then repairs several fields together. A
/// record would mean a <c>with</c> expression per field and a new allocation for
/// each, for a type that never escapes the import pipeline.
/// </para>
/// </summary>
public sealed class BrowserCookie
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    /// <summary>
    /// Cookie domain, possibly leading-dot ("<c>.example.com</c>") for a
    /// subdomain-inclusive cookie.
    /// </summary>
    public string? Domain { get; set; }

    public string? Path { get; set; }

    /// <summary>
    /// Expiry in Unix <b>seconds</b>, or <c>-1</c> for a session cookie.
    /// <para>
    /// Seconds rather than <see cref="DateTimeOffset"/> because that is what every
    /// export format carries, and converting on the way in would mean converting
    /// back on the way out — two lossy round trips through a value the Chromium
    /// database wants as an integer anyway.
    /// </para>
    /// </summary>
    public long Expires { get; set; } = -1;

    public bool HttpOnly { get; set; }

    public bool Secure { get; set; }

    /// <summary>
    /// <c>Strict</c>, <c>Lax</c> or <c>None</c>; null when the source did not say.
    /// <para>
    /// Null is meaningfully different from <c>Lax</c>: Netscape files have no
    /// SameSite column at all, and <see cref="CookieSanitiser"/> uses the absence
    /// to decide whether it may infer <c>None</c> for a known cross-site host.
    /// Defaulting to Lax on the way in would erase the distinction and silently
    /// break embedded SSO logins.
    /// </para>
    /// </summary>
    public CookieSameSite? SameSite { get; set; }

    /// <summary>
    /// Origin URL, used instead of <see cref="Domain"/> for host-only cookies.
    /// <para>
    /// A <c>__Host-</c> cookie is defined by the <i>absence</i> of a Domain
    /// attribute, so it cannot be expressed as a domain string. Carrying the URL
    /// is how that distinction survives a round trip.
    /// </para>
    /// </summary>
    public string? Url { get; set; }

    /// <summary>True when this cookie has no expiry and dies with the browser.</summary>
    public bool IsSession => Expires <= 0;

    /// <summary>
    /// The host this cookie belongs to, with any leading dot removed, taken from
    /// <see cref="Domain"/> or falling back to the host of <see cref="Url"/>.
    /// </summary>
    public string HostOnlyDomain
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Domain))
                return Domain.TrimStart('.');

            if (!string.IsNullOrWhiteSpace(Url) &&
                Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return uri.Host;

            return "";
        }
    }

    public BrowserCookie Clone() => new()
    {
        Name = Name,
        Value = Value,
        Domain = Domain,
        Path = Path,
        Expires = Expires,
        HttpOnly = HttpOnly,
        Secure = Secure,
        SameSite = SameSite,
        Url = Url,
    };
}

/// <summary>SameSite attribute values, as spelled in cookie exports.</summary>
public enum CookieSameSite
{
    Strict,
    Lax,
    None,
}

/// <summary>Which format an imported payload turned out to be.</summary>
public enum CookieFormat
{
    Unknown,

    /// <summary>Cookie-Editor, EditThisCookie, Playwright storage state, Puppeteer.</summary>
    Json,

    /// <summary>Netscape <c>cookies.txt</c>, as written by curl, wget and extensions.</summary>
    Netscape,

    /// <summary>A raw <c>Cookie:</c> request header, or bare <c>a=1; b=2</c> pairs.</summary>
    Header,
}
