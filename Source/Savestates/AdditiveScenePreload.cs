using System.Collections.Generic;
using HarmonyLib;
using PreciseSavestates.Utils;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace PreciseSavestates.Savestates;

/// A savestate load preloads the captured additive sub-scenes (boss arenas) itself via Addressables so they are
/// present and active at playback frame 0 (see SavestateLogic.PreloadAdditiveScenes). But when the scene's own
/// SceneAdditiveLoadConditional then starts, it finds the scene already loaded and takes its already-loaded shortcut,
/// which sets sceneLoaded=true but never sets `loadOp` — the handle its Unload() needs. Without it a later scene
/// transition removes the loader yet can't unload the sub-scene → it's orphaned (the HK "room dupe").
///
/// So hand the loader the Addressables handle from our preload: this patch, running after the loader's Start, injects
/// our handle into its `loadOp` for scenes we preloaded, so its normal Unload() path releases the sub-scene.
internal static class AdditiveScenePreload {
    /// Scene name → the Addressables handle from our preload, consumed by the Start postfix below.
    internal static readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> PendingUnloadHandles = new();

    [HarmonyPatch(typeof(SceneAdditiveLoadConditional), "Start")]
    private static class InjectPreloadedLoadOp {
#pragma warning disable HARMONIZE001
        private static void Postfix(SceneAdditiveLoadConditional __instance) {
#pragma warning restore HARMONIZE001
            // Only the already-loaded shortcut leaves sceneLoaded=true with no loadOp; the LoadRoutine path sets its
            // own loadOp (and hasn't finished — sceneLoaded is still false — at this point).
            if (!__instance.GetFieldValue<bool>("sceneLoaded")) {
                return;
            }

            if (__instance.GetPropertyValue<string>("SceneNameToLoad") is { } name
                && PendingUnloadHandles.TryGetValue(name, out var handle)) {
                __instance.SetFieldValue("loadOp", (AsyncOperationHandle<SceneInstance>?)handle);
                PendingUnloadHandles.Remove(name);
            }
        }
    }
}
