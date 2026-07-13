using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;

namespace PreciseSavestates.Savestates.Game;

// SceneLoad timings, but timed manually through Stopwatch since TAS reports 0
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
