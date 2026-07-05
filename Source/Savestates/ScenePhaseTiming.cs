using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;

namespace PreciseSavestates.Savestates;

/// Wall-clock timing of the game's scene-load phases (Fetch/ClearMem/Activation/GarbageCollect). SceneLoad records
/// these itself, but via Time.realtimeSinceStartup, which DeterministicTimePatch freezes during a TAS load — so its
/// GetDuration reports 0. This hooks SceneLoad's private Record{Begin,End}Time with an independent Stopwatch (real
/// wall-clock, not routed through Time.*) so LoadTiming can surface where the transition time actually goes.
internal static class ScenePhaseTiming {
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static readonly Dictionary<string, long> begun = new();
    private static readonly Dictionary<string, long> durations = new();

    /// Begin recording a fresh load's phases. Call before BeginSceneTransition.
    public static void Reset() {
        begun.Clear();
        durations.Clear();
    }

    public static IEnumerable<(string Phase, long Ms)> Durations() =>
        durations.Select(kv => (kv.Key, kv.Value));

    private static void Begin(string phase) => begun[phase] = clock.ElapsedMilliseconds;

    private static void End(string phase) {
        if (begun.TryGetValue(phase, out var start)) {
            durations[phase] = clock.ElapsedMilliseconds - start;
        }
    }

    [HarmonyPatch]
#pragma warning disable HARMONIZE001
    private static class Patch {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SceneLoad), "RecordBeginTime", typeof(SceneLoad.Phases))]
        private static void RecordBeginTime(SceneLoad.Phases phase) => Begin(phase.ToString());

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SceneLoad), "RecordEndTime", typeof(SceneLoad.Phases))]
        private static void RecordEndTime(SceneLoad.Phases phase) => End(phase.ToString());
    }
#pragma warning restore HARMONIZE001
}
