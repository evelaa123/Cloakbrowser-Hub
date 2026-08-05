using CloakHub.Core.Network;

namespace CloakHub.Core.Tests;

/// <summary>
/// Tests for <see cref="GeoLocale"/>, the lookup behind "Follow proxy IP".
/// <para>
/// The mode was inert before this type existed: the launch request carried a
/// <c>GeoIp</c> flag that nothing read, so a profile set to follow its exit IP
/// launched with no locale and no timezone and Chromium used the host's own. A
/// user on a Vienna VPN presented an Austrian IP with their machine's language —
/// the precise mismatch the setting exists to remove.
/// </para>
/// </summary>
public class GeoLocaleTests
{
    // ------------------------------------------------------------------
    // The reported case.
    // ------------------------------------------------------------------

    [Fact]
    public void An_austrian_exit_ip_produces_the_austrian_locale()
    {
        // de-AT, not de-DE and not the host default. A Vienna VPN exit that
        // reports German-for-Germany is still a mismatch, just a subtler one.
        Assert.Equal("de-AT", GeoLocale.ForCountry("AT"));
    }

    [Theory]
    [InlineData("US", "en-US")]
    [InlineData("GB", "en-GB")]
    [InlineData("DE", "de-DE")]
    [InlineData("AT", "de-AT")]
    [InlineData("CH", "de-CH")]
    [InlineData("FR", "fr-FR")]
    [InlineData("BR", "pt-BR")]
    [InlineData("PT", "pt-PT")]
    [InlineData("JP", "ja-JP")]
    [InlineData("UA", "uk-UA")]
    public void Maps_countries_to_the_locale_a_browser_there_would_send(string cc, string expected)
    {
        Assert.Equal(expected, GeoLocale.ForCountry(cc));
    }

    [Fact]
    public void Country_codes_are_matched_regardless_of_case_or_padding()
    {
        // Providers disagree on casing, and one pads the field.
        Assert.Equal("de-AT", GeoLocale.ForCountry("at"));
        Assert.Equal("de-AT", GeoLocale.ForCountry(" AT "));
    }

    [Fact]
    public void An_unmapped_country_yields_null_rather_than_a_guess()
    {
        // Falling back to en-US would assert a language the visitor probably does
        // not speak — a worse mismatch than leaving the browser default alone.
        Assert.Null(GeoLocale.ForCountry("ZZ"));
        Assert.Null(GeoLocale.ForCountry(""));
        Assert.Null(GeoLocale.ForCountry(null));
    }

    // ------------------------------------------------------------------
    // Accept-Language shape.
    // ------------------------------------------------------------------

    [Fact]
    public void Non_english_locales_fall_back_through_the_bare_language_then_english()
    {
        // What a real de-AT browser sends. A single-entry header is itself a
        // signature, because no shipping browser produces one by default.
        Assert.Equal("de-AT,de;q=0.9,en;q=0.8", GeoLocale.AcceptLanguage("de-AT"));
    }

    [Fact]
    public void English_locales_do_not_list_english_twice()
    {
        Assert.Equal("en-GB,en;q=0.9", GeoLocale.AcceptLanguage("en-GB"));
    }

    [Fact]
    public void A_bare_language_tag_is_handled_without_inventing_a_region()
    {
        Assert.Equal("de,de;q=0.9,en;q=0.8", GeoLocale.AcceptLanguage("de"));
    }

    [Fact]
    public void An_empty_locale_falls_back_to_a_plausible_default()
    {
        Assert.Equal("en-US,en;q=0.9", GeoLocale.AcceptLanguage("   "));
    }

    // ------------------------------------------------------------------
    // Resolve: manual always wins.
    // ------------------------------------------------------------------

    [Fact]
    public void A_pinned_locale_is_never_overridden_by_the_lookup()
    {
        // Manual mode means the user typed it. Second-guessing that would make
        // the field unusable for the people who most need it.
        var r = GeoLocale.Resolve("fr-FR", "Europe/Paris", "AT", "Europe/Vienna");

        Assert.Equal("fr-FR", r.Locale);
        Assert.Equal("Europe/Paris", r.Timezone);
    }

    [Fact]
    public void Geo_values_fill_only_what_was_left_blank()
    {
        // A user who pinned a timezone but not a language gets both, coherently.
        var r = GeoLocale.Resolve(null, "Europe/Vienna", "AT", "Europe/Berlin");

        Assert.Equal("de-AT", r.Locale);
        Assert.Equal("Europe/Vienna", r.Timezone);
    }

    [Fact]
    public void Geo_supplies_both_when_nothing_is_pinned()
    {
        var r = GeoLocale.Resolve(null, null, "AT", "Europe/Vienna");

        Assert.Equal("de-AT", r.Locale);
        Assert.Equal("Europe/Vienna", r.Timezone);
    }

    [Fact]
    public void A_failed_lookup_leaves_both_unset_rather_than_guessing()
    {
        // Null here means the launcher emits no language flags at all, which is
        // the old behaviour — correct as a fallback, wrong as the only path.
        var r = GeoLocale.Resolve(null, null, null, null);

        Assert.Null(r.Locale);
        Assert.Null(r.Timezone);
    }

    [Fact]
    public void Whitespace_pins_are_treated_as_unset()
    {
        // An empty text box must not beat a real lookup.
        var r = GeoLocale.Resolve("  ", "\t", "AT", "Europe/Vienna");

        Assert.Equal("de-AT", r.Locale);
        Assert.Equal("Europe/Vienna", r.Timezone);
    }

    [Fact]
    public void An_unmapped_country_still_passes_the_timezone_through()
    {
        // The two are independent: not knowing the language is no reason to also
        // discard a timezone the provider did return.
        var r = GeoLocale.Resolve(null, null, "ZZ", "Antarctica/Troll");

        Assert.Null(r.Locale);
        Assert.Equal("Antarctica/Troll", r.Timezone);
    }
}
