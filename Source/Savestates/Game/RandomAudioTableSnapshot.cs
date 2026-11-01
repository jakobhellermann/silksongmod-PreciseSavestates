using System.Linq;
using JetBrains.Annotations;
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
        var previousClip = table.previousClip;
        return new RandomAudioTableSnapshot {
            Name = table.name,
            CurrentProbabilities = table.currentProbabilities,
            PreviousClip = previousClip ? previousClip!.name : null,
            NextPlayTime = table.nextPlayTime,
        };
    }

    public void Restore(RandomAudioClipTable table) {
        table.currentProbabilities = CurrentProbabilities;
        table.nextPlayTime = NextPlayTime;
        table.previousClip = PreviousClip != null ? FindClip(table, PreviousClip) : null;
    }

    // AudioClip has no stable global id at runtime (GUID is editor/Addressables-only, InstanceID isn't stable).
    // But previousClip is always one of the table's own clips, so resolve it by name within clips[].
    private static AudioClip? FindClip(RandomAudioClipTable table, string clipName) {
        return table.clips.Select(c => c.Clip).FirstOrDefault(clip => clip && clip.name == clipName);
    }
}
