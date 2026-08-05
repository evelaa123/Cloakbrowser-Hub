using System.Text.Json;
using System.Text.Json.Nodes;
using CloakHub.Core.Model;

namespace CloakHub.Core.Storage;

/// <summary>
/// Forward-migration for profiles read off disk.
/// <para>
/// The profile store is hand-editable by design and accumulates profiles created
/// by older builds. Deserialising them straight into the app means a field added
/// in a later release stays at its CLR default on every pre-existing profile.
/// That is not a cosmetic gap: the flag builder only emits a flag when a value is
/// present, so an old profile silently launches <i>without</i> the flag while the
/// UI shows it as "default".
/// </para>
/// <para>
/// That is exactly how the incognito-detection bug happened. <c>StorageQuotaMb</c>
/// has a non-incognito default, but a profile created before that default landed
/// had no quota at all, so <c>--fingerprint-storage-quota</c> was never passed,
/// the binary's normalised default applied, and the detector read it as a private
/// window — while the profile's own editor insisted everything was default.
/// </para>
/// <para>Two rules make this safe:</para>
/// <list type="number">
///   <item><b>Structural repair is unconditional.</b> A missing sub-object is
///   always filled, because there is no legitimate reading of "absent" for those
///   — the app would misbehave either way.</item>
///   <item><b>Value backfill is version-gated.</b> <c>SchemaVersion</c> records
///   which steps a profile has been through, so backfilling can never undo a
///   deliberate choice. If the user clears the storage quota to hand control back
///   to the binary, that empty value survives every future load, because the
///   profile is already at or past the version that introduced the field.</item>
/// </list>
/// </summary>
public static class ProfileMigration
{
    /// <summary>
    /// Current schema version.
    /// <list type="bullet">
    ///   <item>1 — baseline.</item>
    ///   <item>2 — storage quota backfilled to a plausible disk size.</item>
    ///   <item>3 — per-surface noise, port blocking and folders introduced.</item>
    ///   <item>4 — workflow status, category, MAC intent and device name introduced.</item>
    /// </list>
    /// </summary>
    public const int CurrentVersion = 4;

    /// <summary>Default storage quota in MB — describes a plausible disk, not merely a threshold pass.</summary>
    public const int DefaultStorageQuotaMb = 120000;

    public sealed record Result(Profile Profile, bool Changed, List<string> Notes);

    /// <summary>
    /// Migrate one profile.
    /// </summary>
    /// <param name="raw">
    /// The profile as read from disk, as a mutable JSON object. Working on the
    /// JSON rather than a deserialised record is deliberate: it is the only way
    /// to distinguish "the field was absent" from "the field was present and
    /// null/zero", and that distinction is the whole point of rule 2.
    /// </param>
    public static Result Migrate(JsonObject raw)
    {
        var notes = new List<string>();
        var changed = false;

        var version = raw["schemaVersion"]?.GetValue<int>() ?? 0;

        // ------------------------------------------------------------------
        // Rule 1: structural repair, always.
        // ------------------------------------------------------------------
        foreach (var (key, factory) in RequiredObjects)
        {
            if (raw[key] is JsonObject) continue;
            raw[key] = factory();
            changed = true;
            notes.Add($"Repaired missing '{key}' section.");
        }

        foreach (var key in RequiredArrays)
        {
            if (raw[key] is JsonArray) continue;
            raw[key] = new JsonArray();
            changed = true;
            notes.Add($"Repaired missing '{key}' list.");
        }

        if (raw["id"] is null || string.IsNullOrWhiteSpace(raw["id"]!.GetValue<string>()))
        {
            raw["id"] = Guid.NewGuid().ToString();
            changed = true;
            notes.Add("Assigned an id to a profile that had none.");
        }

        // ------------------------------------------------------------------
        // Rule 2: value backfill, version-gated.
        // ------------------------------------------------------------------
        if (version < 2)
        {
            var fp = raw["fingerprint"]!.AsObject();
            // Only fill when the key is genuinely ABSENT. An explicit null means
            // the user cleared it, and re-adding it here would be the bug this
            // gate exists to prevent.
            if (!fp.ContainsKey("storageQuotaMb"))
            {
                fp["storageQuotaMb"] = DefaultStorageQuotaMb;
                changed = true;
                notes.Add($"Backfilled storage quota to {DefaultStorageQuotaMb} MB (was never set; " +
                          "an absent quota is what detectors read as an incognito window).");
            }
        }

        if (version < 3)
        {
            var startup = raw["startup"]!.AsObject();
            if (!startup.ContainsKey("blockedPorts"))
            {
                startup["blockedPorts"] = new JsonArray(
                    [.. Launch.PrivacyArgs.DefaultBlockedPorts.Select(p => (JsonNode)p)]);
                changed = true;
                notes.Add("Enabled default localhost port protection.");
            }
        }

        // Version 4 adds status, kind, mac and deviceName. There is deliberately no
        // backfill step for them: every one has a meaningful "unset" default (None,
        // None, Real, null) and an absent field deserialises to exactly that. Writing
        // them in explicitly would touch every profile on disk to record nothing, and
        // rule 2 exists to stop precisely that kind of churn.
        //
        // The structural repair above does cover the new `mac` sub-object, because an
        // absent object and an empty one are not equivalent to the deserialiser.

        if (version != CurrentVersion)
        {
            raw["schemaVersion"] = CurrentVersion;
            changed = true;
        }

        var profile = raw.Deserialize<Profile>(JsonOptions) ?? new Profile { Id = Guid.NewGuid().ToString() };
        return new Result(profile, changed, notes);
    }

    private static readonly (string Key, Func<JsonObject> Factory)[] RequiredObjects =
    [
        ("fingerprint", () => new JsonObject()),
        ("proxy",       () => new JsonObject()),
        ("locale",      () => new JsonObject()),
        ("geo",         () => new JsonObject()),
        ("behaviour",   () => new JsonObject()),
        ("startup",     () => new JsonObject()),
        ("mac",         () => new JsonObject()),
    ];

    private static readonly string[] RequiredArrays = ["tags"];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: true),

            // Required for backward compatibility: the Electron app stores `noise`
            // as a single boolean, this port models it as four per-surface values.
            // Without this converter every profile written by the Electron app
            // throws on load. See NoiseConfigConverter.
            new NoiseConfigConverter(),
        },
    };
}
