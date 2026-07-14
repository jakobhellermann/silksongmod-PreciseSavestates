using System;
using PreciseSavestates.Source;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// Refresh HUD canvas after health_display FSM update
public static class HudFixes {
    public static void RefreshHealthHud() {
        try {
            RefreshHealthHudInner();
        } catch (Exception e) {
            Log.Warning($"RefreshHealthHud failed: {e}");
        }
    }

    public static void RefreshSilkHud() {
        try {
            SilkSpool.Instance.RefreshSilk();
        } catch (Exception e) {
            Log.Warning($"RefreshSilkHud failed: {e}");
        }
    }

    private static void RefreshHealthHudInner() {
        var cameras = GameCameras.instance;
        if (!cameras || !cameras.hudCanvasSlideOut) {
            return;
        }

        var hudCanvas = cameras.hudCanvasSlideOut.transform;

        // The Health object may be inactive right after a load; find it by name rather than GameObject.Find.
        GameObject? health = null;
        for (var i = 0; i < hudCanvas.childCount; i++) {
            var child = hudCanvas.GetChild(i).gameObject;
            if (child.name == "Health") {
                health = child;
                break;
            }
        }

        if (!health) {
            Log.Warning("RefreshHealthHud: no 'Health' object under hudCanvasSlideOut");
            return;
        }

        // Reset the Low Health FX overlay (screen-edge vignette) so it re-evaluates against the restored HP.
        var lowHealthFx = health.LocateMyFSM("Low Health FX");
        if (lowHealthFx != null) {
            var health1 = lowHealthFx.FsmVariables.FindFsmGameObject("Health 1")?.Value;
            var h1InitialPos = lowHealthFx.FsmVariables.FindFsmVector3("H1 Initial Pos");
            if (health1 != null && h1InitialPos != null) {
                health1.transform.localPosition = h1InitialPos.Value;
            }

            lowHealthFx.SetState("Check Health");
        }

        // Re-initialize each mask's health_display FSM via silent init path.
        foreach (var fsm in health.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (fsm.FsmName != "health_display") {
                continue;
            }

            if (fsm.gameObject.activeSelf) {
                fsm.Fsm.OnEnable();
            } else {
                fsm.gameObject.SetActive(true);
            }

            fsm.FsmVariables.FindFsmBool("Initialised").Value = true;
            fsm.FsmVariables.FindFsmBool("Skip HUD Frame Wait").Value = true;
            fsm.SetState("Check Max HP");
        }
    }
}
