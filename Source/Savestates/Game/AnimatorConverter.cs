using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// Captures an Animator's playback position as the current state + normalized time per layer
public class AnimatorConverter : JsonConverter {
    public override bool CanConvert(Type objectType) => objectType == typeof(Animator);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        var animator = (Animator)value!;
        if (!animator.enabled) {
            new JObject { ["enabled"] = false }.WriteTo(writer);
            return;
        }

        var layers = new JArray();
        for (var i = 0; i < animator.layerCount; i++) {
            var state = animator.GetCurrentAnimatorStateInfo(i);
            layers.Add(new JObject {
                ["state"] = state.fullPathHash,
                ["time"] = state.normalizedTime,
            });
        }

        new JObject { ["enabled"] = true, ["layers"] = layers }.WriteTo(writer);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer) {
        var token = JToken.ReadFrom(reader);
        var animator = (Animator)existingValue!;
        if (!animator.gameObject.activeInHierarchy) {
            Log.Error($"Can't restore Animator state into inactive '{ObjectUtils.ObjectComponentPath(animator)}'");
            return animator;
        }

        animator.enabled = token.Value<bool?>("enabled") ?? true;
        if (!animator.enabled) {
            return animator;
        }

        var layers = (JArray)token["layers"]!;
        for (var i = 0; i < layers.Count; i++) {
            animator.Play(layers[i].Value<int>("state"), i, layers[i].Value<float>("time"));
        }

        // mirrors Gate.Opened, which forces AlwaysAnimate around Update(0)
        var cullingMode = animator.cullingMode;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Update(0f);
        animator.cullingMode = cullingMode;

        return animator;
    }
}
