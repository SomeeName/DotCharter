using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tempo;

/// <summary>
/// Doubles or halves the BPM across a measure range, preserving the
/// absolute audio time of every note/chord/handshape/anchor/event/tone.
/// Returns a single undoable action representing the entire mutation.
/// </summary>
public interface IBeatScaleService
{
    /// <summary>
    /// Builds (but does not execute) an action that scales the tempo of all
    /// beats whose time falls in [startMs, endMs).
    /// factor=2.0 → double-time (BPM x2, inserts midpoint beats).
    /// factor=0.5 → half-time  (BPM /2, removes every other beat).
    /// Returns null if the operation cannot be applied (locked, empty range).
    /// </summary>
    IChartAction? BuildScaleAction(ChartProject project, double startMs, double endMs, double factor);
}
