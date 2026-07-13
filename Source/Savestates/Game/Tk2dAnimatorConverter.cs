using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// Captures a tk2dSpriteAnimator's runtime state as {clip name, clipTime, fps, playing}
public class Tk2dAnimatorConverter : JsonConverter {
    public override bool CanConvert(Type objectType) {
        return objectType == typeof(tk2dSpriteAnimator);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        if (value is not tk2dSpriteAnimator animator || !animator) {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("clip");
        writer.WriteValue(animator.CurrentClip?.name);
        // Raw clipTime, in *frame units* (not seconds).
        // tk2d advances clipTime by deltaTime*clipFps and derives the frame as (int)clipTime,
        // so clipTime is fps-independent playback position.
        writer.WritePropertyName("clipTime");
        writer.WriteValue(animator.GetFieldValue<float>("clipTime"));
        writer.WritePropertyName("fps");
        writer.WriteValue(animator.ClipFps);
        writer.WritePropertyName("playing");
        writer.WriteValue(animator.Playing);

        // TODO: move to different snapshot?
        if (animator.GetComponent<tk2dBaseSprite>() is { } sprite && sprite) {
            var c = sprite.color;
            writer.WritePropertyName("color");
            writer.WriteStartArray();
            writer.WriteValue(c.r);
            writer.WriteValue(c.g);
            writer.WriteValue(c.b);
            writer.WriteValue(c.a);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer) {
        var token = JToken.ReadFrom(reader);
        if (token.Type == JTokenType.Null) {
            return existingValue;
        }

        if (existingValue is not tk2dSpriteAnimator animator || !animator) {
            Log.Warning($"Could not restore tk2dSpriteAnimator into '{existingValue}'");
            return existingValue;
        }

        if (token["color"] is JArray { Count: 4 } color
            && animator.GetComponent<tk2dBaseSprite>() is { } sprite && sprite) {
            sprite.color = new Color(
                color[0].Value<float>(), color[1].Value<float>(), color[2].Value<float>(), color[3].Value<float>());
        }

        var clip = token["clip"]?.Value<string>();
        if (clip == null) {
            return existingValue;
        }

        var clipTime = token["clipTime"]?.Value<float>() ?? 0f;
        var fps = token["fps"]?.Value<float>() ?? -1f;
        var playing = token["playing"]?.Value<bool>() ?? true;

        animator.PlayFrom(clip, 0f);
        if (fps > 0f) {
            animator.ClipFps = fps;
        }

        animator.SetFieldValue("clipTime", clipTime);

        // Refresh visible sprite without emitting events
        if (animator.CurrentClip is { frames.Length: > 0 } cc) {
            var len = cc.frames.Length;
            var frame = cc.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once
                ? Math.Min((int)clipTime, len - 1)
                : (int)clipTime % len;
            animator.SetFrame(frame, triggerEvent: false);
        }

        if (!playing) {
            animator.Stop();
        }

        return existingValue;
    }
}
