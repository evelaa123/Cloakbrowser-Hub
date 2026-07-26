using System.Text.Json;
using System.Text.Json.Nodes;
using CloakHub.Core.Model;

namespace CloakHub.Core.Storage;

/// <summary>
/// The profile collection on disk, and the only thing allowed to write it.
/// <para>
/// One file for all profiles rather than a file each. Profiles are read together
/// on every launch to populate the list, they are small, and a single atomic
/// rename keeps the whole set consistent — with a file each, an interrupted
/// reorder or folder move could leave half the profiles pointing at a folder the
/// other half no longer has.
/// </para>
/// <para>
/// Every profile passes through <see cref="ProfileMigration"/> on load, so an
/// older file is upgraded in memory before anything reads it. The upgrade is
/// written back only when something actually changed, because rewriting the file
/// on every launch would burn a needless disk write and, more importantly, would
/// destroy the evidence if a load ever went wrong.
/// </para>
/// </summary>
public sealed class ProfileStore
{
    private readonly string _path;

    // All mutation is serialised. The UI can fire several commands before the first
    // finishes — a user holding Enter on "duplicate", or an automation client
    // scripting a batch create — and read-modify-write on a shared list is exactly
    // the shape that silently loses entries under concurrency.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>In-memory copy. Never handed out directly; see <see cref="List"/>.</summary>
    private List<Profile> _profiles = [];
    private List<ProfileFolder> _folders = [];
    private bool _loaded;

    public ProfileStore(string path) => _path = path;

    /// <summary>Where a corrupt file was moved on the last load, if it happened.</summary>
    public string? Quarantined { get; private set; }

    /// <summary>Notes from migration, for surfacing in the UI after a load.</summary>
    public IReadOnlyList<string> MigrationNotes { get; private set; } = [];

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <summary>
    /// A snapshot of the profiles, newest activity first.
    /// <para>
    /// Returns a copy. Handing out the live list would let a caller mutate the store
    /// without going through the gate or the save, which is how a UI ends up
    /// displaying state that was never persisted.
    /// </para>
    /// </summary>
    public IReadOnlyList<Profile> List()
    {
        EnsureLoaded();
        return [.. _profiles];
    }

    public IReadOnlyList<ProfileFolder> Folders()
    {
        EnsureLoaded();
        return [.. _folders];
    }

    public Profile? Get(string id)
    {
        EnsureLoaded();
        return _profiles.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// Load on first use.
    /// <para>
    /// Lazy rather than in the constructor so that constructing a store cannot throw
    /// or touch the disk — which keeps it usable from a DI container and from tests
    /// that never read anything.
    /// </para>
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;

        _gate.Wait();
        try
        {
            if (_loaded) return;
            LoadLocked();
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Read, migrate, and write back only if the migration changed something.
    /// <para>
    /// Read as a <c>JsonNode</c> rather than straight into records, because
    /// migration needs to tell an absent field from an explicitly null one and that
    /// distinction is gone the moment it becomes a typed object.
    /// </para>
    /// </summary>
    private void LoadLocked()
    {
        var root = JsonStore.Read<JsonObject?>(_path, null, out var quarantined);
        Quarantined = quarantined;

        _profiles = [];
        _folders = [];
        var notes = new List<string>();
        var changed = false;

        if (root is null)
        {
            MigrationNotes = notes;
            return;
        }

        if (root["profiles"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry is not JsonObject obj) continue;

                try
                {
                    var result = ProfileMigration.Migrate(obj);
                    _profiles.Add(result.Profile);
                    changed |= result.Changed;
                    notes.AddRange(result.Notes);
                }
                catch (JsonException ex)
                {
                    // Skip the one bad profile instead of failing the whole load. The
                    // alternative is that a single unreadable entry hides every other
                    // profile the user has, which is a far worse outcome than losing
                    // the one that was already broken.
                    notes.Add($"Skipped an unreadable profile: {ex.Message}");
                    changed = true;
                }
            }
        }

        if (root["folders"] is JsonArray folderArray)
        {
            foreach (var entry in folderArray)
            {
                if (entry is not JsonObject obj) continue;
                try
                {
                    var folder = obj.Deserialize<ProfileFolder>(ProfileMigration.JsonOptions);
                    if (folder is not null && !string.IsNullOrWhiteSpace(folder.Id)) _folders.Add(folder);
                }
                catch (JsonException)
                {
                    // A lost folder is cosmetic — the profiles in it fall back to the
                    // root view — so it is dropped quietly rather than reported.
                    changed = true;
                }
            }
        }

        DropOrphanedFolderIds(notes, ref changed);

        MigrationNotes = notes;

        // Persist the upgrade so the next load is clean. Guarded on `changed` so an
        // ordinary launch never rewrites the file.
        if (changed) SaveLocked();
    }

    /// <summary>
    /// Detach profiles whose folder no longer exists.
    /// <para>
    /// Otherwise they vanish from the UI: a tree view filters by folder id, so a
    /// profile pointing at a deleted folder appears under no node at all and looks
    /// deleted. Clearing the id puts it back at the root where it can be seen and
    /// refiled.
    /// </para>
    /// </summary>
    private void DropOrphanedFolderIds(List<string> notes, ref bool changed)
    {
        var ids = _folders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var orphaned = 0;

        for (var i = 0; i < _profiles.Count; i++)
        {
            var folderId = _profiles[i].FolderId;
            if (folderId is null || ids.Contains(folderId)) continue;
            _profiles[i] = _profiles[i] with { FolderId = null };
            orphaned++;
        }

        if (orphaned == 0) return;
        notes.Add($"Moved {orphaned} profile(s) to the root: their folder no longer exists.");
        changed = true;
    }

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <summary>Add a profile and persist.</summary>
    public Profile Add(Profile profile)
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var now = Timestamp();
            var stored = profile with
            {
                Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString() : profile.Id,
                Name = UniqueName(list, profile.Name, excludingId: null),
                CreatedAt = profile.CreatedAt == 0 ? now : profile.CreatedAt,
                UpdatedAt = now,
                SchemaVersion = ProfileMigration.CurrentVersion,
            };
            list.Add(stored);
            return stored;
        });
    }

    /// <summary>
    /// Replace a profile wholesale.
    /// <para>
    /// Preserves <c>CreatedAt</c> and <c>LastLaunchedAt</c> from the stored copy
    /// rather than trusting the incoming one. The editor round-trips a profile
    /// through a form, and a UI that does not bind those fields would otherwise
    /// write zeroes back over them — silently resetting a profile's apparent age,
    /// which is the one property that cannot be regenerated.
    /// </para>
    /// </summary>
    public Profile? Update(Profile profile)
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var index = list.FindIndex(p => p.Id == profile.Id);
            if (index < 0) return null;

            var existing = list[index];
            var stored = profile with
            {
                CreatedAt = existing.CreatedAt,
                LastLaunchedAt = existing.LastLaunchedAt,
                Name = UniqueName(list, profile.Name, excludingId: profile.Id),
                UpdatedAt = Timestamp(),
                SchemaVersion = ProfileMigration.CurrentVersion,
            };

            list[index] = stored;
            return stored;
        });
    }

    public bool Remove(string id)
    {
        EnsureLoaded();
        return Mutate(list => list.RemoveAll(p => p.Id == id) > 0);
    }

    /// <summary>
    /// Copy a profile, optionally with a new fingerprint identity.
    /// <para>
    /// <paramref name="newSeed"/> defaults true because the common reason to
    /// duplicate is wanting another account on the same site. Two profiles sharing a
    /// seed present the same device, which is precisely the correlation the tool
    /// exists to break — so sharing one has to be the deliberate choice, not the
    /// default.
    /// </para>
    /// </summary>
    public Profile? Duplicate(string id, bool newSeed = true)
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var source = list.FirstOrDefault(p => p.Id == id);
            if (source is null) return null;

            var now = Timestamp();
            var copy = source with
            {
                Id = Guid.NewGuid().ToString(),
                Name = UniqueName(list, source.Name, excludingId: null),
                // Cleared, not copied: a duplicate has never been launched, and
                // inheriting the original's timestamp would misreport its age.
                LastLaunchedAt = null,
                CreatedAt = now,
                UpdatedAt = now,
                Fingerprint = newSeed
                    ? source.Fingerprint with { Seed = null }
                    : source.Fingerprint,
            };

            list.Add(copy);
            return copy;
        });
    }

    /// <summary>Record a launch, for the "last used" column.</summary>
    public void MarkLaunched(string id)
    {
        EnsureLoaded();
        Mutate(list =>
        {
            var index = list.FindIndex(p => p.Id == id);
            if (index < 0) return false;
            // UpdatedAt is deliberately not touched: it means "the configuration
            // changed", and launching a profile does not change its configuration.
            list[index] = list[index] with { LastLaunchedAt = Timestamp() };
            return true;
        });
    }

    // ------------------------------------------------------------------
    // Folders
    // ------------------------------------------------------------------

    public ProfileFolder AddFolder(string name)
    {
        EnsureLoaded();
        _gate.Wait();
        try
        {
            var folder = new ProfileFolder
            {
                Id = Guid.NewGuid().ToString(),
                Name = string.IsNullOrWhiteSpace(name) ? "New folder" : name.Trim(),
                CreatedAt = Timestamp(),
                SortOrder = _folders.Count == 0 ? 0 : _folders.Max(f => f.SortOrder) + 1,
            };
            _folders.Add(folder);
            SaveLocked();
            return folder;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Rename a folder.
    /// <para>
    /// Returns false when the folder is gone rather than throwing, so a stale UI row
    /// produces a message instead of a crash.
    /// </para>
    /// </summary>
    public bool RenameFolder(string id, string name)
    {
        EnsureLoaded();
        _gate.Wait();
        try
        {
            var index = _folders.FindIndex(f => f.Id == id);
            if (index < 0) return false;

            var trimmed = string.IsNullOrWhiteSpace(name) ? "New folder" : name.Trim();
            if (_folders[index].Name == trimmed) return true;

            _folders[index] = _folders[index] with { Name = trimmed };
            SaveLocked();
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Move a profile into a folder, or to the root when <paramref name="folderId"/> is null.
    /// <para>
    /// Validates the target folder exists. Without that check a move to a folder
    /// deleted in another window would set an orphaned id, and the profile would
    /// disappear from every folder view until the next load repaired it.
    /// </para>
    /// </summary>
    public bool MoveToFolder(string profileId, string? folderId)
    {
        EnsureLoaded();
        _gate.Wait();
        try
        {
            if (folderId is not null && !_folders.Any(f => f.Id == folderId)) return false;

            var index = _profiles.FindIndex(p => p.Id == profileId);
            if (index < 0) return false;
            if (_profiles[index].FolderId == folderId) return true;

            // UpdatedAt is touched here, unlike MarkLaunched: which folder a profile
            // lives in is part of its stored configuration.
            _profiles[index] = _profiles[index] with
            {
                FolderId = folderId,
                UpdatedAt = Timestamp(),
            };

            SaveLocked();
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Number of profiles in a folder, for the sidebar counts.</summary>
    public int CountIn(string? folderId)
    {
        EnsureLoaded();
        return _profiles.Count(p => p.FolderId == folderId);
    }

    /// <summary>
    /// Delete a folder, moving its profiles to the root.
    /// <para>
    /// Never deletes the profiles inside it. Deleting a container in a file manager
    /// takes its contents, but a profile represents real work — an aged identity
    /// with cookies and history — and losing several of them to one misclick on a
    /// grouping label would be indefensible.
    /// </para>
    /// </summary>
    public bool RemoveFolder(string id)
    {
        EnsureLoaded();
        _gate.Wait();
        try
        {
            if (_folders.RemoveAll(f => f.Id == id) == 0) return false;

            for (var i = 0; i < _profiles.Count; i++)
                if (_profiles[i].FolderId == id)
                    _profiles[i] = _profiles[i] with { FolderId = null };

            SaveLocked();
            return true;
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    /// <summary>Run a mutation under the gate and persist the result.</summary>
    private T Mutate<T>(Func<List<Profile>, T> mutation)
    {
        _gate.Wait();
        try
        {
            var result = mutation(_profiles);

            // Skip the write when nothing happened — a delete of a missing id, an
            // update of a profile that is gone. Saving anyway would rewrite the file
            // for no reason and make a failed operation look like a successful one.
            if (result is bool ok && !ok) return result;
            if (result is null) return result;

            SaveLocked();
            return result;
        }
        finally { _gate.Release(); }
    }

    private void SaveLocked() => JsonStore.Write(_path, new JsonObject
    {
        // Versioned at the document level as well as per profile, so a future change
        // to the file's own layout can be detected without inspecting every entry.
        ["version"] = 1,
        ["profiles"] = new JsonArray([.. _profiles.Select(p =>
            JsonSerializer.SerializeToNode(p, ProfileMigration.JsonOptions))]),
        ["folders"] = new JsonArray([.. _folders.Select(f =>
            JsonSerializer.SerializeToNode(f, ProfileMigration.JsonOptions))]),
    });

    /// <summary>
    /// Make a name unique by appending a counter.
    /// <para>
    /// Names are not identities — the id is — so a duplicate would not break
    /// anything structurally. It breaks the human, who cannot tell two rows called
    /// "Amazon" apart when deciding which to delete.
    /// </para>
    /// </summary>
    private static string UniqueName(List<Profile> list, string name, string? excludingId)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "New profile" : name.Trim();

        var taken = list
            .Where(p => p.Id != excludingId)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(trimmed)) return trimmed;

        // Strip an existing " (n)" suffix first, so duplicating a copy gives
        // "Amazon (3)" rather than "Amazon (2) (2)".
        var stem = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s*\(\d+\)$", "");

        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{stem} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }

        // Unreachable in practice; a guid suffix beats looping forever.
        return $"{stem} ({Guid.NewGuid():N})";
    }

    private static long Timestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
