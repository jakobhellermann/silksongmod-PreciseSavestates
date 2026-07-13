using System.Linq;
using HarmonyLib;
using PreciseSavestates.Source;
using UnityEngine;
using UnityEngine.Audio;

namespace PreciseSavestates.Savestates.Game;

// Restore the captured MusicCue across a load. Cue-only + coarse audibility (we don't reproduce playback position or
// the exact mixer snapshot — just that the right track plays, or none). Hooked to CustomSceneManagerReady so it runs
// after the destination scene applied its own music, and only ever at scene entry (never during normal gameplay).
//
// KNOWN LIMITATION: on a same-scene reload the game's CustomSceneManager closure often applies its music snapshot but
// aborts before calling CustomSceneManagerReady, so this hook doesn't fire and same-scene music isn't restored. A
// reliable fix needs a load-scoped trigger that runs after the scene's audio without hooking global audio calls (which
// pollutes normal gameplay). See the music-savestate-mixer memory.
[HarmonyPatch]
public static class MusicRestore {
    private const string MusicMixer = "Music";
    private const string AudibleSnapshot = "Normal";
    private const string SilentSnapshot = "Silent";

    private static string? pendingCue;
    private static bool hasPending;

    public static void SchedulePendingRestore(string? cueName) {
        pendingCue = cueName;
        hasPending = true;
    }

    // Drop a scheduled restore without applying it. Called for same-scene reloads: CustomSceneManagerReady won't fire
    // there, so the pending would otherwise linger and hijack the music of the *next* scene the player enters.
    public static void DiscardPending() {
        pendingCue = null;
        hasPending = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.CustomSceneManagerReady))]
    private static void CustomSceneManagerReady() {
        if (!hasPending) {
            return;
        }

        var cueName = pendingCue;
        hasPending = false;
        pendingCue = null;

        var am = GameManager.instance.AudioManager;

        if (cueName == null) {
            foreach (var source in am.MusicSources) {
                source.Stop();
            }

            TransitionMusicSnapshot(am, SilentSnapshot);
            Log.Info("[music] restored: no cue (stopped)");
            return;
        }

        if ((am.CurrentMusicCue ? am.CurrentMusicCue.name : null) != cueName) {
            var cue = Resources.FindObjectsOfTypeAll<MusicCue>().FirstOrDefault(c => c.name == cueName);
            if (!cue) {
                Log.Warning($"Savestate music cue '{cueName}' not loaded at load time");
                return;
            }

            am.ApplyMusicCue(cue, 0f, 0f, applySnapshot: false);
        }

        TransitionMusicSnapshot(am, AudibleSnapshot);
        Log.Info($"[music] restored cue='{cueName}'");
    }

    // AudioManager.ApplyMusicSnapshot (sets currentMusicSnapshot so the game's blend keeps our snapshot), not a raw
    // AudioMixerSnapshot.TransitionTo (which only moves the mixer and gets re-faded back to the stale field value).
    private static void TransitionMusicSnapshot(AudioManager am, string name) {
        var snapshot = Resources.FindObjectsOfTypeAll<AudioMixerSnapshot>()
            .FirstOrDefault(s => s.name == name && s.audioMixer && s.audioMixer.name == MusicMixer);
        if (snapshot) {
            am.ApplyMusicSnapshot(snapshot, 0f, 0f);
        } else {
            Log.Warning($"Music snapshot '{name}' on mixer '{MusicMixer}' not found at load time");
        }
    }
}
