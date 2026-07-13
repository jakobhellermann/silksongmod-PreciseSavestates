using System.Collections.Generic;
using HarmonyLib;
using PreciseSavestates.Utils;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace PreciseSavestates.Savestates.Game;

// `PreloadAdditiveScenes` already loaded the scene. So SceneAdditiveLoadConditional finds the scene already loaded,
// and never sets loadOp -> cannot `Unload` it later.
// Inject the unload handle from the preload manually instead.
internal static class AdditiveScenePreload {
    internal static readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> PendingUnloadHandles = new();

    [HarmonyPatch(typeof(SceneAdditiveLoadConditional), "Start")]
    private static class InjectPreloadedLoadOp {
#pragma warning disable HARMONIZE001
        // ReSharper disable once InconsistentNaming
        private static void Postfix(SceneAdditiveLoadConditional __instance) {
#pragma warning restore HARMONIZE001
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
