using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;

namespace PreciseSavestates.Savestates.Game;

/// Shifts game state holding absolute `Time.time` timestamps by a delta, so it stays valid when the clock it was
/// captured against differs from the current one (savestate from another session, or a TAS clock handing back to
/// the real clock).
public static class AbsoluteTimeRebase {
    private static readonly FieldInfo[] HeroFields = [
        Field(typeof(HeroController), "preventSoftLandTimer"),
        Field(typeof(HeroController), "noShuttlecockTime"),
        Field(typeof(HeroController), "canThrowTime"),
        Field(typeof(HeroController), "recoilAllowTime"),
        Field(typeof(HeroController), "lastHazardRespawnTime"),
        Field(typeof(HeroController), "HARPOON_DASH_TIME"),
    ];

    private static readonly FieldInfo TimeLimitRandomBoolTime = Field(typeof(TimeLimitRandomBool), "time");

    private static FieldInfo Field(Type type, string name) =>
        type.GetFieldInfo(name, ReflectionExtension.InstanceAnyVisibility) ?? throw new MissingFieldException(type.FullName, name);

    public static void RebaseHero(HeroController hero, double delta) {
        foreach (var field in HeroFields) {
            switch (field.GetValue(hero)) {
                case float f:
                    field.SetValue(hero, (float)(f + delta));
                    break;
                case double d:
                    field.SetValue(hero, d + delta);
                    break;
                default:
                    throw new InvalidOperationException($"{field.Name} is neither float nor double");
            }
        }
    }

    /// Shifts the named float variables any action stores `Time.time` into, plus such inline values in the
    /// active state (inline values of other states get rewritten on entry).
    public static void RebaseFsm(Fsm fsm, double delta) {
        var shifted = new HashSet<FsmFloat>();

        void Shift(FsmFloat? value) {
            if (value == null) return;

            var target = value.UsesVariable ? fsm.Variables.FindFsmFloat(value.Name) : value;
            if (target == null) {
                Log.Warning($"Not rebasing absolute time '{value.Name}' of FSM {fsm.Name}: not a local variable");
                return;
            }
            if (shifted.Add(target)) {
                target.Value = (float)(target.Value + delta);
            }
        }

        foreach (var state in fsm.States) {
            var isActive = state == fsm.ActiveState;
            foreach (var action in state.Actions) {
                if (AbsoluteTimeStore(action) is not { } store) {
                    if (isActive && action is TimeLimitRandomBool randomBool) {
                        var time = (float)TimeLimitRandomBoolTime.GetValue(randomBool);
                        TimeLimitRandomBoolTime.SetValue(randomBool, (float)(time + delta));
                    }
                    continue;
                }

                if (store.UsesVariable || isActive) {
                    Shift(store);
                }
            }
        }
    }

    public static void RebaseBreakables(IEnumerable<Breakable> breakables, double delta) {
        foreach (var breakable in breakables) {
            breakable.hitCooldownEndTime += delta;
        }
    }

    public static void RebaseCurveAnimators(IEnumerable<VectorCurveAnimator> animators, double delta) {
        foreach (var animator in animators) {
            animator.nextUpdateTime += delta;
        }
    }

    public static void RebaseFsms(IEnumerable<PlayMakerFSM> fsms, double delta) {
        foreach (var fsm in fsms.Where(fsm => fsm)) {
            RebaseFsm(fsm.Fsm, delta);
        }
    }

    /// Shifts all live absolute-time state of the hero and every running FSM.
    public static void RebaseAll(double delta) {
        Log.Info($"Rebasing absolute times by {delta:F3}s");
        if (HeroController.instance is { } hero) {
            RebaseHero(hero, delta);
        }
        RebaseFsms(PlayMakerFSM.FsmList, delta);
    }

    private static FsmFloat? AbsoluteTimeStore(FsmStateAction action) => action switch {
        TimeLimitSet a => a.storeValue,
        TimeLimitSetV2 a => a.StoreValue,
        TimeLimitSetRandom a => a.StoreValue,
        GetTimeInfo { getInfo: GetTimeInfo.TimeInfo.TimeSinceStartup } a => a.storeValue,
        _ => null,
    };
}
