using CloakHub.Core.Model;

namespace CloakHub.Core.Storage;

/// <summary>
/// Application settings on disk.
/// <para>
/// A separate file from profiles, deliberately. The two have different failure
/// consequences: losing settings costs the user a minute of re-picking a theme,
/// losing profiles costs them their work. Keeping them apart means a corrupt
/// settings file cannot quarantine the profile list along with it.
/// </para>
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings? _cached;

    public SettingsStore(string path) => _path = path;

    /// <summary>Where a corrupt settings file was moved, if that happened.</summary>
    public string? Quarantined { get; private set; }

    /// <summary>
    /// The current settings, loading on first use.
    /// <para>
    /// Always normalised before being handed out, so no consumer has to defend
    /// itself against a zoom of 0 or a port of -1. The file is plain JSON that users
    /// do hand-edit, so it cannot be assumed sane just because the app wrote it.
    /// </para>
    /// </summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (_cached is not null) return _cached;

                var loaded = JsonStore.Read(_path, new AppSettings(), out var quarantined);
                Quarantined = quarantined;
                _cached = loaded.Normalised();

                // A file that normalised to something different was out of range or
                // missing a generated value (an enabled automation API with no token).
                // Written back immediately so the on-disk copy matches what the app is
                // actually using — otherwise the settings page would show a clamped
                // value that silently reverts on next launch.
                if (_cached != loaded) SaveLocked(_cached);

                return _cached;
            }
        }
    }

    /// <summary>
    /// Apply a change and persist.
    /// <para>
    /// Takes a transform rather than a patch object because <see cref="AppSettings"/>
    /// is a record: <c>with</c> expressions give a compile-time-checked partial
    /// update, whereas a patch type would need a nullable mirror of every field and
    /// could not distinguish "set to null" from "leave alone".
    /// </para>
    /// </summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        lock (_gate)
        {
            // Read through the property so a first-use Update still loads the existing
            // file rather than silently overwriting it with defaults.
            var next = change(Current).Normalised();
            SaveLocked(next);
            _cached = next;
            return next;
        }
    }

    /// <summary>
    /// Discard the cache so the next read comes from disk.
    /// <para>
    /// For the case where the user edits the file by hand while the app is running,
    /// and for tests that need to prove a value actually reached the disk rather than
    /// just the cache.
    /// </para>
    /// </summary>
    public void Invalidate()
    {
        lock (_gate) _cached = null;
    }

    private void SaveLocked(AppSettings settings) => JsonStore.Write(_path, settings);
}
