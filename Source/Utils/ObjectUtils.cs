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
        return $"{objectPath}@{component.GetType().Name}";
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
        var (objectPath, componentName) = path.SplitOnce('@') ??
                                          throw new Exception($"Object-Component path contains no component: {path}");

        var obj = LookupPath(objectPath);
        if (!obj) {
            return null;
        }

        // PERF
        var components = obj.GetComponents<Component>();
        return components.FirstOrDefault(c => c.GetType().Name == componentName);
    }
}
