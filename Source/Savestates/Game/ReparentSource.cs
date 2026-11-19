using System.Collections.Generic;
using HarmonyLib;
using PreciseSavestates.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PreciseSavestates.Savestates.Game;

// Where an object the game moved in the hierarchy at runtime originally lived, so a load can move it again. Exactly one
// option is set
public sealed class ReparentSource {
    public FlingChildrenReparent? FlingChildren;

    public GameObject? Apply() => FlingChildren?.Apply();
}

// FlingChildrenOnStart detaches its object to the scene root before flinging the children
public sealed class FlingChildrenReparent {
    public required string OriginalPath;

    public static IEnumerable<(GameObject detached, FlingChildrenReparent reparent)> In(Scene scene) {
        foreach (var root in scene.GetRootGameObjects()) {
            if (root.TryGetComponent<FlingChildrenOnStart>(out var fling) && fling.parentParent) {
                yield return (root, new FlingChildrenReparent {
                    OriginalPath = $"{ObjectUtils.ObjectPath(fling.parentParent.gameObject)}/{root.name}",
                });
            }
        }
    }

    public GameObject? Apply() {
        var go = ObjectUtils.LookupPath(OriginalPath);
        if (!go) {
            return null;
        }

        var fling = go.GetComponent<FlingChildrenOnStart>();
        var parent = go.transform.parent;
        go.transform.SetParent(null, worldPositionStays: true);
        // only after SetParent, whose OnEnable would fling once started
        fling.checkedParentParent = true;
        fling.parentParent = parent;
        fling.hasStarted = true;
        return go;
    }
}

[HarmonyPatch]
internal static class FlingChildrenOnStartPatches {
    // Unity still calls Start once a restored, already flung object becomes active
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FlingChildrenOnStart), "Start")]
#pragma warning disable HARMONIZE001
    private static bool SkipRestoredFling(FlingChildrenOnStart __instance) => !__instance.hasStarted;
#pragma warning restore HARMONIZE001
}
