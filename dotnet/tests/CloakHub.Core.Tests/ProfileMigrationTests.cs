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
    // Version 4 — status, kind, MAC intent and device name. There is
    // deliberately no backfill step: every one of them has a meaningful
    // "unset" default that an absent field already deserialises to, so
    // writing them in would touch every profile on disk to record nothing.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_pre_v4_profile_gains_the_new_fields_at_their_unset_defaults()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","schemaVersion":3}"""));

        Assert.Equal(ProfileStatus.None, r.Profile.Status);
        Assert.Equal(ProfileKind.None, r.Profile.Kind);
        Assert.Null(r.Profile.DeviceName);
        // Real means "leave the interface alone", which is the only safe default for
        // something that needs elevated privileges to apply.
        Assert.Equal(ValueMode.Real, r.Profile.Mac.Mode);
        Assert.Null(r.Profile.Mac.Address);
    }

    [Fact]
    public void The_mac_section_is_repaired_when_absent()
    {
        // Structural repair, not backfill: an absent object and an empty one are not
        // equivalent to the deserialiser, so this one does need creating.
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","schemaVersion":3}"""));

        Assert.NotNull(r.Profile.Mac);
        Assert.Contains(r.Notes, n => n.Contains("mac", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_mac_section_is_repaired_when_it_is_the_wrong_type()
    {
        var r = ProfileMigration.Migrate(Legacy("""{"id":"p1","mac":"eth0"}"""));

        Assert.NotNull(r.Profile.Mac);
        Assert.Equal(ValueMode.Real, r.Profile.Mac.Mode);
    }

    [Fact]
    public void Upgrading_to_v4_does_not_disturb_the_values_the_user_already_set()
    {
        // The v4 step must not be a "spread the defaults" pass — that is the bug the
        // version gate exists to prevent, and it would silently clear a label the
        // user set on a profile they had already upgraded once.
        var r = ProfileMigration.Migrate(Legacy("""
        {
          "id": "p1", "schemaVersion": 3,
          "status": "Banned", "kind": "Facebook", "deviceName": "DESKTOP-7F2K1",
          "mac": { "mode": "Manual", "address": "02:1A:2B:3C:4D:5E", "interfaceName": "eth0" }
        }
        """));

        Assert.Equal(ProfileStatus.Banned, r.Profile.Status);
        Assert.Equal(ProfileKind.Facebook, r.Profile.Kind);
        Assert.Equal("DESKTOP-7F2K1", r.Profile.DeviceName);
        Assert.Equal(ValueMode.Manual, r.Profile.Mac.Mode);
        Assert.Equal("02:1A:2B:3C:4D:5E", r.Profile.Mac.Address);
        Assert.Equal("eth0", r.Profile.Mac.InterfaceName);
    }

    [Fact]
    public void A_v3_profile_is_stamped_up_to_v4()
    {
        var r = ProfileMigration.Migrate(Legacy("""
        {
          "id": "p1", "name": "n", "tags": [],
          "schemaVersion": 3,
          "fingerprint": {"storageQuotaMb": 120000},
          "proxy": {}, "locale": {}, "geo": {}, "behaviour": {},
          "startup": {"blockedPorts": [3389]}, "mac": {}
        }
        """));

        Assert.True(r.Changed);
        Assert.Equal(4, r.Profile.SchemaVersion);
        Assert.Equal(ProfileMigration.CurrentVersion, r.Profile.SchemaVersion);
    }

    [Fact]
    public void A_v4_upgrade_does_not_re_run_the_earlier_backfills()
    {
        // A v3 profile has already had its quota and port list decided. Re-running
        // those steps on the way to v4 would overwrite deliberate clearings.
        var r = ProfileMigration.Migrate(Legacy("""
        {
          "id": "p1", "schemaVersion": 3,
          "fingerprint": { "storageQuotaMb": null },
          "startup": { "blockedPorts": [] }
        }
        """));

        Assert.Null(r.Profile.Fingerprint.StorageQuotaMb);
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
        // Every required section must be present, `mac` included. It was missing
        // here until schema 4 shipped, and the omission made this test fail for the
        // right reason: structural repair had genuine work to do, so the profile was
        // not in fact current.
        var current = $$"""
            {
              "id": "p1", "name": "n", "tags": [],
              "schemaVersion": {{ProfileMigration.CurrentVersion}},
              "fingerprint": {"storageQuotaMb": 120000},
              "proxy": {}, "locale": {}, "geo": {}, "behaviour": {},
              "startup": {"blockedPorts": [3389]}, "mac": {}
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
