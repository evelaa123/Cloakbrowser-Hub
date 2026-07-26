using CloakHub.Core.Model;

namespace CloakHub.App.ViewModels;

/// <summary>
/// How enums are spelled on screen.
/// <para>
/// Exists because <c>Enum.ToString()</c> leaks C# naming rules into the interface:
/// <c>FingerprintPlatform.Macos</c> renders as "Macos", which is simply the wrong
/// name for the product. Centralised so the profiles table and the settings combo
/// boxes cannot disagree about what to call the same value.
/// </para>
/// </summary>
public static class DisplayNames
{
    public static string Of(FingerprintPlatform platform) => platform switch
    {
        FingerprintPlatform.Windows => "Windows",
        FingerprintPlatform.Macos => "macOS",
        FingerprintPlatform.Linux => "Linux",
        _ => platform.ToString(),
    };

    public static string Of(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "Dark",
        AppTheme.Light => "Light",
        _ => theme.ToString(),
    };

    public static string Of(ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Stable => "Stable",
        ReleaseChannel.Preview => "Preview",
        _ => channel.ToString(),
    };

    /// <summary>
    /// The three-state value switch.
    /// <para>
    /// Spelled out rather than abbreviated, because the difference between "pass the
    /// real value through" and "let the seed derive one" is the whole decision the
    /// control exists for, and "Real / Auto / Manual" alone does not convey it to
    /// someone meeting the concept for the first time.
    /// </para>
    /// </summary>
    public static string Of(ValueMode mode) => mode switch
    {
        ValueMode.Real => "Real (pass through)",
        ValueMode.Auto => "Auto (from seed)",
        ValueMode.Manual => "Manual",
        _ => mode.ToString(),
    };

    public static string Of(NoiseMode mode) => mode switch
    {
        NoiseMode.Off => "Off",
        NoiseMode.Real => "Real",
        NoiseMode.Noise => "Noise",
        _ => mode.ToString(),
    };

    public static string Of(ProxyKind kind) => kind switch
    {
        ProxyKind.None => "No proxy",
        ProxyKind.Http => "HTTP",
        ProxyKind.Https => "HTTPS",
        ProxyKind.Socks5 => "SOCKS5",
        _ => kind.ToString(),
    };

    public static string Of(LocaleMode mode) => mode switch
    {
        LocaleMode.Ip => "Follow proxy IP",
        LocaleMode.Manual => "Manual",
        _ => mode.ToString(),
    };

    public static string Of(GeoMode mode) => mode switch
    {
        GeoMode.Off => "Off (leave untouched)",
        GeoMode.Ip => "Follow proxy IP",
        GeoMode.Manual => "Manual coordinates",
        _ => mode.ToString(),
    };

    public static string Of(WebRtcMode mode) => mode switch
    {
        WebRtcMode.Off => "Off",
        WebRtcMode.Real => "Real",
        WebRtcMode.Auto => "Auto (from proxy)",
        WebRtcMode.Manual => "Manual",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Workflow status labels.
    /// <para>
    /// <c>None</c> reads as "No status" rather than an empty string, so an unset value
    /// is visibly a choice instead of looking like a rendering fault.
    /// </para>
    /// </summary>
    public static string Of(ProfileStatus status) => status switch
    {
        ProfileStatus.None => "No status",
        ProfileStatus.New => "New",
        ProfileStatus.Warming => "Warming up",
        ProfileStatus.Ready => "Ready",
        ProfileStatus.Working => "In use",
        ProfileStatus.Paused => "Paused",
        ProfileStatus.Banned => "Banned",
        ProfileStatus.Retired => "Retired",
        _ => status.ToString(),
    };

    public static string Of(ProfileKind kind) => kind switch
    {
        ProfileKind.None => "Uncategorised",
        ProfileKind.Facebook => "Facebook",
        ProfileKind.Google => "Google",
        ProfileKind.TikTok => "TikTok",
        ProfileKind.Crypto => "Crypto",
        ProfileKind.Shopping => "Shopping",
        ProfileKind.Ads => "Ads",
        ProfileKind.Other => "Other",
        _ => kind.ToString(),
    };

    public static string Of(HumanPresetKind preset) => preset switch
    {
        HumanPresetKind.Default => "Default",
        HumanPresetKind.Careful => "Careful (slower)",
        _ => preset.ToString(),
    };
}
