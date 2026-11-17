using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Source;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// Clips are owned by shared tk2dSpriteAnimation assets: store them as {library, clip} names and resolve back to the
// loaded instance, since populating one by value would overwrite the asset's frames (nulling their spriteCollection).
public class Tk2dClipConverter : JsonConverter {
    private static readonly Dictionary<tk2dSpriteAnimationClip, tk2dSpriteAnimation> libraryByClip = new();
    private static readonly Dictionary<string, tk2dSpriteAnimation> libraryByName = new();

    public override bool CanConvert(Type objectType) => objectType == typeof(tk2dSpriteAnimationClip);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        if (value is not tk2dSpriteAnimationClip clip) {
            writer.WriteNull();
            return;
        }

        if (!libraryByClip.TryGetValue(clip, out var library) || !library) {
            RebuildIndex();
            if (!libraryByClip.TryGetValue(clip, out library)) {
                throw new JsonSerializationException(
                    $"tk2d clip '{clip.name}' at {writer.Path} belongs to no loaded tk2dSpriteAnimation");
            }
        }

        new JObject {
            ["library"] = library.name,
            ["clip"] = clip.name,
        }.WriteTo(writer);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer) {
        var token = JToken.ReadFrom(reader);
        if (token.Type == JTokenType.Null) {
            return null;
        }

        if (token.Value<string?>("library") is not { } libraryName) {
            Log.Warning($"Savestate stores tk2d clip at {reader.Path} by value (old format), keeping the live clip");
            return existingValue;
        }

        var clipName = token.Value<string>("clip");
        if (!libraryByName.TryGetValue(libraryName, out var library) || !library) {
            RebuildIndex();
            libraryByName.TryGetValue(libraryName, out library);
        }

        return library?.GetClipByName(clipName)
               ?? throw new JsonSerializationException(
                   $"Could not resolve tk2d clip '{libraryName}/{clipName}' at {reader.Path}");
    }

    private static void RebuildIndex() {
        libraryByClip.Clear();
        libraryByName.Clear();
        foreach (var library in Resources.FindObjectsOfTypeAll<tk2dSpriteAnimation>()) {
            libraryByName[library.name] = library;
            foreach (var clip in library.clips) {
                libraryByClip[clip] = library;
            }
        }
    }
}
