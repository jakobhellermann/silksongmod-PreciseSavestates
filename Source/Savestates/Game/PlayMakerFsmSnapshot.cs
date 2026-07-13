using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Savestates.Snapshot;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// TODO: review the file

// Snapshots a PlayMaker FSM's runtime state: active state, variable values, and per-action runtime fields
// (timers etc.). Restored by directly setting the active state (no OnEnter actions), populating live variable
// and action instances in-place — the FSM definition (states/actions arrays) comes from the prefab and is
// matched by index/name, never re-created (which would break action<->variable wiring).
[PublicAPI]
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

    public Dictionary<string, string?>? ObjectVariables;

    // Actions whose OnEnter is re-run on restore (see Restore): their OnEnter establishes live state that lives
    // *outside* the FSM and so can't be snapshotted — a subscription/callback on another object — and that re-running
    // is idempotent (no SendEvent/spawn/... to double). Trigger2dEvent(Layer) register a hero-detection callback on a
    // PlayMakerProxy on the target GameObject (e.g. the Cog_Dancers boss's "Start Range" wake trigger). Extend this
    // list as other external-registration actions surface (ReceivedDamage, CheckAlertRange, …) — only after checking
    // the action is genuinely idempotent.
    private static readonly HashSet<Type> reArmOnRestore = [
        typeof(Trigger2dEvent),
        typeof(Trigger2dEventLayer),
        typeof(SendTrigger2DEvent),
        typeof(Collision2dEventLayer),
        // ReceivedDamageBase.OnEnter GetOrAdds a ReceivedDamageProxy component on the target and registers this action
        // as a handler — a live registration on a dynamically-added component that no serialized field restores (the
        // proxy component doesn't exist on the freshly-loaded object). Base type: covers all ReceivedDamage* subclasses.
        typeof(ReceivedDamageBase),
        // FaceObjectV2 / GetAngleToTarget2D cache a resolved GameObject in OnEnter (objectA_object / self, from an
        // FsmOwnerDefault) and dereference it every OnUpdate. Restoring an enemy AI FSM mid-state without re-running
        // OnEnter leaves that cache null, so their OnUpdate NREs every frame after a savestate load. Both OnEnter are
        // idempotent (cache + a facing / angle computation, no SendEvent/spawn), so re-running just re-establishes the
        // cache.
        typeof(FaceObjectV2),
        typeof(GetAngleToTarget2D),
        // ChaseObjectGround (enemy ground-chase AI): OnEnter caches the rigidbody / self / animator from an
        // FsmOwnerDefault, then DoChase() every OnFixedUpdate dereferences them. Same restore-without-OnEnter NRE.
        // OnEnter is idempotent (cache + a DoChase that only sets chase velocity, which OnFixedUpdate would set anyway).
        typeof(ChaseObjectGround),
    ];

    // EaseFsmAction.OnEnter builds an `ease` *delegate* (via SetEasingFunction) from the serialized easeType — a
    // non-serializable derived value. When we resume an active Ease action's OnUpdate (see the ActiveActions rebuild
    // in Restore) without having run OnEnter, `ease` is null and OnUpdate NREs invoking it. Rebuild it directly; it's
    // a pure function of easeType with no side effects and doesn't touch the eased progress (which is serialized).
    private static readonly MethodInfo? easeSetEasingFunction =
        typeof(EaseFsmAction).GetMethod("SetEasingFunction", BindingFlags.NonPublic | BindingFlags.Instance);

    // Safely ignorable unsave fsm variables
    private static readonly HashSet<(string Fsm, string Variable)> dropWarnSilenced = [
        ("Bind", "Bind Voice Clip"),
        ("Sprint", "Clip"),
        ("Superjump", "Clip"),
        ("Silk Specials", "Voice"),
        ("Tool Attacks", "Get Item"),
        ("Chest Control", "Hit Sound"),
        ("Inspection", "End Audio Snapshot"),
        ("Inspection", "End Enviro Snapshot"),
        ("pilgrim_behaviour", "Audio Table Idle"),
        ("pilgrim_behaviour", "Audio Table Alert"),
        ("pilgrim_behaviour", "Audio Table Sing"),
        ("Control", "Route Points"),
        ("Control", "Clip"),
        // static authored sprite-variant array, only read via ArrayGetRandom for a cosmetic random pick (Sprite Crt);
        // no Control FSM writes an uncapturable Sprites (Bone_Boulder's Sprites is a FindChild GameObject → captured)
        ("Control", "Sprites"),
        ("Sway", "Ambient Chain Audio"),
        ("Detect Landing", "Land Audio Table"),
        ("Detect Landing", "Land Audio Table 2"),
        ("Detect Landing", "Tink Audio Table"),
    ];

    private static bool IsSerializableVariable(VariableType type) {
        return type is VariableType.Float or VariableType.Int
            or VariableType.Bool or VariableType.String or VariableType.Vector2 or VariableType.Vector3
            or VariableType.Color or VariableType.Rect or VariableType.Quaternion or VariableType.Enum;
    }

    public static PlayMakerFsmSnapshot Of(PlayMakerFSM fsmComponent) {
        var fsm = fsmComponent.Fsm;

        var variables = new Dictionary<string, JToken>();
        var gameObjectVariables = new Dictionary<string, string?>();
        var objectVariables = new Dictionary<string, string?>();
        foreach (var v in fsm.Variables.GetAllNamedVariables()) {
            if (IsSerializableVariable(v.VariableType)) {
                object? raw;
                try {
                    raw = v.RawValue;
                } catch (Exception e) {
                    Log.Warning($"Could not read FSM variable {fsm.Name}.{v.Name}: {e.Message}");
                    continue;
                }

                variables[v.Name] = raw == null ? JValue.CreateNull() : SnapshotSerializer.Snapshot(raw);
            } else if (v.VariableType == VariableType.GameObject) {
                GameObject? target;
                try {
                    target = v.RawValue as GameObject;
                } catch (Exception e) {
                    Log.Warning($"Could not read FSM GameObject variable {fsm.Name}.{v.Name}: {e.Message}");
                    continue;
                }

                gameObjectVariables[v.Name] = target ? ObjectUtils.ObjectPath(target) : null;
            } else if (v.VariableType == VariableType.Object) {
                // FsmObject.Value is always a UnityEngine.Object
                var obj = v.RawValue as UnityEngine.Object;
                if (!obj) {
                    objectVariables[v.Name] = null;
                } else if (obj is Component comp) {
                    objectVariables[v.Name] = ObjectUtils.ObjectComponentPath(comp);
                } else if (!dropWarnSilenced.Contains((fsm.Name, v.Name))) {
                    Log.Warning($"Not capturing FSM asset variable {ObjectUtils.ObjectPath(fsmComponent.gameObject)}/"
                                + $"{fsm.Name}.{v.Name} ({obj.GetType().Name})");
                }
            } else if (!dropWarnSilenced.Contains((fsm.Name, v.Name))) {
                Log.Warning($"Not capturing FSM variable {ObjectUtils.ObjectPath(fsmComponent.gameObject)}/"
                            + $"{fsm.Name}.{v.Name} ({v.VariableType})");
            }
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
            ObjectVariables = objectVariables,
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

        if (ObjectVariables != null) {
            foreach (var v in fsm.Variables.GetAllNamedVariables()) {
                if (v.VariableType != VariableType.Object ||
                    !ObjectVariables.TryGetValue(v.Name, out var path)) {
                    continue;
                }

                if (path == null) {
                    v.RawValue = null;
                } else if (ObjectUtils.LookupObjectComponentPath(path) is { } resolved) {
                    v.RawValue = resolved;
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
            if (action is { Active: true, Finished: false }) {
                activeActions.Add(action);

                // Reconstruct OnEnter-computed, non-serializable state the action's OnUpdate needs. Ease actions
                // rebuild their `ease` delegate from easeType (else OnUpdate NREs on the null delegate).
                if (action is EaseFsmAction) {
                    easeSetEasingFunction?.Invoke(action, null);
                }

                // The tk2d animation-event actions whose OnEnter subscribes the animator's AnimationEventTriggered/
                // AnimationCompleted delegate and caches a private `_sprite` — the exact members RewireTk2dAnimationEvents
                // re-establishes. Explicit list (not a member-shape probe) so it's clear which actions are handled.
                // (Tk2dPlayAnimationWait, tk2dPlayAnimAfterPreviousComplete, WaitTimeAndTk2dFrame, HeroTurnToFace also
                // subscribe but have a different member layout — not handled here; add them if a case surfaces.)
                if (action is Tk2dPlayAnimationWithEvents or Tk2dPlayAnimationWithEventsV2 or Tk2dPlayAnimationWithEventsV3
                    or Tk2dPlayRandomAnimationWithEvents
                    or Tk2dWatchAnimationEvents or Tk2dWatchAnimationEventsV2 or Tk2dWatchAnimationEventsV3) {
                    RewireTk2dAnimationEvents(action);
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
            if (!reArmOnRestore.Any(t => t.IsInstanceOfType(action))) {
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

    // tk2d animation-event actions (Play* / Watch* / Wait* families): OnEnter subscribes callbacks onto the animator's
    // AnimationEventTriggered / AnimationCompleted delegates, which send the FSM event that exits the state, and caches
    // _sprite that OnUpdate dereferences. Neither is serializable, so a restored animation state resumes the clip but
    // never fires the event / NREs on the null _sprite and hangs. Re-running OnEnter isn't general (some variants
    // re-play or re-randomize the clip); re-establish only the wiring — _sprite, optional expectedClip, and the two
    // delegates (each gated on its event, as OnEnter does). Never plays, so the converter-restored clip is preserved.
    private void RewireTk2dAnimationEvents(FsmStateAction action) {
        const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
        var type = action.GetType();
        try {
            // Re-resolve the animator into the private _sprite cache (from the action's FsmOwnerDefault gameObject).
            type.GetMethod("_getSprite", priv)?.Invoke(action, null);
            if (type.GetField("_sprite", priv)?.GetValue(action) is not tk2dSpriteAnimator sprite || !sprite) {
                return;
            }

            // OnUpdate derefs _sprite.CurrentClip and compares it to expectedClip; seed expectedClip to the current
            // clip so it neither NREs nor fires a spurious exit, and still detects the *next* clip change.
            type.GetField("expectedClip", priv)?.SetValue(action, sprite.CurrentClip);
            type.GetField("hasExpectedClip", priv)?.SetValue(action, sprite.CurrentClip != null);

            if (type.GetField("animationTriggerEvent", pub)?.GetValue(action) != null
                && type.GetMethod("AnimationEventDelegate", priv) is { } trig) {
                sprite.AnimationEventTriggered = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>)
                    Delegate.CreateDelegate(typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>), action, trig);
            }

            if (type.GetField("animationCompleteEvent", pub)?.GetValue(action) != null
                && type.GetMethod("AnimationCompleteDelegate", priv) is { } comp) {
                sprite.AnimationCompleted = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>)
                    Delegate.CreateDelegate(typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>), action, comp);
            }
        } catch (Exception e) {
            Log.Warning($"Could not rewire tk2d animation events for {type.Name} on {FsmName}: {e.Message}");
        }
    }
}

public class FsmActionSnapshot {
    public required int StateIndex;
    public required int ActionIndex;
    public required JToken Data;
}
