using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PreciseSavestates.Savestates.Game;

internal static class LoadCoroutineCleanup {
    public static void Run() {
        StopHeroDeathCoroutines();
        StopPendingScenePreloads();
        ClearDeathVisuals();
    }

    // Unity activates scene loads in queue order and SceneLoad activates preloads first, so one queued after ours deadlocks
    public static async Task WaitForPendingScenePreloads() {
        while (ScenePreloader.GetOperations().Any(op => !op.Operation.IsDone)) {
            await Task.Yield();
        }
    }

    // A started preload issues its load a frame later, i.e. after ours (deadlock, see above); LoadRoutine is its only coroutine
    private static void StopPendingScenePreloads() {
        foreach (var preloader in Object.FindObjectsByType<ScenePreloader>(FindObjectsSortMode.None)) {
            preloader.StopAllCoroutines();
        }
    }

    private static void StopHeroDeathCoroutines() {
        var hero = HeroController.UnsafeInstance;
        if (!hero) {
            return;
        }

        foreach (var routine in new[] { hero.hazardRespawnRoutine, hero.hazardInvulnRoutine }) {
            if (routine != null) {
                hero.StopCoroutine(routine);
            }
        }

        // iframes don't work when the coroutine is still set
        hero.hazardRespawnRoutine = null;
        hero.hazardInvulnRoutine = null;

        foreach (var routine in DeathCoroutines.Running) {
            hero.StopCoroutine(routine);
        }
        DeathCoroutines.Running.Clear();
    }

    [HarmonyPatch]
    [PublicAPI]
    private static class DeathCoroutines {
        public static readonly List<IEnumerator> Running = [];

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.Die))]
        private static void Die(IEnumerator __result) => Running.Add(__result);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.PlayerDead))]
        private static void PlayerDead(IEnumerator __result) => Running.Add(__result);
    }

    private static void ClearDeathVisuals() {
        var hero = HeroController.UnsafeInstance;
        if (hero) {
            var invPulse = hero.invPulse;
            if (invPulse) {
                invPulse.StopInvulnerablePulse();
            }

            if (hero.SpriteFlash) {
                hero.SpriteFlash.CancelFlash();
            }
        }

        var vignette = StatusVignette._instance;
        if (vignette) {
            foreach (var routines in new[] { vignette.fadeRoutines, vignette.tempFadeRoutines }) {
                foreach (var routine in routines) {
                    if (routine != null) {
                        vignette.StopCoroutine(routine);
                    }
                }
            }

            foreach (var animator in vignette.vignettes) {
                if (animator) {
                    animator.gameObject.SetActive(false);
                }
            }

            Array.Clear(vignette.mixTValues, 0, vignette.mixTValues.Length);
        }

        // death slides the HUD out
        GameCameras.instance.HUDIn();

        // SendEventSafe is a no-op if the fader is already clear. The two faders use different event names:
        // GameManager's screenFader_fsm takes "SCENE FADE IN", the camera's fadeFSM "FADE SCENE IN".
        var gm = GameManager.instance;
        if (gm.screenFader_fsm) {
            gm.screenFader_fsm.SendEventSafe("SCENE FADE IN");
        }

        if (gm.cameraCtrl && gm.cameraCtrl.fadeFSM) {
            gm.cameraCtrl.fadeFSM.SendEventSafe("FADE SCENE IN");
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
