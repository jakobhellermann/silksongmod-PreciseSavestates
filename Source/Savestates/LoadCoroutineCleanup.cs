using System;
using System.Reflection;
using HarmonyLib;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates;

/// A savestate captures serializable fields, but a running coroutine has no serializable form — so any transient
/// sequence a load lands in the middle of (the hero's death → respawn → invulnerability chain and the screen/sprite
/// effects it drives) can't be round-tripped through a snapshot. This class owns that residue: <see cref="Run"/> is
/// called at the start of every load (a no-op when nothing is in flight), cancelling the in-flight coroutines and
/// resetting the effects they would otherwise leave stuck — screen fade, status vignette, invulnerability sprite
/// flash. New non-snapshottable load cleanup belongs here, not scattered through SavestateLogic.
internal static class LoadCoroutineCleanup {
    public static void Run() {
        StopHeroDeathCoroutines();
        ClearDeathVisuals();
    }

    // Stop the hero's in-flight hazard death/respawn coroutines. They park on WaitForSeconds — so the MoveNext-based
    // abort below can't reach them — and would otherwise wake after the load and drive a stale respawn on the restored
    // hero. Stop them via the hero's own stored handles, the way DebugMod does; the handle-less, scene-owned
    // HazardRespawn is covered by HazardRespawnAbort plus the scene reload.
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

    // Reset the visual effects a mid-death load would leave stuck. All three are driven by running coroutines / FSMs,
    // not by serializable fields, so a snapshot can't restore them:
    //  - the invulnerability sprite flash: HeroController.Invulnerable drives InvulnerablePulse, whose Flash()
    //    coroutine loops forever re-triggering SpriteFlash until the invuln window ends. Stop the pulse loop and
    //    CancelFlash the SpriteFlash — the latter also resets the _FlashAmount material block to 0 (no residual tint).
    //  - the StatusVignette overlay: fade coroutines + per-vignette alpha, otherwise left darkened.
    //  - the full-screen fader (camera fadeFSM + GameManager screenFader_fsm, sent "HAZARD FADE"): the dreamGate load
    //    never fades it back in, so it stays black — fade it in explicitly.
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
                if (vignette.GetFieldValue<Coroutine[]>(field) is { } routines) {
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

    // HazardRespawn has no stored handle (it's StartCoroutine'd on the hazard's KillOnContact object — "Bounds Cage" —
    // not on a hero field we can reach), so a MoveNext bail is the owner-agnostic way to stop it. Returning false from
    // a MoveNext prefix (with __result = false) reports the coroutine as finished, so Unity stops driving it —
    // equivalent to a StopCoroutine but without needing its handle/owner. Same pattern as CelesteTAS's
    // RandomLoopIsolation. Doesn't reach coroutines parked on WaitForSeconds (their MoveNext isn't called during the
    // load) — those are the handle-bearing ones StopHeroDeathCoroutines stops directly.
    [HarmonyPatch]
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
