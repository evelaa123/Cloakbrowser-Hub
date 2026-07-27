using System.Globalization;

namespace CloakHub.Core.Network;

/// <summary>
/// Turns an exit IP's country into the locale a browser there would report.
/// <para>
/// This is the missing half of the profile's "Follow proxy IP" locale mode. The
/// mode was recorded on the launch request as <c>GeoIp</c> and then never read by
/// anything, so a profile set to follow its exit IP launched with no locale flags
/// at all and Chromium fell back to the host's own language. A user behind a
/// Vienna VPN got an Austrian IP and a machine-default locale — the exact
/// mismatch the setting exists to prevent, and one a site can test for in a
/// single line of JavaScript.
/// </para>
/// <para>
/// The lookup is a static table rather than a service call. The country is
/// already resolved by <see cref="ProxyChecker"/> as part of the check the user
/// runs anyway, so mapping it here costs nothing at launch, works offline, and
/// cannot add a network round-trip to the critical path of starting a browser.
/// </para>
/// </summary>
public static class GeoLocale
{
    /// <summary>
    /// Primary language tag per ISO-3166 alpha-2 country.
    /// <para>
    /// One entry per country, chosen as the language the largest share of web
    /// traffic from that country actually sends. Multilingual countries are a
    /// judgement call — <c>CH</c> is de-CH because German-speaking Switzerland is
    /// the plurality, <c>CA</c> is en-CA for the same reason — and a user who
    /// needs the other one pins it manually, which is what Manual mode is for.
    /// </para>
    /// <para>
    /// Regional tags (<c>de-AT</c>, not <c>de</c>) because that is what a real
    /// browser in that country sends. A bare <c>de</c> from an Austrian IP is
    /// itself mildly unusual, which would trade one mismatch for another.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> ByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        // Europe — German-speaking. AT is the case that prompted this file.
        ["AT"] = "de-AT", ["DE"] = "de-DE", ["CH"] = "de-CH", ["LI"] = "de-LI",

        // Europe — rest
        ["GB"] = "en-GB", ["IE"] = "en-IE", ["FR"] = "fr-FR", ["BE"] = "nl-BE",
        ["NL"] = "nl-NL", ["LU"] = "fr-LU", ["ES"] = "es-ES", ["PT"] = "pt-PT",
        ["IT"] = "it-IT", ["GR"] = "el-GR", ["PL"] = "pl-PL", ["CZ"] = "cs-CZ",
        ["SK"] = "sk-SK", ["HU"] = "hu-HU", ["RO"] = "ro-RO", ["BG"] = "bg-BG",
        ["HR"] = "hr-HR", ["SI"] = "sl-SI", ["RS"] = "sr-RS", ["BA"] = "bs-BA",
        ["MK"] = "mk-MK", ["AL"] = "sq-AL", ["ME"] = "sr-ME", ["SE"] = "sv-SE",
        ["NO"] = "nb-NO", ["DK"] = "da-DK", ["FI"] = "fi-FI", ["IS"] = "is-IS",
        ["EE"] = "et-EE", ["LV"] = "lv-LV", ["LT"] = "lt-LT", ["UA"] = "uk-UA",
        ["BY"] = "be-BY", ["RU"] = "ru-RU", ["MD"] = "ro-MD", ["CY"] = "el-CY",
        ["MT"] = "mt-MT", ["TR"] = "tr-TR",

        // Americas
        ["US"] = "en-US", ["CA"] = "en-CA", ["MX"] = "es-MX", ["BR"] = "pt-BR",
        ["AR"] = "es-AR", ["CL"] = "es-CL", ["CO"] = "es-CO", ["PE"] = "es-PE",
        ["VE"] = "es-VE", ["EC"] = "es-EC", ["UY"] = "es-UY", ["PY"] = "es-PY",
        ["BO"] = "es-BO", ["CR"] = "es-CR", ["PA"] = "es-PA", ["DO"] = "es-DO",
        ["GT"] = "es-GT", ["CU"] = "es-CU", ["PR"] = "es-PR",

        // Asia-Pacific
        ["CN"] = "zh-CN", ["HK"] = "zh-HK", ["TW"] = "zh-TW", ["JP"] = "ja-JP",
        ["KR"] = "ko-KR", ["IN"] = "en-IN", ["PK"] = "en-PK", ["BD"] = "bn-BD",
        ["ID"] = "id-ID", ["MY"] = "ms-MY", ["SG"] = "en-SG", ["TH"] = "th-TH",
        ["VN"] = "vi-VN", ["PH"] = "en-PH", ["AU"] = "en-AU", ["NZ"] = "en-NZ",
        ["KZ"] = "ru-KZ", ["UZ"] = "uz-UZ", ["GE"] = "ka-GE", ["AM"] = "hy-AM",
        ["AZ"] = "az-AZ",

        // Middle East & Africa
        ["IL"] = "he-IL", ["AE"] = "ar-AE", ["SA"] = "ar-SA", ["QA"] = "ar-QA",
        ["KW"] = "ar-KW", ["BH"] = "ar-BH", ["OM"] = "ar-OM", ["JO"] = "ar-JO",
        ["LB"] = "ar-LB", ["IQ"] = "ar-IQ", ["IR"] = "fa-IR", ["EG"] = "ar-EG",
        ["MA"] = "ar-MA", ["DZ"] = "ar-DZ", ["TN"] = "ar-TN", ["LY"] = "ar-LY",
        ["ZA"] = "en-ZA", ["NG"] = "en-NG", ["KE"] = "en-KE", ["GH"] = "en-GH",
        ["ET"] = "am-ET", ["TZ"] = "sw-TZ", ["UG"] = "en-UG",
    };

    /// <summary>
    /// The locale for a country code, or null when the country is unknown.
    /// <para>
    /// Null rather than a guess. Falling back to <c>en-US</c> for an unmapped
    /// country would state a language the visitor probably does not speak, which
    /// is a worse mismatch than leaving the browser's own default in place.
    /// </para>
    /// </summary>
    public static string? ForCountry(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return null;
        return ByCountry.TryGetValue(countryCode.Trim(), out var locale) ? locale : null;
    }

    /// <summary>
    /// The <c>Accept-Language</c> header value for a locale.
    /// <para>
    /// Real browsers send the regional tag, then the bare language, and — for
    /// non-English locales — English as a final fallback, each with a descending
    /// q-value. Sending only <c>de-AT</c> is a recognisable signature, because
    /// no shipping browser produces a single-entry header by default.
    /// </para>
    /// </summary>
    public static string AcceptLanguage(string locale)
    {
        var tag = locale.Trim();
        if (tag.Length == 0) return "en-US,en;q=0.9";

        var dash = tag.IndexOf('-');
        var bare = dash > 0 ? tag[..dash] : tag;

        if (bare.Equals("en", StringComparison.OrdinalIgnoreCase))
            return string.Create(CultureInfo.InvariantCulture, $"{tag},{bare};q=0.9");

        return string.Create(CultureInfo.InvariantCulture, $"{tag},{bare};q=0.9,en;q=0.8");
    }

    /// <summary>
    /// Resolve the locale and timezone a session should launch with.
    /// <para>
    /// <paramref name="pinnedLocale"/> and <paramref name="pinnedTimezone"/> come
    /// from Manual mode and always win: a value the user typed is never
    /// second-guessed by a lookup. The geo values fill only what was left blank,
    /// which is what makes "Follow proxy IP" a default rather than an override.
    /// </para>
    /// </summary>
    public static Resolved Resolve(
        string? pinnedLocale,
        string? pinnedTimezone,
        string? geoCountryCode,
        string? geoTimezone)
    {
        var locale = Blank(pinnedLocale) ?? ForCountry(geoCountryCode);
        var timezone = Blank(pinnedTimezone) ?? Blank(geoTimezone);

        return new Resolved(locale, timezone);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The locale and timezone a launch should use, either may be null.</summary>
    public sealed record Resolved(string? Locale, string? Timezone);
}
