using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.UnityConverters.Math;
using PreciseSavestates.Savestates.Game;
using PreciseSavestates.Source.Savestates.Snapshot;
using Random = UnityEngine.Random;

namespace PreciseSavestates.Savestates;

public class Savestate {
    public string? Scene;
    public List<string>? AdditiveScenes;

    public List<ComponentSnapshot>? ComponentSnapshots;
    public List<PlayMakerFsmSnapshot>? FsmSnapshots;
    public List<RandomAudioTableSnapshot>? AudioTableSnapshots;
    public List<GameObjectSnapshot>? GameObjectSnapshots;
    
    public Random.State? RandomState;

    public float? GameTime;
    public int? GameFrameCount;
    public int? FixedUpdateCycle; // CustomPlayerLoop.FixedUpdateCycle

    // Separated from component snapshot, due to required load order
    public JToken? PlayerData;
    public JToken? SceneData;

    // [NonSerialized] data
    public HazardRespawnSnapshot? HazardRespawn;

    public void SerializeTo(StreamWriter writer) {
        JsonSerializer.Create(jsonSettings).Serialize(writer, this);
    }

    public static Savestate DeserializeFrom(StreamReader reader) {
        var jsonReader = new JsonTextReader(reader);
        return JsonSerializer.Create(jsonSettings).Deserialize<Savestate>(jsonReader) ??
               throw new Exception("Failed to deserialize savestate");
    }

    public string Serialize() {
        return JsonConvert.SerializeObject(this, Formatting.Indented);
    }

    public static Savestate Deserialize(string data) {
        return JsonConvert.DeserializeObject<Savestate>(data) ??
               throw new Exception("Failed to deserialize savestate");
    }

    private static readonly JsonSerializerSettings jsonSettings = new() {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new ForceSerializeResolver {
            ForceSerializePropertiesOf = [typeof(Random.State)],
        },
        Converters = [new Vector3Converter()],
    };

    private class ForceSerializeResolver : DefaultContractResolver {
        public List<Type> ForceSerializePropertiesOf = [];

        protected override List<MemberInfo> GetSerializableMembers(Type objectType) {
            if (ForceSerializePropertiesOf.Contains(objectType)) {
                return objectType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Cast<MemberInfo>()
                    .ToList();
            }

            return base.GetSerializableMembers(objectType);
        }

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization) {
            var property = base.CreateProperty(member, MemberSerialization.Fields);

            return property;
        }
    }
}

