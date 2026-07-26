using CloakHub.Core.Model;
using CloakHub.Core.Storage;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// The profile collection: persistence, migration on load, and the invariants
/// that protect a user's work.
/// </summary>
public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ProfileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cloakhub-profiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "profiles.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ProfileStore Store() => new(_path);

    private static Profile New(string name = "Test") => new() { Name = name };

    // ---------------------------------------------------------------------
    // Basics
    // ---------------------------------------------------------------------

    [Fact]
    public void An_empty_store_starts_empty()
    {
        Assert.Empty(Store().List());
    }

    [Fact]
    public void An_added_profile_survives_a_reload()
    {
        var added = Store().Add(New("Amazon"));

        // A fresh instance, so this proves the disk write rather than the cache.
        var reloaded = Store().List();
        Assert.Single(reloaded);
        Assert.Equal("Amazon", reloaded[0].Name);
        Assert.Equal(added.Id, reloaded[0].Id);
    }

    [Fact]
    public void Adding_assigns_an_id_and_timestamps()
    {
        var added = Store().Add(New());

        Assert.False(string.IsNullOrWhiteSpace(added.Id));
        Assert.True(added.CreatedAt > 0);
        Assert.True(added.UpdatedAt > 0);
        Assert.Null(added.LastLaunchedAt);
    }

    [Fact]
    public void Adding_stamps_the_current_schema_version()
    {
        // Otherwise the next load would try to migrate a profile this build just
        // wrote, and backfill values over the user's explicit choices.
        Assert.Equal(ProfileMigration.CurrentVersion, Store().Add(New()).SchemaVersion);
    }

    [Fact]
    public void Get_returns_null_for_an_unknown_id()
    {
        Assert.Null(Store().Get("nope"));
    }

    // ---------------------------------------------------------------------
    // Names
    // ---------------------------------------------------------------------

    [Fact]
    public void A_duplicate_name_gets_a_counter()
    {
        var store = Store();
        store.Add(New("Amazon"));
        Assert.Equal("Amazon (2)", store.Add(New("Amazon")).Name);
    }

    [Fact]
    public void Counters_do_not_stack_on_repeated_duplication()
    {
        var store = Store();
        store.Add(New("Amazon"));
        store.Add(New("Amazon"));

        // "Amazon (2) (2)" would be the naive result and reads as a bug to the user.
        Assert.Equal("Amazon (3)", store.Add(New("Amazon")).Name);
    }

    [Fact]
    public void Name_uniqueness_ignores_case()
    {
        var store = Store();
        store.Add(New("Amazon"));

        // "amazon" and "Amazon" are indistinguishable at a glance in a table, which
        // is the whole reason for enforcing uniqueness.
        Assert.NotEqual("amazon", store.Add(New("amazon")).Name);
    }

    [Fact]
    public void A_blank_name_becomes_a_placeholder()
    {
        Assert.Equal("New profile", Store().Add(New("   ")).Name);
    }

    [Fact]
    public void Renaming_a_profile_to_its_own_name_does_not_add_a_counter()
    {
        // The editor round-trips the whole profile on save, so an unchanged name must
        // not collide with itself and drift to "Amazon (2)" on every save.
        var store = Store();
        var added = store.Add(New("Amazon"));

        var updated = store.Update(added with { Notes = "edited" });

        Assert.Equal("Amazon", updated!.Name);
    }

    // ---------------------------------------------------------------------
    // Update
    // ---------------------------------------------------------------------

    [Fact]
    public void Updating_persists_the_change()
    {
        var store = Store();
        var added = store.Add(New("Before"));

        store.Update(added with { Name = "After" });

        Assert.Equal("After", Store().Get(added.Id)!.Name);
    }

    [Fact]
    public void Updating_an_unknown_profile_returns_null()
    {
        Assert.Null(Store().Update(New() with { Id = "ghost" }));
    }

    [Fact]
    public void Updating_preserves_created_and_last_launched()
    {
        var store = Store();
        var added = store.Add(New("Aged"));
        store.MarkLaunched(added.Id);

        var stored = store.Get(added.Id)!;
        Assert.NotNull(stored.LastLaunchedAt);

        // A form that does not bind these fields sends zero/null back. Trusting the
        // incoming values would silently reset a profile's apparent age, which is the
        // one property that cannot be regenerated.
        var updated = store.Update(added with { Name = "Renamed", CreatedAt = 0, LastLaunchedAt = null });

        Assert.Equal(stored.CreatedAt, updated!.CreatedAt);
        Assert.Equal(stored.LastLaunchedAt, updated.LastLaunchedAt);
    }

    [Fact]
    public void Marking_launched_does_not_touch_the_updated_timestamp()
    {
        // UpdatedAt means "the configuration changed". Launching does not change it,
        // and conflating the two would make the sort order meaningless.
        var store = Store();
        var added = store.Add(New());
        var before = store.Get(added.Id)!.UpdatedAt;

        store.MarkLaunched(added.Id);

        Assert.Equal(before, store.Get(added.Id)!.UpdatedAt);
        Assert.NotNull(store.Get(added.Id)!.LastLaunchedAt);
    }

    // ---------------------------------------------------------------------
    // Duplicate
    // ---------------------------------------------------------------------

    [Fact]
    public void Duplicating_clears_the_seed_by_default()
    {
        // Two profiles sharing a seed present the same device, which is exactly the
        // correlation the tool exists to break. Sharing must be deliberate.
        var store = Store();
        var added = store.Add(New("Amazon") with
        {
            Fingerprint = new FingerprintConfig { Seed = 12345 },
        });

        var copy = store.Duplicate(added.Id);

        Assert.Null(copy!.Fingerprint.Seed);
        Assert.Equal(12345, store.Get(added.Id)!.Fingerprint.Seed);
    }

    [Fact]
    public void Duplicating_can_keep_the_seed_when_asked()
    {
        var store = Store();
        var added = store.Add(New() with { Fingerprint = new FingerprintConfig { Seed = 12345 } });

        Assert.Equal(12345, store.Duplicate(added.Id, newSeed: false)!.Fingerprint.Seed);
    }

    [Fact]
    public void A_duplicate_has_a_new_id_and_no_launch_history()
    {
        var store = Store();
        var added = store.Add(New("Original"));
        store.MarkLaunched(added.Id);

        var copy = store.Duplicate(added.Id)!;

        Assert.NotEqual(added.Id, copy.Id);
        // Inheriting the original's launch time would misreport the copy's age.
        Assert.Null(copy.LastLaunchedAt);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Duplicating_an_unknown_profile_returns_null()
    {
        Assert.Null(Store().Duplicate("ghost"));
    }

    // ---------------------------------------------------------------------
    // Remove
    // ---------------------------------------------------------------------

    [Fact]
    public void Removing_persists()
    {
        var store = Store();
        var added = store.Add(New());

        Assert.True(store.Remove(added.Id));
        Assert.Empty(Store().List());
    }

    [Fact]
    public void Removing_an_unknown_id_reports_false()
    {
        Assert.False(Store().Remove("ghost"));
    }

    [Fact]
    public void A_failed_remove_does_not_rewrite_the_file()
    {
        var store = Store();
        store.Add(New());
        var before = File.GetLastWriteTimeUtc(_path);

        store.Remove("ghost");

        // A no-op that rewrites the file makes a failed operation look successful.
        Assert.Equal(before, File.GetLastWriteTimeUtc(_path));
    }

    // ---------------------------------------------------------------------
    // Folders
    // ---------------------------------------------------------------------

    [Fact]
    public void A_folder_survives_a_reload()
    {
        var folder = Store().AddFolder("Work");

        var reloaded = Store().Folders();
        Assert.Single(reloaded);
        Assert.Equal("Work", reloaded[0].Name);
        Assert.Equal(folder.Id, reloaded[0].Id);
    }

    [Fact]
    public void Deleting_a_folder_keeps_its_profiles_and_moves_them_to_the_root()
    {
        // Deleting a container in a file manager takes its contents. A profile is
        // real work — an aged identity with cookies and history — so losing several
        // to one misclick on a grouping label would be indefensible.
        var store = Store();
        var folder = store.AddFolder("Work");
        var profile = store.Add(New("Inside") with { FolderId = folder.Id });

        Assert.True(store.RemoveFolder(folder.Id));

        var survivor = store.Get(profile.Id);
        Assert.NotNull(survivor);
        Assert.Null(survivor!.FolderId);
    }

    [Fact]
    public void A_profile_pointing_at_a_missing_folder_is_moved_to_the_root_on_load()
    {
        // Written directly, simulating a file where the folder was lost. Without this
        // repair the profile filters out of every folder view and looks deleted.
        File.WriteAllText(_path, """
        {
          "version": 1,
          "profiles": [{
            "id": "p1", "name": "Orphan", "tags": [], "folderId": "gone",
            "fingerprint": {}, "proxy": {}, "locale": {}, "geo": {},
            "behaviour": {}, "startup": {}, "schemaVersion": 3
          }],
          "folders": []
        }
        """);

        var loaded = Store().List();
        Assert.Single(loaded);
        Assert.Null(loaded[0].FolderId);
    }

    // ---------------------------------------------------------------------
    // Load robustness
    // ---------------------------------------------------------------------

    [Fact]
    public void One_unreadable_profile_does_not_hide_the_others()
    {
        // A single bad entry hiding every other profile is far worse than losing the
        // one that was already broken.
        File.WriteAllText(_path, """
        {
          "version": 1,
          "profiles": [
            { "id": "good1", "name": "Good One", "tags": [], "fingerprint": {},
              "proxy": {}, "locale": {}, "geo": {}, "behaviour": {}, "startup": {},
              "schemaVersion": 3 },
            { "id": "bad", "name": "Bad", "tags": [], "fingerprint": { "seed": "not-a-number" },
              "proxy": {}, "locale": {}, "geo": {}, "behaviour": {}, "startup": {},
              "schemaVersion": 3 },
            { "id": "good2", "name": "Good Two", "tags": [], "fingerprint": {},
              "proxy": {}, "locale": {}, "geo": {}, "behaviour": {}, "startup": {},
              "schemaVersion": 3 }
          ],
          "folders": []
        }
        """);

        var store = Store();
        var loaded = store.List();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, p => p.Name == "Good One");
        Assert.Contains(loaded, p => p.Name == "Good Two");
        Assert.Contains(store.MigrationNotes, n => n.Contains("unreadable"));
    }

    [Fact]
    public void A_corrupt_file_is_quarantined_and_the_store_still_opens()
    {
        File.WriteAllText(_path, "{ not json");

        var store = Store();

        Assert.Empty(store.List());
        Assert.NotNull(store.Quarantined);
        Assert.Equal("{ not json", File.ReadAllText(store.Quarantined!));
    }

    [Fact]
    public void A_legacy_profile_is_migrated_on_load_and_written_back_once()
    {
        // schemaVersion 1, so the migration must backfill and persist. Written back so
        // the next launch is a clean read rather than repeating the work.
        File.WriteAllText(_path, """
        {
          "version": 1,
          "profiles": [{
            "id": "old", "name": "Legacy", "tags": [], "fingerprint": {},
            "proxy": {}, "locale": {}, "geo": {}, "behaviour": {}, "startup": {},
            "schemaVersion": 1
          }],
          "folders": []
        }
        """);

        var loaded = Store().List();
        Assert.Single(loaded);
        Assert.Equal(ProfileMigration.CurrentVersion, loaded[0].SchemaVersion);

        // The upgrade stuck: a second load needs no migration notes.
        var second = Store();
        second.List();
        Assert.Empty(second.MigrationNotes);
    }

    [Fact]
    public void An_ordinary_load_does_not_rewrite_the_file()
    {
        var store = Store();
        store.Add(New());
        var before = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(20);
        Store().List();

        // Rewriting on every launch burns a disk write and destroys the evidence if
        // a load ever goes wrong.
        Assert.Equal(before, File.GetLastWriteTimeUtc(_path));
    }

    // ---------------------------------------------------------------------
    // Concurrency
    // ---------------------------------------------------------------------

    [Fact]
    public void Concurrent_adds_all_survive()
    {
        // Read-modify-write on a shared list is the classic shape for silently losing
        // entries. The UI can produce this by holding Enter on a button, and an
        // automation client by scripting a batch.
        var store = Store();

        Parallel.For(0, 50, i => store.Add(New($"Profile {i}")));

        Assert.Equal(50, store.List().Count);
        Assert.Equal(50, Store().List().Count);
    }

    [Fact]
    public void Concurrent_adds_all_get_distinct_names_and_ids()
    {
        var store = Store();

        Parallel.For(0, 30, _ => store.Add(New("Same")));

        var all = store.List();
        Assert.Equal(30, all.Select(p => p.Id).Distinct().Count());
        Assert.Equal(30, all.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_snapshot_is_not_affected_by_later_mutation()
    {
        // Handing out the live list would let a caller mutate the store without the
        // gate or the save, so the UI could display state that was never persisted.
        var store = Store();
        store.Add(New("First"));

        var snapshot = store.List();
        store.Add(New("Second"));

        Assert.Single(snapshot);
    }
}
