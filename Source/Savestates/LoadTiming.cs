using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PreciseSavestates.Source;

namespace PreciseSavestates.Savestates;

/// Records the wall-clock cost of each phase of a savestate load and logs a one-line breakdown. Most of the latency
/// sits in the scene-entry tail (the hero fade-up plus the entry waits) rather than the asset load — splitting it out
/// makes that visible and lets each load-skip optimization be measured against a baseline.
internal sealed class LoadTiming {
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly Stopwatch phase = Stopwatch.StartNew();
    private readonly List<(string Label, long Ms)> phases = [];

    /// Close the current phase under <paramref name="label"/> and start the next one.
    public void Mark(string label) {
        phases.Add((label, phase.ElapsedMilliseconds));
        phase.Restart();
    }

    /// Record a phase whose duration was measured elsewhere (e.g. the snapshot apply, which runs deferred on a
    /// separate stopwatch rather than back-to-back with the load phases).
    public void Add(string label, long ms) {
        phases.Add((label, ms));
    }

    public void LogSummary() {
        var breakdown = string.Join(", ", phases.Select(p => $"{p.Label} {p.Ms}ms"));
        Log.Info($"Savestate load timing: {total.ElapsedMilliseconds}ms total ({breakdown})");
    }
}
