using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PreciseSavestates.Utils;

public static class ObjectUtils {
    public static string ObjectPath(GameObject obj) {
        List<string> segments = [];
        for (var current = obj; current != null; current = current.transform.parent?.gameObject) {
            segments.Add(current.name);
        }

        segments.Reverse();
        return segments.Join(delimiter: "/");
    }

    [return: NotNullIfNotNull(nameof(component))]
    public static string? ObjectComponentPath(Component? component) {
        if (!component) {
            return null;
        }

        var objectPath = ObjectPath(component!.gameObject);
        var type = component.GetType();

        // a GameObject can hold multiple components of the same type (e.g. the hero's many PlayMakerFSMs) — without
        // an index they'd all share one path and a $ref restore would resolve every one to the first component.
        var sameType = component.gameObject.GetComponents(type);
        if (sameType.Length > 1) {
            var index = Array.IndexOf(sameType, component);
            return $"{objectPath}@{type.Name}:{index}";
        }

        return $"{objectPath}@{type.Name}";
    }

    public static GameObject? LookupPath(string path) {
        var dontDestroyScene = GameManager.instance.gameObject.scene;
        if (LookupPath(dontDestroyScene, path) is { } objD) {
            return objD;
        }

        for (var i = 0; i < SceneManager.sceneCount; i++) {
            var scene = SceneManager.GetSceneAt(i);
            if (LookupPath(scene, path) is { } obj) {
                return obj;
            }
        }

        return null;
    }

    public static GameObject? LookupPath(Scene scene, string path) {
        var rootObjects = scene.GetRootGameObjects();
        return GetGameObjectFromArray(rootObjects, path);
    }


    internal static GameObject? GetGameObjectFromArray(GameObject[] objects, string objName) {
        // Split object name into root and child names based on '/'
        string rootName;
        string? childName = null;

        var slashIndex = objName.IndexOf('/');
        if (slashIndex == -1) {
            rootName = objName;
        } else if (slashIndex == 0 || slashIndex == objName.Length - 1) {
            throw new ArgumentException("Invalid GameObject path");
        } else {
            rootName = objName[..slashIndex];
            childName = objName[(slashIndex + 1)..];
        }

        // Get root object
        var obj = objects.FirstOrDefault(o => o.name == rootName);
        if (obj is null) {
            return null;
        }

        // Get child object
        if (childName == null) {
            return obj;
        }


        var t = obj.transform.Find(childName);
        return !t ? null : t.gameObject;
    }


    public static Component? LookupObjectComponentPath(string path) {
        var (objectPath, componentSpec) = path.SplitOnce('@') ??
                                          throw new Exception($"Object-Component path contains no component: {path}");

        var obj = LookupPath(objectPath);
        if (!obj) {
            return null;
        }

        // componentSpec is "TypeName" or, for disambiguated same-type components, "TypeName:index"
        var componentName = componentSpec;
        int? index = null;
        var colon = componentSpec.IndexOf(':');
        if (colon >= 0) {
            componentName = componentSpec[..colon];
            if (int.TryParse(componentSpec[(colon + 1)..], out var i)) {
                index = i;
            }
        }

        // PERF
        var matches = obj.GetComponents<Component>().Where(c => c.GetType().Name == componentName).ToArray();
        if (index is { } idx) {
            return idx < matches.Length ? matches[idx] : null;
        }

        return matches.FirstOrDefault();
    }
}
