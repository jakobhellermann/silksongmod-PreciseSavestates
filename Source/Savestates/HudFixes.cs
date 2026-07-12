using System;
using PreciseSavestates.Source;
using UnityEngine;

namespace PreciseSavestates.Savestates;

/// After a savestate load the HUD canvas persists (it's DontDestroyOnLoad), so its health_display FSMs keep the
/// pre-load state and never re-read the restored PlayerData.health — the mask display goes stale. Force each
/// health_display FSM back through its init path ("Check Max HP"), which snaps the masks to the correct sprite
/// *silently*: the empty branch just plays the static "Anim Empty" clip, with no break sound and no "DAMAGE TAKEN"
/// event (unlike firing "HEALTH UPDATE", which routes whole-but-should-be-empty masks through the audible Break?
/// state). Mirrors DebugMod's HudHelper.RefreshMasks.
public static class HudFixes {
    public static void RefreshHealthHud() {
        // A HUD quirk must never abort the load itself — this is a cosmetic fix.
        try {
            RefreshHealthHudInner();
        } catch (Exception e) {
            Log.Warning($"RefreshHealthHud failed: {e}");
        }
    }

    private static void RefreshHealthHudInner() {
        var cameras = GameCameras.instance;
        if (cameras == null || cameras.hudCanvasSlideOut == null) {
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

        if (health == null) {
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

        // Re-initialise each mask's health_display FSM via its clean, silent init path.
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
