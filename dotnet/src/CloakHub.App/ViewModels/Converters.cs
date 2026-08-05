using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CloakHub.Core.Model;

namespace CloakHub.App.ViewModels;

/// <summary>
/// The few value converters the XAML needs.
/// <para>
/// Kept to a minimum on purpose: most of what a converter would do here is better
/// expressed as a boolean property on the view model, where it is named, testable and
/// visible to anyone reading the class. These exist only for comparisons Avalonia's
/// binding syntax cannot express — it has <c>!</c> for negation but no operators.
/// </para>
/// </summary>
public static class Converters
{
    /// <summary>True when the bound number is greater than zero.</summary>
    public static readonly IValueConverter GreaterThanZero =
        new FuncValueConverter<int, bool>(count => count > 0);

    /// <summary>
    /// Formats a zoom factor as a percentage.
    /// <para>
    /// Needed because the combo box binds to the raw <c>double</c> — the value that
    /// gets stored — while 1.25 has to read as "125%" on screen.
    /// </para>
    /// </summary>
    public static readonly IValueConverter ZoomPercent =
        new FuncValueConverter<double, string>(z => $"{(int)Math.Round(z * 100)}%");

    // Enum labels for the settings combo boxes. Bound to the enum value itself, so
    // SelectedItem still round-trips the real type; only the text differs.
    public static readonly IValueConverter PlatformName =
        new FuncValueConverter<FingerprintPlatform, string>(DisplayNames.Of);

    public static readonly IValueConverter ThemeName =
        new FuncValueConverter<AppTheme, string>(DisplayNames.Of);

    public static readonly IValueConverter ChannelName =
        new FuncValueConverter<ReleaseChannel, string>(DisplayNames.Of);

    // Labels for the profile editor's combo boxes. Same pattern as above: the box
    // binds the enum value, only the displayed text is converted.
    public static readonly IValueConverter ValueModeName =
        new FuncValueConverter<ValueMode, string>(DisplayNames.Of);

    public static readonly IValueConverter NoiseModeName =
        new FuncValueConverter<NoiseMode, string>(DisplayNames.Of);

    public static readonly IValueConverter ProxyKindName =
        new FuncValueConverter<ProxyKind, string>(DisplayNames.Of);

    public static readonly IValueConverter LocaleModeName =
        new FuncValueConverter<LocaleMode, string>(DisplayNames.Of);

    public static readonly IValueConverter GeoModeName =
        new FuncValueConverter<GeoMode, string>(DisplayNames.Of);

    public static readonly IValueConverter WebRtcModeName =
        new FuncValueConverter<WebRtcMode, string>(DisplayNames.Of);

    public static readonly IValueConverter BrandName =
        new FuncValueConverter<BrowserBrand, string>(b => b.ToString());

    public static readonly IValueConverter StatusName =
        new FuncValueConverter<ProfileStatus, string>(DisplayNames.Of);

    public static readonly IValueConverter KindName =
        new FuncValueConverter<ProfileKind, string>(DisplayNames.Of);

    public static readonly IValueConverter HumanPresetName =
        new FuncValueConverter<HumanPresetKind, string>(DisplayNames.Of);

    /// <summary>True when the bound string is non-empty, for collapsing hint rows.</summary>
    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrWhiteSpace(s));
}
