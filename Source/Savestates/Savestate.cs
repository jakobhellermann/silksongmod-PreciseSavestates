using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.UnityConverters.Math;
using PreciseSavestates.Savestates.Snapshot;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;
using Random = UnityEngine.Random;

namespace PreciseSavestates.Savestates;

public class Savestate {
    public string? Scene;

    public List<ComponentSnapshot>? ComponentSnapshots;

    public List<PlayMakerFsmSnapshot>? FsmSnapshots;

    public List<RandomAudioTableSnapshot>? AudioTableSnapshots;

    // public List<GameObjectSnapshot>? GameObjectSnapshots;
    // public List<GeneralFsmSnapshot>? GeneralFsmSnapshots;
    // public JObject? Flags;
    public Random.State? RandomState;

    // Clock at create time (the snapshot holds absolute-time/-frame values).
    public float? GameTime;
    public int? GameFrameCount;

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

/*
public record GameObjectData(bool Active);

public class GameObjectSnapshot {
    public required string Path;
    public required GameObjectData Data;

    public static GameObjectSnapshot Of(GameObject go) => new() {
        Path = ObjectUtils.ObjectPath(go),
        Data = new GameObjectData(go.activeSelf),
    };

    public bool Restore() {
        var targetGo = ObjectUtils.LookupPath(Path);
        if (!targetGo) {
            Log.Error($"Savestate stored state on {Path}, which does not exist at load time");
            return false;
        }

        targetGo!.SetActive(Data.Active);

        return true;
    }
}
*/

public class ComponentSnapshot {
    public required string Path;
    public required JToken Data;

    public static ComponentSnapshot Of(Component mb) {
        return new ComponentSnapshot {
            Path = ObjectUtils.ObjectComponentPath(mb),
            Data = SnapshotSerializer.Snapshot(mb),
        };
    }

    public bool Restore() {
        var targetComponent = ObjectUtils.LookupObjectComponentPath(Path);
        if (!targetComponent) {
            Log.Error($"Savestate stored state on {Path}, which does not exist at load time");
            return false;
        }

        SnapshotSerializer.Populate(targetComponent, Data);

        return true;
    }
}

// Snapshots a PlayMaker FSM's runtime state: active state, variable values, and per-action runtime fields
// (timers etc.). Restored by directly setting the active state (no OnEnter actions), populating live variable
// and action instances in-place — the FSM definition (states/actions arrays) comes from the prefab and is
// matched by index/name, never re-created (which would break action<->variable wiring).
public class PlayMakerFsmSnapshot {
    public required string Path;
    public required string FsmName;
    public required string ActiveState;
    public required Dictionary<string, JToken> Variables;
    public required List<FsmActionSnapshot> Actions;

    // variable types whose value can be serialized by value; refs (GameObject/Object/Material/Texture) and
    // complex containers (Array) are skipped — they're prefab-wired, not runtime state.
    private static bool IsSerializableVariable(VariableType type) {
        return type is VariableType.Float or VariableType.Int
            or VariableType.Bool or VariableType.String or VariableType.Vector2 or VariableType.Vector3
            or VariableType.Color or VariableType.Rect or VariableType.Quaternion or VariableType.Enum;
    }

    public static PlayMakerFsmSnapshot Of(PlayMakerFSM fsmComponent) {
        var fsm = fsmComponent.Fsm;

        var variables = new Dictionary<string, JToken>();
        foreach (var v in fsm.Variables.GetAllNamedVariables()) {
            if (!IsSerializableVariable(v.VariableType)) {
                continue;
            }

            object? raw;
            try {
                raw = v.RawValue;
            } catch (Exception e) {
                Log.Warning($"Could not read FSM variable {fsm.Name}.{v.Name}: {e.Message}");
                continue;
            }

            variables[v.Name] = raw == null ? JValue.CreateNull() : SnapshotSerializer.Snapshot(raw);
        }

        // only the active state has actions mid-execution with meaningful runtime state (timers etc.); actions in
        // other states are re-initialized by OnEnter when next entered, so capturing them is just bloat.
        var actions = new List<FsmActionSnapshot>();
        var states = fsm.States;
        var activeStateIndex = Array.FindIndex(states, s => s.Name == fsm.ActiveStateName);
        if (activeStateIndex >= 0) {
            var stateActions = states[activeStateIndex].Actions;
            for (var ai = 0; ai < stateActions.Length; ai++) {
                actions.Add(new FsmActionSnapshot {
                    StateIndex = activeStateIndex,
                    ActionIndex = ai,
                    Data = SnapshotSerializer.Snapshot(stateActions[ai]),
                });
            }
        }

        return new PlayMakerFsmSnapshot {
            Path = ObjectUtils.ObjectPath(fsmComponent.gameObject),
            FsmName = fsm.Name,
            ActiveState = fsm.ActiveStateName,
            Variables = variables,
            Actions = actions,
        };
    }

    public bool Restore() {
        var go = ObjectUtils.LookupPath(Path);
        if (!go) {
            Log.Error($"Savestate stored FSM state on {Path}, which does not exist at load time");
            return false;
        }

        // multiple PlayMakerFSMs can live on one GameObject, so disambiguate by FsmName
        var fsmComponent = go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.Fsm.Name == FsmName);
        if (fsmComponent == null) {
            Log.Error($"Savestate stored FSM '{FsmName}' on {Path}, which has no such PlayMakerFSM");
            return false;
        }

        var fsm = fsmComponent.Fsm;

        // set variables by name into the live (prefab-wired) instances
        foreach (var v in fsm.Variables.GetAllNamedVariables()) {
            if (!Variables.TryGetValue(v.Name, out var tok)) {
                continue;
            }

            try {
                if (tok.Type == JTokenType.Null) {
                    v.RawValue = null;
                } else {
                    var targetType = v.RawValue?.GetType();
                    v.RawValue = targetType != null
                        ? SnapshotSerializer.Deserialize(tok, targetType)
                        : tok.ToObject<object>();
                }
            } catch (Exception e) {
                Log.Warning($"Could not restore FSM variable {FsmName}.{v.Name}: {e.Message}");
            }
        }

        // populate action runtime fields (timers etc.) in-place, matched by index against the prefab definition
        var states = fsm.States;
        foreach (var a in Actions) {
            if (a.StateIndex >= states.Length) {
                continue;
            }

            var stateActions = states[a.StateIndex].Actions;
            if (a.ActionIndex >= stateActions.Length) {
                continue;
            }

            SnapshotSerializer.Populate(stateActions[a.ActionIndex], a.Data);
        }

        // restore the active state directly, without re-running OnEnter actions: Fsm.Update only calls Continue()
        // (which enters the state) when !activeStateEntered, so setting the flag makes it resume UpdateState instead.
        var targetState = fsm.GetState(ActiveState);
        if (targetState == null) {
            Log.Warning($"FSM {FsmName} has no state '{ActiveState}', leaving active state unchanged");
        } else {
            fsm.SetFieldValue("activeState", targetState);
            fsm.SetFieldValue("activeStateName", ActiveState);
            fsm.SetFieldValue("activeStateEntered", true);
        }

        return true;
    }
}

public class FsmActionSnapshot {
    public required int StateIndex;
    public required int ActionIndex;
    public required JToken Data;
}

// RandomAudioClipTable is a shared ScriptableObject asset, so its runtime selection state is session-global and
// survives a scene load — and the recursive component snapshot ignores ScriptableObjects. We capture the three
// runtime fields explicitly because the table draws from the global UnityEngine.Random, so leftover state from
// before the load makes a TAS non-deterministic (different clip/weight → divergent Random.Range). Keyed by asset
// name and matched back against the same HeroController tables on load.
public class RandomAudioTableSnapshot {
    public required string Name;
    public float[]? CurrentProbabilities;     // the "fair selection" weight accumulator
    public string? PreviousClip;              // previousClip by name, resolved against the table's clips on load
    public double NextPlayTime;               // cooldown gate

    public static RandomAudioTableSnapshot Of(RandomAudioClipTable table) {
        var previousClip = table.GetFieldValue<AudioClip>("previousClip");
        return new RandomAudioTableSnapshot {
            Name = table.name,
            CurrentProbabilities = table.GetFieldValue<float[]>("currentProbabilities"),
            PreviousClip = previousClip ? previousClip!.name : null,
            NextPlayTime = table.GetFieldValue<double>("nextPlayTime"),
        };
    }

    public void Restore(RandomAudioClipTable table) {
        table.SetFieldValue("currentProbabilities", CurrentProbabilities);
        table.SetFieldValue("nextPlayTime", NextPlayTime);
        table.SetFieldValue("previousClip", PreviousClip != null ? FindClip(table, PreviousClip) : null);
    }

    // AudioClip has no stable global id at runtime (GUID is editor/Addressables-only, InstanceID isn't stable).
    // But previousClip is always one of the table's own clips, so resolve it by name within clips[].
    private static AudioClip? FindClip(RandomAudioClipTable table, string clipName) {
        if (table.GetFieldValue<IEnumerable>("clips") is not { } clips) {
            return null;
        }

        foreach (var c in clips) {
            if (c.GetFieldValue<AudioClip>("Clip") is { } clip && clip && clip.name == clipName) {
                return clip;
            }
        }

        return null;
    }
}

/*
public class GeneralFsmSnapshot {
    public required string Path;
    public required string CurrentState;

    public static GeneralFsmSnapshot Of(StateMachineOwner owner) => new() {
        Path = ObjectUtils.ObjectPath(owner.gameObject),
        CurrentState = owner.FsmContext.fsm.State.name,
    };
}

public record ReferenceFixupField(string Field, string? Reference);

public class ReferenceFixups {
    public required string Path;
    public required List<ReferenceFixupField> Fields;

    public static ReferenceFixups Of(MonoBehaviour mb, List<ReferenceFixupField> fixups) => new() {
        Path = ObjectUtils.ObjectComponentPath(mb),
        Fields = fixups,
    };
}
*/
