using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PreciseSavestates.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PreciseSavestates.Savestates.Game;

// Roots detached at runtime keep their original parent in paths, since root names clash (e.g. Under_18 "Harpoon Ring Chain")
internal static class DetachedRoots {
    private static readonly Dictionary<Transform, Transform> originalParents = [];

    public static bool IsDetached(Transform root) => originalParents.ContainsKey(root);

    public static Transform? OriginalParent(Transform root) =>
        originalParents.TryGetValue(root, out var parent) && parent ? parent : null;

    public static GameObject? Lookup(Scene scene, string path) {
        foreach (var (root, parent) in originalParents) {
            if (!root || !parent || root.gameObject.scene != scene) continue;

            var origin = $"{ObjectUtils.HierarchyPath(parent)}/{root.name}";
            if (path == origin) return root.gameObject;
            if (!path.StartsWith(origin + "/")) continue;

            var child = root.Find(path[(origin.Length + 1)..]);
            if (child != null) return child.gameObject;
        }

        return null;
    }

#if DEBUG
    /// Joints detached before a hot reload are only known to the previous generation's registry.
    public static void RegisterExisting() {
        foreach (var joints in Object.FindObjectsByType<FixParentedJoints>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            if (joints.isActive && joints.topParent) {
                Register(joints.selfParent, joints.topParent);
            }
        }
    }
#endif

    private static void Register(Transform root, Transform parent) {
        foreach (var dead in originalParents.Keys.Where(key => !key).ToList()) {
            originalParents.Remove(dead);
        }
        originalParents[root] = parent;
    }

    [HarmonyPatch]
    private static class Patches {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FixParentedJoints), "Awake")]
#pragma warning disable HARMONIZE001
        private static void FixParentedJointsAwake(FixParentedJoints __instance) {
#pragma warning restore HARMONIZE001
            if (__instance.isActive && __instance.topParent) {
                Register(__instance.selfParent, __instance.topParent);
            }
        }
    }
}
