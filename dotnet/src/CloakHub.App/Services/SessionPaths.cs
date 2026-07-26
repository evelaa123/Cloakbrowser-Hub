using CloakHub.Core.Launch;
using CloakHub.Core.Storage;

namespace CloakHub.App.Services;

/// <summary>
/// Adapts <see cref="HubPaths"/> to what the session manager needs.
/// <para>
/// Reads the store on every call rather than caching a resolved directory: the
/// profile data location is a setting the user can change while the app is running,
/// and a cached value would keep launching new sessions into the old folder until
/// the next restart — with no indication that the setting had not taken effect.
/// </para>
/// </summary>
public sealed class SessionPaths(HubPaths paths, SettingsStore settings) : ISessionPaths
{
    public string ProfileDataDir(string profileId) =>
        paths.ProfileDataDir(settings.Current, profileId);

    public string BrandingAssetRoot => paths.BrandingDir;

    public string TempDir => Path.Combine(paths.Root, "tmp");
}
