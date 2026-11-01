using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace PreciseSavestates.Savestates.Game;

// `PreloadAdditiveScenes` already loaded the scene. So SceneAdditiveLoadConditional finds the scene already loaded,
// and never sets loadOp -> cannot `Unload` it later.
// Inject the unload handle from the preload manually instead.
internal static class AdditiveScenePreload {
    internal static readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> PendingUnloadHandles = new();

    [HarmonyPatch(typeof(SceneAdditiveLoadConditional), nameof(SceneAdditiveLoadConditional.Start))]
    private static class InjectPreloadedLoadOp {
        // ReSharper disable once InconsistentNaming
        private static void Postfix(SceneAdditiveLoadConditional __instance) {
            if (!__instance.sceneLoaded) {
                return;
            }

            if (PendingUnloadHandles.TryGetValue(__instance.SceneNameToLoad, out var handle)) {
                __instance.loadOp = handle;
                PendingUnloadHandles.Remove(__instance.SceneNameToLoad);
            }
        }
    }
}
