using CloakHub.Core.Model;
using CloakHub.Core.Storage;

namespace CloakHub.Core.Import;

/// <summary>
/// Turn a discovered browser profile into a Hub profile.
/// <para>
/// The orchestration lives here rather than in the view model so the whole import
/// — create, clone, roll back — can be exercised without a UI, and so the failure
/// path is one code path instead of one per screen.
/// </para>
/// </summary>
public sealed class ProfileImporter
{
    private readonly ProfileStore _profiles;
    private readonly Func<string, string> _dataDirFor;

    /// <param name="dataDirFor">Maps a profile id to its browser user-data directory.</param>
    public ProfileImporter(ProfileStore profiles, Func<string, string> dataDirFor)
    {
        _profiles = profiles;
        _dataDirFor = dataDirFor;
    }

    /// <summary>
    /// Import one discovered profile.
    /// <para>
    /// When <paramref name="cloneData"/> is false only the settings are carried
    /// across and the result is a fresh, logged-out identity — which is the right
    /// default, because a cloned profile shares its cookies <i>and</i> its
    /// established fingerprint with the original, and two browsers presenting the
    /// same identity from different IPs is precisely the correlation the Hub exists
    /// to avoid. The clone is offered because "keep me logged in" is the reason
    /// people import at all; it is just not the default.
    /// </para>
    /// </summary>
    public ImportOutcome Import(
        DiscoveredProfile source,
        bool cloneData,
        FingerprintPlatform platform,
        string? folderId = null,
        string? nameOverride = null)
    {
        var name = Clean(nameOverride) ?? UniqueName(Clean(source.Name) ?? "Imported profile");

        var profile = ProfileFactory.NewProfile(name, platform, folderId);

        // The locale the original browser actually sent is a real part of the
        // identity being imported: a site that has seen this account with en-GB
        // will notice it becoming en-US. Pinned to Manual rather than left on Ip,
        // because Ip mode would recompute it from the proxy and discard the very
        // value that was just read.
        if (!string.IsNullOrWhiteSpace(source.Locale))
        {
            profile = profile with
            {
                Locale = new LocaleConfig { Mode = LocaleMode.Manual, Locale = source.Locale.Trim() },
            };
        }

        var notes = new List<string>
        {
            $"Imported from {source.Browser}: {source.Path}",
        };

        CloneResult? clone = null;
        if (cloneData)
        {
            // TargetFor, not the raw data dir. _dataDirFor returns the profile's
            // --user-data-dir; Chromium reads its actual profile from the Default
            // subdirectory beneath it. Cloning into the root put every cookie one
            // level above where the browser looks, so the import copied hundreds
            // of megabytes, reported success, and produced a logged-out profile.
            clone = ProfileCloner.Clone(source.Path, ProfileCloner.TargetFor(_dataDirFor(profile.Id)));
            if (!clone.Ok)
            {
                // Nothing is written to the store on a failed clone. Leaving an
                // empty profile behind would look like a partial success and the
                // user would have to work out for themselves that it is logged out.
                return ImportOutcome.Failed(clone.Error ?? "The profile data could not be copied.");
            }

            notes.Add($"Cloned {clone.Copied.Count} items ({clone.MegaBytes} MB) from the source profile.");
            if (clone.Skipped.Count > 0)
                notes.Add($"Skipped: {string.Join(", ", clone.Skipped)}");
        }

        profile = profile with { Notes = string.Join(Environment.NewLine, notes) };

        var saved = _profiles.Add(profile);

        return new ImportOutcome
        {
            Ok = true,
            Profile = saved,
            Copy = clone,
        };
    }

    /// <summary>Import several at once, reporting each result rather than stopping at the first failure.</summary>
    public IReadOnlyList<ImportOutcome> ImportAll(
        IEnumerable<DiscoveredProfile> sources,
        bool cloneData,
        FingerprintPlatform platform,
        string? folderId = null)
    {
        // A batch where profile three is locked must still import one, two, four
        // and five: making the user retry the whole set to skip one open browser
        // is how a fifty-profile migration becomes unusable.
        return [.. sources.Select(s => Import(s, cloneData, platform, folderId))];
    }

    /// <summary>
    /// A name not already taken, so a second import of the same browser profile is
    /// distinguishable from the first in the list.
    /// </summary>
    private string UniqueName(string wanted)
    {
        var taken = _profiles.List()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        if (!taken.Contains(wanted)) return wanted;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{wanted} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"{wanted} ({Guid.NewGuid().ToString()[..8]})";
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>The result of importing one profile.</summary>
public sealed record ImportOutcome
{
    public bool Ok { get; init; }
    public Profile? Profile { get; init; }

    /// <summary>
    /// What the data clone did, or null when only settings were imported.
    /// <para>
    /// Named <c>Copy</c> rather than <c>Clone</c> because C# reserves that member
    /// name on records for the compiler-generated copy constructor.
    /// </para>
    /// </summary>
    public CloneResult? Copy { get; init; }
    public string? Error { get; init; }

    public static ImportOutcome Failed(string error) => new() { Ok = false, Error = error };
}
