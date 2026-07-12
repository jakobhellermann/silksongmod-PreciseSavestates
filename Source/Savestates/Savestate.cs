using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
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

    // Additive scenes loaded alongside the main Scene at capture time (boss arenas etc.). On load, playback is held
    // until exactly these are loaded again, so the async boss-arena load can't bleed into playback on a variable frame
    // (see SavestateLogic.AwaitAdditiveScenesLoaded).
    public List<string>? AdditiveScenes;

    public List<ComponentSnapshot>? ComponentSnapshots;

    public List<PlayMakerFsmSnapshot>? FsmSnapshots;

    public List<RandomAudioTableSnapshot>? AudioTableSnapshots;

    public List<GameObjectSnapshot>? GameObjectSnapshots;
    // public List<GeneralFsmSnapshot>? GeneralFsmSnapshots;
    // public JObject? Flags;
    public Random.State? RandomState;

    // Clock at create time (the snapshot holds absolute-time/-frame values).
    public float? GameTime;
    public int? GameFrameCount;

    // Full PlayerData (the player save-data singleton: abilities, flags like encounteredSongGolem, health, …),
    // captured via Unity serialization. It's a standalone singleton, not reachable as a Component by the recursive
    // snapshot, and scene FSMs read it during their Start on load — so it's captured as an explicit root and restored
    // mid-transition (before scene-object init), same as SceneData. See SavestateLogic.LoadInner.
    public JToken? PlayerData;

    // Full SceneData (per-scene persistent bool/int state: dead enemies, broken objects, opened gates, …), captured
    // via Unity's own serialization (JsonUtility — SceneData's PersistentItemDataCollection is an
    // ISerializationCallbackReceiver that flattens its dictionaries into a serializable list). It's a standalone
    // singleton not reachable from the hero object graph, so the recursive component snapshot misses it; captured as
    // an explicit root. Restored mid-transition — before the incoming scene's persistent objects read it in Start —
    // see SavestateLogic.LoadInner.
    public JToken? SceneData;

    // CustomPlayerLoop.FixedUpdateCycle at create time. A session-global monotonic counter used purely as a
    // cache-invalidation token by FixedUpdateCache.ShouldUpdate(). The recursive snapshot serializes those caches'
    // lastUpdate fields, so restoring the counter to its captured value keeps the restored caches consistent and
    // makes savestates byte-reproducible — otherwise the serialized lastUpdate values drift with session age.
    public int? FixedUpdateCycle;

    // Hazard-respawn point (spike/lava death → here). Captured explicitly, not via PlayerData: hazardRespawnLocation
    // is [NonSerialized], and FinishedEnteringScene re-derives both fields from transient hero state on our dreamGate
    // entry (non-deterministically). Re-applied after entry in ApplySnapshot so the captured value wins.
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
        NormalizeCenterOfMass(mb);
        return new ComponentSnapshot {
            Path = ObjectUtils.ObjectComponentPath(mb),
            Data = SnapshotSerializer.Snapshot(mb),
        };
    }

    // Box2D integrates a body's center of mass (sweep.c) internally and derives rb.position from it; we snapshot the
    // derived position, so setting it back on restore reconstructs sweep.c a couple ULP off the continuously-
    // integrated value — a resumed run then drifts ~1 ULP from a continuous one. Zeroing the center of mass makes
    // sweep.c == position, so snapshotting/restoring position is exact (no offset to round through). It is behavior-
    // neutral for a FreezeRotation body (it translates rigidly regardless of where its center of mass sits), so it
    // never needs restoring. Applied at capture (the continuing run must sit on the same footing as a resume) and at
    // restore (a freshly loaded body may still carry its auto center of mass).
    public static void NormalizeCenterOfMass(Component c) {
        if (c is Rigidbody2D { bodyType: RigidbodyType2D.Dynamic } rb &&
            rb.constraints.HasFlag(RigidbodyConstraints2D.FreezeRotation) &&
            rb.centerOfMass != Vector2.zero) {
            rb.useAutoMass = false;
            rb.centerOfMass = Vector2.zero;
        }
    }

    public bool Restore() {
        var targetComponent = ObjectUtils.LookupObjectComponentPath(Path);
        if (!targetComponent) {
            Log.Error($"Savestate stored state on {Path}, which does not exist at load time");
            return false;
        }

        NormalizeCenterOfMass(targetComponent);
        SnapshotSerializer.Populate(targetComponent, Data);

        return true;
    }
}

// GameObject-level state not reachable as a serialized component field: the physics layer (gameplay state — it
// decides what the object collides with) and the active flag. activeSelf is runtime state a load must restore:
// e.g. the Cog_Dancers boss's position markers (Pos1..12) are prefab-active and disabled *once* by the Dancer
// Control FSM's Init OnEnter; since the FSM restore reinstates a later active state without re-running OnEnter, a
// scene reload brings them back active — capturing/restoring activeSelf reinstates the disable.
public class GameObjectSnapshot {
    public required string Path;
    public int Layer;

    // Required on load: a savestate that predates this field must fail loudly at deserialization, not fall back to
    // the bool default (false) and silently deactivate the captured object — e.g. the hero, which then "falls through
    // / disappears". Missing data is an error here; if we ever want forward-compat, the default must be applied
    // explicitly (deliberately), never left to the implicit C# default.
    [JsonProperty(Required = Required.Always)]
    public bool Active;

    public static GameObjectSnapshot Of(GameObject go) => new() {
        Path = ObjectUtils.ObjectPath(go),
        Layer = go.layer,
        Active = go.activeSelf,
    };

    public bool Restore() {
        var go = ObjectUtils.LookupPath(Path);
        if (!go) {
            Log.Error($"Savestate stored GameObject state on {Path}, which does not exist at load time");
            return false;
        }

        go!.layer = Layer;
        if (go.activeSelf != Active) {
            go.SetActive(Active);
        }

        return true;
    }
}

// See Savestate.HazardRespawn.
public class HazardRespawnSnapshot {
    [JsonProperty(Required = Required.Always)]
    public Vector3 Location;

    [JsonProperty(Required = Required.Always)]
    public HazardRespawnMarker.FacingDirection Facing;

    public static HazardRespawnSnapshot Of(global::PlayerData pd) => new() {
        Location = pd.hazardRespawnLocation,
        Facing = pd.hazardRespawnFacing,
    };

    public void Restore() {
        var pd = global::PlayerData.instance;
        pd.hazardRespawnLocation = Location;
        pd.hazardRespawnFacing = Facing;
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

    // GameObject-typed FSM variables, captured by the referenced object's hierarchy path (InstanceIDs aren't stable
    // across a reload). Value-typed vars go in Variables; GameObject refs can't be serialized by value, but they hold
    // real runtime state — e.g. the Cog_Dancers dancers' "Next Pos"/"Current Pos" (the Pos marker a dash/jump tweens
    // to, set mid-fight by the sequence via SetFsmGameObject). Without them a resumed dancer's move target is null →
    // it plays the attack anim but doesn't translate. null value = the var was null at capture. (Non-scene / spawned
    // targets whose path doesn't resolve on load are left as-is — best effort.)
    public Dictionary<string, string?>? GameObjectVariables;

    // Actions whose OnEnter is re-run on restore (see Restore): their OnEnter establishes live state that lives
    // *outside* the FSM and so can't be snapshotted — a subscription/callback on another object — and that re-running
    // is idempotent (no SendEvent/spawn/... to double). Trigger2dEvent(Layer) register a hero-detection callback on a
    // PlayMakerProxy on the target GameObject (e.g. the Cog_Dancers boss's "Start Range" wake trigger). Extend this
    // list as other external-registration actions surface (ReceivedDamage, CheckAlertRange, …) — only after checking
    // the action is genuinely idempotent.
    private static readonly HashSet<Type> ReArmOnRestore = [
        typeof(Trigger2dEvent),
        typeof(Trigger2dEventLayer),
        // ReceivedDamageBase.OnEnter GetOrAdds a ReceivedDamageProxy component on the target and registers this action
        // as a handler — a live registration on a dynamically-added component that no serialized field restores (the
        // proxy component doesn't exist on the freshly-loaded object). Base type: covers all ReceivedDamage* subclasses.
        typeof(ReceivedDamageBase),
    ];

    // EaseFsmAction.OnEnter builds an `ease` *delegate* (via SetEasingFunction) from the serialized easeType — a
    // non-serializable derived value. When we resume an active Ease action's OnUpdate (see the ActiveActions rebuild
    // in Restore) without having run OnEnter, `ease` is null and OnUpdate NREs invoking it. Rebuild it directly; it's
    // a pure function of easeType with no side effects and doesn't touch the eased progress (which is serialized).
    private static readonly MethodInfo? EaseSetEasingFunction =
        typeof(EaseFsmAction).GetMethod("SetEasingFunction", BindingFlags.NonPublic | BindingFlags.Instance);

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

        // GameObject-typed vars: capture the referenced scene object's path (see GameObjectVariables).
        var gameObjectVariables = new Dictionary<string, string?>();
        foreach (var v in fsm.Variables.GetAllNamedVariables()) {
            if (v.VariableType != VariableType.GameObject) {
                continue;
            }

            GameObject? target;
            try {
                target = v.RawValue as GameObject;
            } catch (Exception e) {
                Log.Warning($"Could not read FSM GameObject variable {fsm.Name}.{v.Name}: {e.Message}");
                continue;
            }

            gameObjectVariables[v.Name] = target ? ObjectUtils.ObjectPath(target!) : null;
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
            GameObjectVariables = gameObjectVariables,
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

        // restore GameObject-typed vars by resolving the captured path (see GameObjectVariables)
        if (GameObjectVariables != null) {
            foreach (var v in fsm.Variables.GetAllNamedVariables()) {
                if (v.VariableType != VariableType.GameObject ||
                    !GameObjectVariables.TryGetValue(v.Name, out var path)) {
                    continue;
                }

                try {
                    // a null captured path means the var was null; a non-null path that no longer resolves is left
                    // as-is
                    if (path == null) {
                        v.RawValue = null;
                    } else if (ObjectUtils.LookupPath(path) is { } resolved) {
                        v.RawValue = resolved;
                    }
                } catch (Exception e) {
                    Log.Warning($"Could not restore FSM GameObject variable {FsmName}.{v.Name}: {e.Message}");
                }
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

        // Restore the active state directly, without re-running OnEnter actions: Fsm.Update only calls Continue()
        // (which enters the state) when !activeStateEntered, so setting the flag makes it resume UpdateState instead.
        // This preserves mid-execution runtime (timers etc.) and avoids OnEnter side effects (SendEvent/spawn/...).
        var targetState = fsm.GetState(ActiveState);
        if (targetState == null) {
            Log.Warning($"FSM {FsmName} has no state '{ActiveState}', leaving active state unchanged");
            return true;
        }

        fsm.SetFieldValue("activeState", targetState);
        fsm.SetFieldValue("activeStateName", ActiveState);
        fsm.SetFieldValue("activeStateEntered", true);

        // OnEnter also builds the state's ActiveActions list (via ActivateActions) — the list FsmState.OnUpdate
        // iterates to run per-frame actions. Skipping OnEnter leaves it empty, so a restored state runs NO OnUpdate:
        // Wait timers, everyFrame checks etc. freeze — e.g. Cog_Dancers' Beat Control Wait never advances, so the
        // boss never beats/attacks on resume. Rebuild the list directly from the actions that were active at capture;
        // Init() only wires refs (fsm/state/owner), it does not reset action runtime, so the captured timers survive.
        targetState.Fsm = fsm;
        var activeActions = targetState.ActiveActions;
        activeActions.Clear();
        foreach (var action in targetState.Actions) {
            if (!action.Enabled) {
                continue;
            }

            action.Init(targetState);
            action.Entered = true;
            if (action.Active && !action.Finished) {
                activeActions.Add(action);

                // Reconstruct OnEnter-computed, non-serializable state the action's OnUpdate needs. Ease actions
                // rebuild their `ease` delegate from easeType (else OnUpdate NREs on the null delegate).
                if (action is EaseFsmAction) {
                    EaseSetEasingFunction?.Invoke(action, null);
                }
            }
        }

        // Skipping OnEnter above loses the OnEnter effects that are *not* snapshottable: a few actions establish
        // live state external to the FSM (a subscription on another object) that no serialized field can capture —
        // e.g. Trigger2dEvent.OnEnter registers a callback on a PlayMakerProxy on *another* GameObject (Cog_Dancers'
        // "Start Range" → the boss's ENTER/wake trigger). Restoring the state without it leaves the trigger unarmed,
        // so the boss never wakes. Re-run OnEnter for an allowlist of such actions only — they must be idempotent
        // (re-running establishes the registration without doubling any SendEvent/spawn/... side effect). This
        // re-arms regardless of how far the fresh scene's own FSM init had progressed. (A registration set up in an
        // *earlier* state the FSM has since left is still unreachable — the known mid-fight replay gap.)
        //
        // Only re-arm the *active* actions (activeActions), not every action of the state: an inactive/finished
        // allowlisted action never ran OnEnter in a continuous run, so re-arming it would establish a registration
        // (proxy field) the continuous run doesn't have — a spurious resume diff. Match by base type so a family of
        // registration actions (ReceivedDamageBase subclasses, …) is covered without listing every subclass.
        foreach (var action in activeActions) {
            if (!ReArmOnRestore.Any(t => t.IsInstanceOfType(action))) {
                continue;
            }

            try {
                action.OnEnter();
            } catch (Exception e) {
                Log.Warning($"Could not re-arm {action.GetType().Name} on {FsmName}: {e.Message}");
            }
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
