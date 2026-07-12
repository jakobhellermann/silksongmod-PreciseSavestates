using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Snapshot;

// Captures a tk2dSpriteAnimator's runtime state as {clip name, clipTime, fps, playing} and restores it via the
// animator's own API (PlayFrom resolves the clip through the animator's library and refreshes the visible sprite).
// Writing the clip by name (not by value) avoids serializing the clip definition or mutating the shared library.
// Also captures the sibling tk2dBaseSprite's `color` (same GameObject): a runtime tint an FSM sets that the
// OnEnter-skipping restore won't re-apply. Raw floats, since Color is in the resolver's FieldTypesToIgnore.
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
        // Raw clipTime, in *frame units* (not seconds). tk2d advances clipTime by deltaTime*clipFps and derives the
        // frame as (int)clipTime, so clipTime is fps-independent playback position. Capturing seconds instead
        // (ClipTimeSeconds = clipTime/clipFps) does NOT round-trip when the runtime fps differs from the clip's
        // authored fps — bosses scale animation speed (Cog_Dancers' Beat Control), so PlayFrom's seconds×clip.fps
        // lands on the wrong frame. Capture frame units and restore them directly.
        writer.WritePropertyName("clipTime");
        writer.WriteValue(animator.GetFieldValue<float>("clipTime"));
        writer.WritePropertyName("fps");
        writer.WriteValue(animator.ClipFps);
        writer.WritePropertyName("playing");
        writer.WriteValue(animator.Playing);

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
            // we can only restore into a live animator (resolved via the surrounding component); nothing to do otherwise
            return existingValue;
        }

        // Independent of the clip: a color-setting state need not be an animation state.
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

        // Resolve + arm the clip at position 0 (PlayFrom → WarpClipToLocalTime → SetSprite: this is why the animator
        // must round-trip through the converter, not a raw field-set — otherwise the visible sprite and its derived
        // collider stay stale on the prefab-default frame). Then restore the real runtime fps and the raw frame-units
        // clipTime *directly* (PlayFrom's own seconds×clip.fps warp is not frame-exact under a scaled runtime fps),
        // and refresh the visible sprite to that frame without firing frame events.
        animator.PlayFrom(clip, 0f);
        if (fps > 0f) {
            animator.ClipFps = fps;
        }

        animator.SetFieldValue("clipTime", clipTime);

        // Refresh the visible sprite to the frame this clipTime maps to. Match tk2d's own wrap handling: a finished
        // Once clip clamps to its LAST frame (len-1), whereas `clipTime % len` (and CurrentFrame, which returns
        // Min((int)clipTime, len) → can equal len) would wrongly wrap that to frame 0. No frame events.
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
