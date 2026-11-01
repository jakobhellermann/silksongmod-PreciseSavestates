using System.Collections.Generic;
using HarmonyLib;
using PreciseSavestates.Utils;
using UnityEngine.AddressableAssets;

namespace PreciseSavestates.Savestates.Game;

/// Vanilla Cleanup unloads by op.Address, an Addressables key SceneManager can't resolve, leaving a duplicate room.
[HarmonyPatch]
internal static class ScenePreloaderCleanupFix {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScenePreloader), nameof(ScenePreloader.Cleanup))]
    private static bool Cleanup() {
        var ops = typeof(ScenePreloader).GetFieldValue<List<ScenePreloader.SceneLoadOp>>("_forceEndedOperations");
        if (ops == null) {
            return false;
        }

        foreach (var op in ops) {
            Addressables.UnloadSceneAsync(op.Operation);
        }
        ops.Clear();
        return false;
    }
}
