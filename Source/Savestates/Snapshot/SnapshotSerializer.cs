using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.UnityConverters.Math;
using PreciseSavestates.Savestates.Game;
using PreciseSavestates.Source;
using PreciseSavestates.Source.Savestates.Snapshot;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Snapshot;

public static class SnapshotSerializer {
    public static void SnapshotRecursive(
        Component component,
        List<ComponentSnapshot> snapshots,
        HashSet<Component> seen,
        int? maxDepth = null
    ) {
        SnapshotRecursive(component, snapshots, seen, component, 0, maxDepth);
    }

    private static void SnapshotRecursive(
        Component component,
        List<ComponentSnapshot> snapshots,
        HashSet<Component> seen,
        Component? onlyDescendantsOf,
        int depth,
        int? maxDepth = null
    ) {
        if (seen.Contains(component)) {
            return;
        }

        ComponentSnapshot.NormalizeCenterOfMass(component);

        RefConverter.References.Clear();
        var tok = JToken.FromObject(component, JsonSerializer.Create(Settings));

        seen.Add(component);
        snapshots.Add(new ComponentSnapshot {
            Data = tok,
            Path = ObjectUtils.ObjectComponentPath(component),
        });

        if (depth >= maxDepth) {
            return;
        }

        foreach (var reference in RefConverter.References.ToArray()) {
            var recurseIntoReference = !onlyDescendantsOf || reference.transform.IsChildOf(component.transform);
            if (recurseIntoReference) {
                SnapshotRecursive(reference, snapshots, seen, onlyDescendantsOf, depth, maxDepth);
            }
        }
    }

    public static JToken Snapshot(object obj) {
        return JToken.FromObject(obj, JsonSerializer.Create(Settings));
    }

    public static object? Deserialize(JToken token, Type type) {
        return token.ToObject(type, JsonSerializer.Create(Settings));
    }

    public static void Populate(object target, JToken json) {
        var serializer = JsonSerializer.Create(Settings);
        using JsonReader reader = new JTokenReader(json);
        serializer.Populate(reader, target);
    }

    private static readonly JsonSerializerSettings Settings = new() {
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
        Error = (_, args) => {
            args.ErrorContext.Handled = true;
            Log.Error(
                $"Serialization during snapshot: {args.CurrentObject?.GetType()}: {args.ErrorContext.Path}: {args.ErrorContext.Error.Message}");
        },
        ContractResolver = GameSpecific.Resolver,
        Converters = [
            new Vector2Converter(),
            new Vector3Converter(),
            new Vector4Converter(),
            new QuaternionConverter(),
            new ColorConverter(),
            new Color32Converter(),
            // new AnimatorConverter(), TODO
            new StringEnumConverter(),
            ..GameSpecific.ExtraConverters,
        ],
    };
}
