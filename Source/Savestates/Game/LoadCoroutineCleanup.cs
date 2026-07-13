using System;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

internal static class LoadCoroutineCleanup {
    public static void Run() {
        StopHeroDeathCoroutines();
        ClearDeathVisuals();
    }

    private static void StopHeroDeathCoroutines() {
        var hero = HeroController.UnsafeInstance;
        if (!hero) {
            return;
        }

        foreach (var routine in new[] { "hazardRespawnRoutine", "hazardInvulnRoutine" }) {
            if (hero.GetFieldValue<Coroutine>(routine) is { } coro) {
                hero.StopCoroutine(coro);
            }
        }
    }

    private static void ClearDeathVisuals() {
        var hero = HeroController.UnsafeInstance;
        if (hero) {
            var invPulse = hero.GetFieldValue<InvulnerablePulse>("invPulse");
            if (invPulse) {
                invPulse.StopInvulnerablePulse();
            }

            if (hero.SpriteFlash) {
                hero.SpriteFlash.CancelFlash();
            }
        }

        var vignette = typeof(StatusVignette).GetFieldValue<StatusVignette>("_instance");
        if (vignette) {
            foreach (var field in new[] { "fadeRoutines", "tempFadeRoutines" }) {
                if (vignette.GetFieldValue<Coroutine?[]>(field) is { } routines) {
                    foreach (var routine in routines) {
                        if (routine != null) {
                            vignette.StopCoroutine(routine);
                        }
                    }
                }
            }

            if (vignette.GetFieldValue<Animator[]>("vignettes") is { } vignettes) {
                foreach (var animator in vignettes) {
                    if (animator) {
                        animator.gameObject.SetActive(false);
                    }
                }
            }

            if (vignette.GetFieldValue<Array>("mixTValues") is { } mixTValues) {
                Array.Clear(mixTValues, 0, mixTValues.Length);
            }
        }

        // SendEventSafe is a no-op if the fader is already clear. The two faders use different event names:
        // GameManager's screenFader_fsm takes "SCENE FADE IN", the camera's fadeFSM "FADE SCENE IN".
        var gm = GameManager.instance;
        if (gm.screenFader_fsm) {
            gm.screenFader_fsm.SendEventSafe("SCENE FADE IN");
        }

        if (gm.cameraCtrl && gm.cameraCtrl.GetFieldValue<PlayMakerFSM>("fadeFSM") is { } fadeFsm) {
            fadeFsm.SendEventSafe("FADE SCENE IN");
        }
    }

    // HazardRespawn has no stored handle, so a MoveNext bail is the owner-agnostic way to stop it. 
    [HarmonyPatch]
    [PublicAPI]
    private static class HazardRespawnAbort {
        private static MethodBase TargetMethod() =>
            AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(HeroController), nameof(HeroController.HazardRespawn)));

        private static bool Prefix(ref bool __result) {
            if (!SavestateLogic.IsLoadingSavestate) {
                return true;
            }

            __result = false;
            return false;
        }
    }
}
