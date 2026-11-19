using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PreciseSavestates.Savestates.Game;

// The camera blur is persistent and only faded back by the sequence that set it, which a load skips
public class CameraBlurPlaneConverter : JsonConverter {
    public override bool CanConvert(Type objectType) => objectType == typeof(CameraBlurPlane);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        var blur = (CameraBlurPlane)value!;
        new JObject {
            ["spacing"] = blur.lastSetSpacing,
            ["vibrancy"] = blur.lastSetVibrancy,
            ["maskLerp"] = blur.lastSetMaskLerp,
            ["maskScale"] = blur.lastSetMaskScale,
        }.WriteTo(writer);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer) {
        var token = JToken.ReadFrom(reader);
        var blur = (CameraBlurPlane)existingValue!;
        // the setter stores a tenth of its value
        blur.SetSpacingInternal(token.Value<float>("spacing") * 10f);
        blur.SetVibrancyInternal(token.Value<float>("vibrancy"));
        blur.SetMaskLerpInternal(token.Value<float>("maskLerp"));
        blur.SetMaskScale(token.Value<float>("maskScale"), ForceCameraAspect.CurrentViewportAspect);
        return blur;
    }
}
