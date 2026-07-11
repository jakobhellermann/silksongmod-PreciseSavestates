using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PreciseSavestates.Savestates.Snapshot;

// Captures a tk2dSpriteAnimator's runtime state as {clip name, time, fps, playing} and restores it via the animator's
// own API (PlayFrom resolves the clip through the animator's library and refreshes the visible sprite). Writing the
// clip by name (not by value) avoids serializing the clip definition or mutating the shared library on restore.
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
        writer.WritePropertyName("time");
        writer.WriteValue(animator.ClipTimeSeconds);
        writer.WritePropertyName("fps");
        writer.WriteValue(animator.ClipFps);
        writer.WritePropertyName("playing");
        writer.WriteValue(animator.Playing);
        writer.WriteEndObject();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer) {
        var token = JToken.ReadFrom(reader);
        if (token.Type == JTokenType.Null) {
            return existingValue;
        }

        if (existingValue is not tk2dSpriteAnimator animator || !animator) {
            // we can only restore into a live animator (resolved via the surrounding component); nothing to do otherwise
            return existingValue;
        }

        var clip = token["clip"]?.Value<string>();
        if (clip == null) {
            return existingValue;
        }

        var time = token["time"]?.Value<float>() ?? 0f;
        var fps = token["fps"]?.Value<float>() ?? -1f;
        var playing = token["playing"]?.Value<bool>() ?? true;

        // PlayFrom sets the current clip and warps to the captured time — WarpClipToLocalTime → SetSprite, which
        // refreshes the *visible* sprite (and re-derives the tk2d BoxCollider2D from that frame). This is why the
        // animator must round-trip through the converter, not a raw field-set: setting currentClip/clipTime by
        // reflection leaves the visible sprite (and its derived collider) stale on the prefab-default frame.
        animator.PlayFrom(clip, time);
        if (fps > 0f) {
            animator.ClipFps = fps;
        }

        if (!playing) {
            animator.Stop();
        }

        return existingValue;
    }
}
