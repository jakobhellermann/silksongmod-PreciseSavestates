using System;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// RaycastHit2D holds its collider as a raw instance id, which differs between scene loads; store it as a path instead
public class RaycastHit2DConverter : JsonConverter {
    private static readonly FieldInfo ColliderId = AccessTools.Field(typeof(RaycastHit2D), "m_Collider");

    public override bool CanConvert(Type objectType) => objectType == typeof(RaycastHit2D);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        var hit = (RaycastHit2D)value!;
        new JObject {
            ["centroid"] = JToken.FromObject(hit.centroid, serializer),
            ["point"] = JToken.FromObject(hit.point, serializer),
            ["normal"] = JToken.FromObject(hit.normal, serializer),
            ["distance"] = hit.distance,
            ["fraction"] = hit.fraction,
            ["collider"] = hit.collider ? ObjectUtils.ObjectComponentPath(hit.collider) : null,
        }.WriteTo(writer);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer) {
        var token = JObject.Load(reader);
        object hit = new RaycastHit2D {
            centroid = token["centroid"]!.ToObject<Vector2>(serializer),
            point = token["point"]!.ToObject<Vector2>(serializer),
            normal = token["normal"]!.ToObject<Vector2>(serializer),
            distance = token.Value<float>("distance"),
            fraction = token.Value<float>("fraction"),
        };

        if (token.Value<string?>("collider") is { } path) {
            if (ObjectUtils.LookupObjectComponentPath(path) is Collider2D collider) {
                ColliderId.SetValue(hit, collider.GetInstanceID());
            } else {
                Log.Warning($"Could not resolve RaycastHit2D collider '{path}' at {reader.Path}");
            }
        }

        return hit;
    }
}
