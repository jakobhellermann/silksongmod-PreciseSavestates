using System.Collections;
using JetBrains.Annotations;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// Scene load-persisting scriptable object, not saved in ComponentSnapshots.
[PublicAPI]
public class RandomAudioTableSnapshot {
    public required string Name;
    public float[]? CurrentProbabilities;     // the "fair selection" weight accumulator
    public string? PreviousClip;              // previousClip by name, resolved against the table's clips on load
    public double NextPlayTime;               // cooldown gate

    public static RandomAudioTableSnapshot Of(RandomAudioClipTable table) {
        var previousClip = table.GetFieldValue<AudioClip>("previousClip");
        return new RandomAudioTableSnapshot {
            Name = table.name,
            CurrentProbabilities = table.GetFieldValue<float[]>("currentProbabilities"),
            PreviousClip = previousClip ? previousClip!.name : null,
            NextPlayTime = table.GetFieldValue<double>("nextPlayTime"),
        };
    }

    public void Restore(RandomAudioClipTable table) {
        table.SetFieldValue("currentProbabilities", CurrentProbabilities);
        table.SetFieldValue("nextPlayTime", NextPlayTime);
        table.SetFieldValue("previousClip", PreviousClip != null ? FindClip(table, PreviousClip) : null);
    }

    // AudioClip has no stable global id at runtime (GUID is editor/Addressables-only, InstanceID isn't stable).
    // But previousClip is always one of the table's own clips, so resolve it by name within clips[].
    private static AudioClip? FindClip(RandomAudioClipTable table, string clipName) {
        if (table.GetFieldValue<IEnumerable>("clips") is not { } clips) {
            return null;
        }

        foreach (var c in clips) {
            if (c.GetFieldValue<AudioClip>("Clip") is { } clip && clip && clip.name == clipName) {
                return clip;
            }
        }

        return null;
    }
}
