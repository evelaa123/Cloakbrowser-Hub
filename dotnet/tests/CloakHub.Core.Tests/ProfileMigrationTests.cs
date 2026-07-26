using System.Text.Json.Nodes;
using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using CloakHub.Core.Storage;

namespace CloakHub.Core.Tests;

public class ProfileMigrationTests
{
    private static JsonObject Legacy(string json) => JsonNode.Parse(json)!.AsObject();

    // ---------------------------------------------------------------------
    // Rule 1 — structural repair is unconditional.
    // ---------------------------------------------------------------------

    [Fact]
    public void Repairs_every_missing_section()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","name":"old"}"""));
        Assert.True(r.Changed);
        Assert.NotNull(r.Profile.Fingerprint);
        Assert.NotNull(r.Profile.Proxy);
        Assert.NotNull(r.Profile.Locale);
        Assert.NotNull(r.Profile.Geo);
        Assert.NotNull(r.Profile.Behaviour);
        Assert.NotNull(r.Profile.Startup);
        Assert.NotNull(r.Profile.Tags);
    }

    [Fact]
    public void Repairs_a_section_that_is_the_wrong_type()
    {
        // Hand-edited files do contain junk; a string where an object belongs
        // must not throw on load.
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","fingerprint":"broken","tags":42}"""));
        Assert.NotNull(r.Profile.Fingerprint);
        Assert.Empty(r.Profile.Tags);
    }

    [Fact]
    public void Assigns_an_id_when_one_is_missing()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"name":"no id"}"""));
        Assert.False(string.IsNullOrWhiteSpace(r.Profile.Id));
        Assert.True(r.Changed);
    }

    // ---------------------------------------------------------------------
    // Rule 2 — value backfill is version-gated. These are the tests that
    // caught my own first implementation re-adding a deliberately cleared value.
    // ---------------------------------------------------------------------

    [Fact]
    public void Backfills_storage_quota_on_a_legacy_profile()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","fingerprint":{}}"""));
        Assert.Equal(ProfileMigration.DefaultStorageQuotaMb, r.Profile.Fingerprint.StorageQuotaMb);
        Assert.Contains(r.Notes, n => n.Contains("storage quota", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Does_not_resurrect_a_quota_the_user_deliberately_cleared()
    {
        // An explicit null at the current version means "hand control back to the
        // binary". Refilling it would be exactly the bug the version gate exists
        // to prevent — and my first attempt did it, by spreading the defaults
        // wholesale instead of checking key presence.
        var v = ProfileMigration.CurrentVersion;
        var r = ProfileMigration.Migrate(Legacy(
            """{"id":"p1","schemaVersion":""" + v + ""","fingerprint":{"storageQuotaMb":null}}"""));
        Assert.Null(r.Profile.Fingerprint.StorageQuotaMb);
    }

    [Fact]
    public void Does_not_overwrite_a_quota_the_user_chose()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","fingerprint":{"storageQuotaMb":4096}}"""));
        Assert.Equal(4096, r.Profile.Fingerprint.StorageQuotaMb);
    }

    [Fact]
    public void Backfills_port_protection_for_pre_v3_profiles()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","schemaVersion":2}"""));
        Assert.Equal(PrivacyArgs.DefaultBlockedPorts.Length, r.Profile.Startup.BlockedPorts.Count);
    }

    [Fact]
    public void Does_not_resurrect_a_port_list_the_user_emptied()
    {
        var v = ProfileMigration.CurrentVersion;
        var r = ProfileMigration.Migrate(Legacy(
            """{"id":"p1","schemaVersion":""" + v + ""","startup":{"blockedPorts":[]}}"""));
        Assert.Empty(r.Profile.Startup.BlockedPorts);
    }

    // ---------------------------------------------------------------------
    // Change detection drives whether the user's file is rewritten, so a
    // no-op load must report no change — otherwise every app start dirties
    // the file and the mtime becomes meaningless.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_current_profile_reports_no_change()
    {
        var current = $$"""
            {
              "id": "p1", "name": "n", "tags": [],
              "schemaVersion": {{ProfileMigration.CurrentVersion}},
              "fingerprint": {"storageQuotaMb": 120000},
              "proxy": {}, "locale": {}, "geo": {}, "behaviour": {},
              "startup": {"blockedPorts": [3389]}
            }
            """;
        var r = ProfileMigration.Migrate(Legacy(current));
        Assert.False(r.Changed);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        var raw = Legacy("""{"id":"p1"}""");
        var first = ProfileMigration.Migrate(raw);
        Assert.True(first.Changed);

        // Re-running on the already-migrated JSON must settle.
        var second = ProfileMigration.Migrate(raw);
        Assert.False(second.Changed);
    }

    [Fact]
    public void Version_is_stamped_forward()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1"}"""));
        Assert.Equal(ProfileMigration.CurrentVersion, r.Profile.SchemaVersion);
    }

    [Fact]
    public void Notes_explain_every_change_for_the_log()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1"}"""));
        Assert.True(r.Changed);
        Assert.NotEmpty(r.Notes);
        Assert.All(r.Notes, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }
}
