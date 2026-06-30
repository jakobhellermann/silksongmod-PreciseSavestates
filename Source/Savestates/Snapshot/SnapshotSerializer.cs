using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.UnityConverters.Math;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using TMProOld;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Events;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

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

    public static string SnapshotToString(object? obj) {
        return JsonConvert.SerializeObject(obj, Formatting.Indented, Settings);
    }

    public static void Populate(object target, string json) {
        using JsonReader reader = new JsonTextReader(new StringReader(json));
        JsonSerializer.Create(Settings).Populate(reader, target);
    }

    public static void Populate(object target, JToken json) {
        var serializer = JsonSerializer.Create(Settings);
        using JsonReader reader = new JTokenReader(json);
        serializer.Populate(reader, target);
    }

    private static readonly JsonSerializerSettings Settings = new() {
        // Error (not Serialize) so reference loops are detected and logged via the Error handler below instead of
        // being followed forever (Serialize stack-overflows on PlayMaker action graphs). Each logged loop tells us
        // which type to add a field ignore for, rather than silently dropping it like Ignore would.
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
        Error = (_, args) => {
            args.ErrorContext.Handled = true;
            Log.Error(
                $"Serialization during snapshot: {args.CurrentObject?.GetType()}: {args.ErrorContext.Path}: {args.ErrorContext.Error.Message}");
        },
        ContractResolver = resolver,
        Converters = new List<JsonConverter> {
            new Vector2Converter(),
            new Vector3Converter(),
            new Vector4Converter(),
            new QuaternionConverter(),
            new ColorConverter(),
            new Color32Converter(),
            // new AnimatorConverter(), TODO
            new StringEnumConverter(),
        },
    };

    private static CustomizableContractResolver resolver => new() {
        ContainerTypesToIgnore = [
            typeof(MonoBehaviour),
            typeof(Component),
            typeof(Object),
        ],
        FieldTypesToIgnore = [
            // ignored
            typeof(Camera),
            typeof(GameObject),
            typeof(UnityEventBase),
            typeof(Action),
            typeof(Delegate),
            typeof(PositionConstraint),
            typeof(TextMeshProUGUI),
            typeof(TMP_Text),
            typeof(Sprite),
            typeof(Tilemap),
            typeof(LineRenderer),
            typeof(Color),
            typeof(ParticleSystem),
            typeof(AnimationCurve),
            typeof(AnimationClip),
            typeof(Rect),
            // todo
            typeof(Transform), // maybe
            typeof(RenderTexture),
            typeof(Texture2D),
            typeof(Texture3D),
            typeof(SpriteRenderer), // maybe
            typeof(LayerMask), // maybe
            typeof(Collider2D), // maybe
            typeof(ScriptableObject),
            // PlayMaker: when snapshotting FSM action runtime fields, skip the definition graph and back-refs so
            // only primitive runtime state (timers etc.) is captured. NamedVariable covers FsmFloat/FsmBool/etc.;
            // variable values are snapshotted separately by name in PlayMakerFsmSnapshot.
            typeof(Fsm),
            typeof(FsmState),
            typeof(NamedVariable),
            typeof(FsmEvent),
            // back-references from action helpers (e.g. EventResponder.stateAction) point at other actions and form
            // reference loops — they're structural, not runtime state, so ignore any field typed as an action.
            typeof(FsmStateAction),
            // cached reflection metadata (e.g. CallMethod's cachedMethodInfo/cachedType/cachedParameterInfo) is a
            // lazily-rebuilt performance cache, not runtime state — and MethodInfo etc. can't be deserialized anyway.
            typeof(MemberInfo),
            typeof(ParameterInfo),
        ],
        ExactFieldTypesToIgnore = [typeof(Component)],
        FieldAllowlist = new Dictionary<Type, string[]> {
            { typeof(Transform), ["localPosition", "localRotation", "localScale"] },
            { typeof(Rigidbody2D), ["position", "linearVelocity", "gravityScale"] },
        },
        PropertyConverters = new Dictionary<Type, JsonConverter> {
            { typeof(tk2dSpriteAnimator), new Tk2dAnimatorConverter() },
        },
        FieldDenylist = new Dictionary<Type, string[]> {
            // FsmStateAction base boilerplate: definition/wiring that never changes at runtime, repeated on every
            // action. Keep only the runtime flags (active, finished); subclass runtime fields (timer etc.) are kept.
            {
                typeof(FsmStateAction),
                ["name", "enabled", "isOpen", "autoName", "blocksFinish", "fsmComponent", "Enabled", "Name"]
            },
            // Capture the full HeroAnimationController (animation-decision flags/timers drive the next clip); deny only
            // the fields that would otherwise serialize inline: pd/cState (plain classes captured elsewhere) and the
            // AudioEvent structs (they hold an AudioClip). The animator uses Tk2dAnimatorConverter; Component refs get
            // a RefConverter.
            {
                typeof(HeroAnimationController),
                ["pd", "cState", "wakeUpGround1", "wakeUpGround2", "wakeUpGroundCloakless", "backflipSpin"]
            },
        },
    };

    internal static void RemoveNullFields(JToken token, params string[] fields) {
        if (token is not JContainer container) {
            return;
        }

        var removeList = new List<JToken>();
        foreach (var el in container.Children()) {
            if (el is JProperty p && fields.Contains(p.Name) && p.Value.ToObject<object>() == null) {
                removeList.Add(el);
            }

            RemoveNullFields(el, fields);
        }

        foreach (var el in removeList) {
            el.Remove();
        }
    }
}
