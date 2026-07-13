using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PreciseSavestates.Source;

namespace PreciseSavestates.Savestates;

internal sealed class LoadTiming {
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly Stopwatch phase = Stopwatch.StartNew();
    private readonly List<(string Label, long Ms)> phases = [];

    /// Close the current phase and start a new one
    public void Mark(string label) {
        phases.Add((label, phase.ElapsedMilliseconds));
        phase.Restart();
    }

    /// Record a phase whose duration was measured elsewhere
    public void Add(string label, long ms) {
        phases.Add((label, ms));
    }

    public void LogSummary() {
        var breakdown = string.Join(", ", phases.Select(p => $"{p.Label} {p.Ms}ms"));
        Log.Info($"Savestate load timing: {total.ElapsedMilliseconds}ms total ({breakdown})");
    }
}
