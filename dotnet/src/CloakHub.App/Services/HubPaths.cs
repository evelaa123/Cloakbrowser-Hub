using CloakHub.Core.Model;
using CloakHub.Core.Platform;

namespace CloakHub.App.Services;

/// <summary>
/// Every path the app writes to, resolved in one place.
/// <para>
/// Centralised so that "where does the Hub keep its data" has a single answer per
/// platform. Scattering <c>Path.Combine(HubDataDir(), ...)</c> through the UI is
/// how a build ends up writing settings to two different files depending on which
/// screen saved them.
/// </para>
/// </summary>
public sealed class HubPaths
{
    public HubPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? HostOs.HubDataDir();
    }

    /// <summary>Base directory: %APPDATA%\CloakBrowserHub, ~/.config/CloakBrowserHub, etc.</summary>
    public string Root { get; }

    public string ProfilesFile => Path.Combine(Root, "profiles.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LicenseFile => Path.Combine(Root, "license.key");

    /// <summary>
    /// The saved proxy library.
    /// <para>
    /// Its own file so that importing two hundred proxies never rewrites — and never
    /// risks — profiles.json, which is the irreplaceable one.
    /// </para>
    /// </summary>
    public string ProxiesFile => Path.Combine(Root, "proxies.json");

    /// <summary>Where per-profile branding assets (badged icons, .desktop files) go.</summary>
    public string BrandingDir => Path.Combine(Root, "branding");

    /// <summary>
    /// Where browser user-data directories live.
    /// <para>
    /// Honours the setting, because these grow to hundreds of megabytes each and
    /// users with a small system drive need them elsewhere. Falls back to the default
    /// when unset or blank rather than treating an empty string as a valid path.
    /// </para>
    /// </summary>
    public string ProfileDataDir(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ProfilesDir)
            ? Path.Combine(Root, "profiles")
            : settings.ProfilesDir;

    /// <summary>The user-data directory for one profile.</summary>
    public string ProfileDataDir(AppSettings settings, string profileId) =>
        Path.Combine(ProfileDataDir(settings), profileId);
}
