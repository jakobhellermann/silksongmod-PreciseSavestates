using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// Captures only whether a ParticleSystem was still alive
public class ParticleSystemConverter : JsonConverter {
    public override bool CanConvert(Type objectType) => objectType == typeof(ParticleSystem);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        var ps = (ParticleSystem)value!;
        new JObject { ["alive"] = ps.IsAlive(withChildren: false) }.WriteTo(writer);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer) {
        var token = JToken.ReadFrom(reader);
        var ps = (ParticleSystem)existingValue!;
        if (!token.Value<bool>("alive")) {
            ps.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        return ps;
    }
}
