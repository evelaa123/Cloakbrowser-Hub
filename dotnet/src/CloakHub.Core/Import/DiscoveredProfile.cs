namespace CloakHub.Core.Import;

/// <summary>Which family a discovered profile belongs to, and therefore how it is read.</summary>
public enum ProfileFamily
{
    /// <summary>Chrome, Edge, Brave, Opera, Vivaldi, Yandex — a <c>Preferences</c> file and a SQLite cookie DB.</summary>
    Chromium,

    /// <summary>Firefox — <c>prefs.js</c> and <c>cookies.sqlite</c>.</summary>
    Firefox,
}

/// <summary>
/// One browser profile found on disk, in a form the picker can show and the
/// importer can act on.
/// <para>
/// <see cref="SizeMb"/> is nullable rather than zero-by-default because "not
/// measured" and "empty" are different answers and the UI has to say so: the
/// scanner deliberately skips sizing when it walked a folder the user picked,
/// where a size probe on a network drive would be the slowest part of the scan.
/// </para>
/// </summary>
public sealed record DiscoveredProfile
{
    /// <summary>Browser name for display, e.g. "Brave". Best-effort — never used to gate the import.</summary>
    public required string Browser { get; init; }

    /// <summary>Display label, e.g. "Person 1 (me@example.com) — Chrome".</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path of the profile directory itself, not its user-data parent.</summary>
    public required string Path { get; init; }

    public ProfileFamily Family { get; init; } = ProfileFamily.Chromium;

    /// <summary>
    /// A cookie database was found.
    /// <para>
    /// Distinct from "cookies can be read": Chromium encrypts values with an
    /// OS-held key, so the Hub reports presence and lets the clone path carry the
    /// encrypted bytes across rather than promising a decryption it cannot do on
    /// every platform.
    /// </para>
    /// </summary>
    public bool HasCookies { get; init; }

    /// <summary>Approximate size, or null when it was not measured.</summary>
    public double? SizeMb { get; init; }

    /// <summary>Locale hint read from the profile's own settings, when available.</summary>
    public string? Locale { get; init; }
}
