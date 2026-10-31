using System;
using System.Linq;
using HarmonyLib;
using PreciseSavestates.Source;
using UnityEngine;
using UnityEngine.Audio;

namespace PreciseSavestates.Savestates.Game;

public class MusicSnapshot {
    public string? Cue;
    // Unity can't read back the mixer's state, and FSMs (e.g. boss music) transition it directly
    public string? MixerSnapshot;

    public static MusicSnapshot Of(AudioManager am) => new() {
        Cue = am.CurrentMusicCue ? am.CurrentMusicCue.name : null,
        MixerSnapshot = MixerTracker.LastMusicSnapshot ? MixerTracker.LastMusicSnapshot!.name : null,
    };

    // the scene applies its own music on entry, which would override this
    public void RestoreAfterSceneAudio() {
        var sm = GameManager.instance.sm;
        if (sm.IsAudioSnapshotsApplied) {
            Restore();
            return;
        }

        Action? onApplied = null;
        onApplied = () => {
            sm.AudioSnapshotsApplied -= onApplied;
            try {
                Restore();
            } catch (Exception e) {
                Log.Error($"Failed to restore music: {e}");
            }
        };
        sm.AudioSnapshotsApplied += onApplied;
    }

    private void Restore() {
        Log.Info($"Restoring music cue={Cue} mixer={MixerSnapshot}");

        var am = GameManager.instance.AudioManager;
        if (Cue != null) {
            var cue = Resources.FindObjectsOfTypeAll<MusicCue>().FirstOrDefault(c => c.name == Cue);
            if (!cue) {
                throw new Exception($"Music cue '{Cue}' is not loaded");
            }

            am.ApplyMusicCue(cue, 0f, 0f, applySnapshot: false);
        }

        if (MixerSnapshot != null) {
            var snapshot = Resources.FindObjectsOfTypeAll<AudioMixerSnapshot>()
                .FirstOrDefault(s => s.name == MixerSnapshot && s.audioMixer.name == MixerTracker.MusicMixer);
            if (!snapshot) {
                throw new Exception($"Music mixer snapshot '{MixerSnapshot}' not found");
            }

            snapshot.TransitionTo(0f);
        }
    }

    [HarmonyPatch]
    private static class MixerTracker {
        public const string MusicMixer = "Music";
        public static AudioMixerSnapshot? LastMusicSnapshot;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AudioMixer), "TransitionToSnapshot")]
#pragma warning disable HARMONIZE001
        private static void TransitionToSnapshot(AudioMixer __instance, AudioMixerSnapshot snapshot) {
#pragma warning restore HARMONIZE001
            if (__instance.name == MusicMixer) {
                LastMusicSnapshot = snapshot;
            }
        }
    }
}
