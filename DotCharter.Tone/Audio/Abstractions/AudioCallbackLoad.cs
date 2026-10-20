namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The audio callback's load as FRACTIONS of the buffer period (0.25 = a
///     quarter of the block's wall-clock budget; above 1.0 the callback missed
///     its deadline). It is the cost of the whole render — song, stems, every
///     seat's rig and VST3 chain, the master bus — not a process CPU figure.
/// </summary>
/// <param name="Average">Exponentially weighted over ~0.5 s.</param>
/// <param name="Peak">The heaviest single block, decaying over ~1.5 s.</param>
public readonly record struct AudioCallbackLoad(double Average, double Peak);
