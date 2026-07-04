using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace PreciseSavestates.Savestates;

/// Guards against a leak in SceneAdditiveLoadConditional's static loader list. A loader adds itself to
/// `_additiveSceneLoads` in Start and only removes itself in OnDisable->Unload when `sceneLoaded` is true — so a
/// loader destroyed while its sub-scene is still loading (async LoadRoutine, sceneLoaded==false) leaks a destroyed
/// entry. The static Unload(Scene, ...) that SceneLoad runs on every transition then dereferences the destroyed
/// loader's gameObject and throws, aborting the transition (hero stranded in EXITING_SCENE, black screen). Rapid
/// savestate loads reload the scene often enough to hit this. Drop destroyed entries before that Unload iterates.
[HarmonyPatch(typeof(SceneAdditiveLoadConditional), "Unload",
    typeof(Scene), typeof(List<AsyncOperationHandle<SceneInstance>>))]
internal static class SceneAdditiveLoadConditionalPatch {
    private static readonly FieldInfo? AdditiveSceneLoads =
        AccessTools.Field(typeof(SceneAdditiveLoadConditional), "_additiveSceneLoads");

    [HarmonyPrefix]
    private static void PruneDestroyedLoaders() {
        if (AdditiveSceneLoads?.GetValue(null) is List<SceneAdditiveLoadConditional> loads) {
            loads.RemoveAll(loader => loader == null);
        }
    }
}
