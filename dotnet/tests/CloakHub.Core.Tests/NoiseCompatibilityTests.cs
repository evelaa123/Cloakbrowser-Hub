using System.Text.Json;
using System.Text.Json.Nodes;
using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using CloakHub.Core.Storage;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// Backward compatibility for the noise setting, and the default that goes with it.
/// <para>
/// This file exists because of three defects found by running the diagnostics CLI
/// and reading a real profile through the migration, none of which any existing
/// test caught:
/// </para>
/// <list type="number">
///   <item>The ported default inverted — new profiles launched with noise OFF
///   while the Electron original defaults it ON.</item>
///   <item>A profile written by the Electron app threw on load, because
///   <c>noise</c> is a boolean there and an object here.</item>
///   <item>The original test asserted the inverted default, so it locked the bug
///   in rather than catching it.</item>
/// </list>
/// </summary>
public class NoiseCompatibilityTests
{
    /// <summary>A profile in the exact shape the Electron app writes.</summary>
    private static JsonObject LegacyProfile(string noiseLiteral) =>
        JsonNode.Parse($$"""
        {
          "id": "legacy-0001",
          "name": "From Electron",
          "tags": [],
          "fingerprint": { "seed": 48219, "platform": "windows", "noise": {{noiseLiteral}} },
          "proxy": {}, "locale": {}, "geo": {}, "behaviour": {}, "startup": {},
          "schemaVersion": 3
        }
        """)!.AsObject();

    // ---------------------------------------------------------------------
    // The default.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_new_noise_config_enables_noise_on_every_surface()
    {
        var config = new NoiseConfig();
        Assert.Equal(NoiseMode.Noise, config.Canvas);
        Assert.Equal(NoiseMode.Noise, config.WebGl);
        Assert.Equal(NoiseMode.Noise, config.Audio);
        Assert.Equal(NoiseMode.Noise, config.ClientRects);
        Assert.True(config.Resolve());
    }

    [Fact]
    public void A_new_profile_matches_the_electron_default()
    {
        // defaults.ts sets `noise: true`. A port that flips a security default is
        // a regression even when every individual piece behaves as written.
        Assert.True(new Profile().Fingerprint.Noise.Resolve());
    }

    // ---------------------------------------------------------------------
    // Reading the legacy boolean.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void A_legacy_boolean_profile_loads_and_keeps_its_setting(string literal, bool expected)
    {
        var result = ProfileMigration.Migrate(LegacyProfile(literal));
        Assert.Equal(expected, result.Profile.Fingerprint.Noise.Resolve());
    }

    [Fact]
    public void A_legacy_boolean_fans_out_to_all_four_surfaces()
    {
        var noise = ProfileMigration.Migrate(LegacyProfile("false")).Profile.Fingerprint.Noise;
        Assert.Equal(NoiseMode.Real, noise.Canvas);
        Assert.Equal(NoiseMode.Real, noise.WebGl);
        Assert.Equal(NoiseMode.Real, noise.Audio);
        Assert.Equal(NoiseMode.Real, noise.ClientRects);
    }

    [Fact]
    public void A_legacy_noise_off_profile_still_emits_the_disable_flag()
    {
        // The end-to-end claim: a user who turned noise off in the Electron app
        // still gets it off after the upgrade. Silently re-enabling it would break
        // profiles tuned for sites that reject noisy canvas readings.
        var profile = ProfileMigration.Migrate(LegacyProfile("false")).Profile;
        Assert.Contains("--fingerprint-noise=false", FingerprintArgs.Build(profile));
    }

    [Fact]
    public void An_absent_noise_key_takes_the_default()
    {
        var raw = JsonNode.Parse("""
        {
          "id": "no-noise-key",
          "name": "Sparse",
          "tags": [],
          "fingerprint": { "seed": 1234 },
          "proxy": {}, "locale": {}, "geo": {}, "behaviour": {}, "startup": {},
          "schemaVersion": 3
        }
        """)!.AsObject();

        Assert.True(ProfileMigration.Migrate(raw).Profile.Fingerprint.Noise.Resolve());
    }

    [Fact]
    public void An_explicit_null_takes_the_default()
    {
        Assert.True(ProfileMigration.Migrate(LegacyProfile("null")).Profile.Fingerprint.Noise.Resolve());
    }

    // ---------------------------------------------------------------------
    // Reading the modern object.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_modern_object_round_trips_through_serialisation()
    {
        var original = new NoiseConfig
        {
            Canvas = NoiseMode.Noise,
            WebGl = NoiseMode.Real,
            Audio = NoiseMode.Off,
            ClientRects = NoiseMode.Noise,
        };

        var json = JsonSerializer.Serialize(original, ProfileMigration.JsonOptions);
        var back = JsonSerializer.Deserialize<NoiseConfig>(json, ProfileMigration.JsonOptions);

        Assert.Equal(original, back);
    }

    [Fact]
    public void A_partial_object_leaves_unlisted_surfaces_at_the_default()
    {
        var noise = ProfileMigration.Migrate(LegacyProfile("""{ "canvas": "Real" }"""))
            .Profile.Fingerprint.Noise;

        Assert.Equal(NoiseMode.Real, noise.Canvas);
        Assert.Equal(NoiseMode.Noise, noise.WebGl);
    }

    [Fact]
    public void Per_surface_booleans_are_accepted()
    {
        // A hand-edited or partially-migrated file can mix the two forms, and a
        // profile is too valuable to reject over a shape a human could have typed.
        var noise = ProfileMigration.Migrate(
                LegacyProfile("""{ "canvas": false, "webGl": true }"""))
            .Profile.Fingerprint.Noise;

        Assert.Equal(NoiseMode.Real, noise.Canvas);
        Assert.Equal(NoiseMode.Noise, noise.WebGl);
    }

    [Fact]
    public void An_unknown_surface_name_is_ignored_rather_than_fatal()
    {
        var result = ProfileMigration.Migrate(
            LegacyProfile("""{ "canvas": "Real", "webgpu": "Noise" }"""));

        Assert.Equal(NoiseMode.Real, result.Profile.Fingerprint.Noise.Canvas);
    }

    [Fact]
    public void An_unrecognised_mode_name_falls_back_to_the_default()
    {
        var noise = ProfileMigration.Migrate(LegacyProfile("""{ "canvas": "Fabulous" }"""))
            .Profile.Fingerprint.Noise;

        Assert.Equal(NoiseMode.Noise, noise.Canvas);
    }

    [Fact]
    public void A_nested_value_of_the_wrong_shape_is_skipped()
    {
        var noise = ProfileMigration.Migrate(
                LegacyProfile("""{ "canvas": { "nested": 1 }, "webGl": "Real" }"""))
            .Profile.Fingerprint.Noise;

        // The bad value is skipped and the reader stays in sync, so the following
        // property is still read correctly. That second assertion is the point:
        // a converter that skipped incorrectly would silently lose it.
        Assert.Equal(NoiseMode.Noise, noise.Canvas);
        Assert.Equal(NoiseMode.Real, noise.WebGl);
    }

    [Fact]
    public void A_wrongly_typed_noise_value_is_reported_rather_than_guessed()
    {
        // A string where an object or boolean belongs is a corrupt file, not an old
        // format. Guessing here would hide real corruption.
        var ex = Record.Exception(() => ProfileMigration.Migrate(LegacyProfile("\"yes please\"")));
        Assert.IsType<JsonException>(ex);
    }

    // ---------------------------------------------------------------------
    // Writing.
    // ---------------------------------------------------------------------

    [Fact]
    public void Saving_upgrades_a_legacy_boolean_to_the_object_form()
    {
        var profile = ProfileMigration.Migrate(LegacyProfile("false")).Profile;
        var json = JsonSerializer.Serialize(profile, ProfileMigration.JsonOptions);

        // The upgrade has to stick on disk, or every load pays the conversion and
        // the file never matches what the UI shows.
        Assert.Contains("\"canvas\": \"Real\"", json);
        Assert.DoesNotContain("\"noise\": false", json);
    }

    [Fact]
    public void Written_property_names_follow_the_camel_case_policy()
    {
        var json = JsonSerializer.Serialize(new NoiseConfig(), ProfileMigration.JsonOptions);
        Assert.Contains("\"clientRects\"", json);
        Assert.DoesNotContain("\"ClientRects\"", json);
    }
}
