using System.Collections.Generic;
using HarmonyLib;
using PreciseSavestates.Utils;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace PreciseSavestates.Savestates.Game;

// Fix leak in SceneAdditiveLoadConditional's static loader list. A loader adds itself to
// `_additiveSceneLoads` in Start and only removes itself in OnDisable->Unload when `sceneLoaded` is true, so a
// loader destroyed while its sub-scene is still loading leaks a destroyed entry.
// TODO: get rid of this?
[HarmonyPatch]
internal static class SceneAdditiveLoadConditionalPatch {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SceneAdditiveLoadConditional), "Unload",
        typeof(Scene), typeof(List<AsyncOperationHandle<SceneInstance>>))]
#pragma warning disable HARMONIZE003
    private static void PruneDestroyedLoaders() {
#pragma warning restore HARMONIZE003
        var loads = typeof(SceneAdditiveLoadConditional).GetFieldValue<List<SceneAdditiveLoadConditional>>("_additiveSceneLoads");
        loads?.RemoveAll(loader => loader == null);
    }
}
