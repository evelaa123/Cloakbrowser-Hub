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
}
