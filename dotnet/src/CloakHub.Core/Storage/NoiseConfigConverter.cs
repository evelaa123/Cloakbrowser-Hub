using System.Text.Json;
using System.Text.Json.Serialization;
using CloakHub.Core.Model;

namespace CloakHub.Core.Storage;

/// <summary>
/// Reads <see cref="NoiseConfig"/> from either the legacy boolean or the current
/// object form.
/// <para>
/// The Electron app stores <c>"noise": true</c> — a single boolean, matching the
/// browser binary's single flag. This port widened it to four per-surface values
/// so the UI can offer the control users expect. Without this converter that
/// widening is a breaking change: <c>JsonSerializer</c> throws
/// <c>JsonException</c> on the boolean, the migration propagates it, and every
/// profile written by the Electron app becomes unopenable. That is data loss from
/// the user's point of view, since the profile file is the only record of a
/// browsing identity they may have spent months ageing.
/// </para>
/// <para>
/// Found by feeding a real Electron-format profile through the migration rather
/// than by reading the code — the type mismatch is invisible at the record
/// definition, and no existing test used the legacy shape.
/// </para>
/// </summary>
public sealed class NoiseConfigConverter : JsonConverter<NoiseConfig>
{
    /// <summary>
    /// Take control of nulls instead of letting the serializer handle them.
    /// <para>
    /// This defaults to <c>false</c> for reference types, and that default is a
    /// live bug here: <c>System.Text.Json</c> then short-circuits a JSON null and
    /// assigns <c>null</c> to the property without consulting this converter, so
    /// the record's <c>= new()</c> initialiser is overwritten and the first call to
    /// <c>Noise.Resolve()</c> throws a <c>NullReferenceException</c> at launch —
    /// far from the profile that caused it.
    /// </para>
    /// <para>
    /// Caught by a test that fed <c>"noise": null</c> through the migration. The
    /// converter already had a Null branch; without this override that branch was
    /// simply unreachable, which is a good reminder that handling a case in code is
    /// not the same as being asked to handle it.
    /// </para>
    /// </summary>
    public override bool HandleNull => true;

    public override NoiseConfig Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            // Legacy: one boolean for all four surfaces. Fan it out rather than
            // discarding it — the user's explicit "noise off" has to survive the
            // upgrade, or a profile tuned for a site that rejects canvas noise
            // silently starts sending it again.
            case JsonTokenType.True:
            case JsonTokenType.False:
                var mode = reader.GetBoolean() ? NoiseMode.Noise : NoiseMode.Real;
                return new NoiseConfig
                {
                    Canvas = mode,
                    WebGl = mode,
                    Audio = mode,
                    ClientRects = mode,
                };

            // An explicit null means "never configured", which is the default.
            case JsonTokenType.Null:
                return new NoiseConfig();

            case JsonTokenType.StartObject:
                return ReadObject(ref reader, options);

            default:
                throw new JsonException(
                    $"Expected an object or a boolean for the noise setting, found {reader.TokenType}.");
        }
    }

    /// <summary>
    /// Read the modern object form.
    /// <para>
    /// Hand-rolled rather than delegating to the default converter, because
    /// delegating would re-enter this one and recurse forever. Unknown properties
    /// are skipped so a profile written by a newer build still loads here instead
    /// of failing — forward tolerance matters when the same profile directory can
    /// be shared between versions.
    /// </para>
    /// </summary>
    private static NoiseConfig ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var config = new NoiseConfig();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return config;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException($"Unexpected {reader.TokenType} inside the noise object.");

            var name = reader.GetString();
            reader.Read();

            // Per-surface values may themselves be booleans, because a
            // hand-edited or partially-migrated file can mix the two forms.
            var value = ReadMode(ref reader, options);
            if (value is null) continue;

            config = name?.ToLowerInvariant() switch
            {
                "canvas" => config with { Canvas = value.Value },
                "webgl" => config with { WebGl = value.Value },
                "audio" => config with { Audio = value.Value },
                "clientrects" => config with { ClientRects = value.Value },
                _ => config,
            };
        }

        throw new JsonException("The noise object was not closed.");
    }

    /// <summary>One surface's mode, from a boolean, a string or a number.</summary>
    private static NoiseMode? ReadMode(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return NoiseMode.Noise;
            case JsonTokenType.False:
                return NoiseMode.Real;
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var text = reader.GetString();
                return Enum.TryParse<NoiseMode>(text, ignoreCase: true, out var parsed)
                    ? parsed
                    // An unrecognised name is treated as "leave the default" rather
                    // than as an error: refusing to open the whole profile over one
                    // unknown surface value would be wildly disproportionate.
                    : null;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(NoiseMode), n)
                    ? (NoiseMode)n
                    : null;

            default:
                // Skip whatever this is (an array, a nested object) and keep going.
                reader.Skip();
                return null;
        }
    }

    /// <summary>
    /// Always writes the modern object form.
    /// <para>
    /// Writing the object even for a profile that arrived as a boolean is what
    /// makes the upgrade stick: the next read needs no conversion, and the file on
    /// disk matches what the UI presents.
    /// </para>
    /// </summary>
    public override void Write(Utf8JsonWriter writer, NoiseConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteMode(writer, options, "canvas", value.Canvas);
        WriteMode(writer, options, "webGl", value.WebGl);
        WriteMode(writer, options, "audio", value.Audio);
        WriteMode(writer, options, "clientRects", value.ClientRects);
        writer.WriteEndObject();
    }

    private static void WriteMode(
        Utf8JsonWriter writer, JsonSerializerOptions options, string name, NoiseMode mode)
    {
        // Honour the naming policy so this converter does not become the one place
        // in the file that ignores it.
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
        writer.WriteStringValue(mode.ToString());
    }
}
