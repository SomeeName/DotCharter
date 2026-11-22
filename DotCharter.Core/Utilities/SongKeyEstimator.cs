using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     A song's estimated key: the best of the 24 major and minor keys, the
///     runner-up, and their Krumhansl profile correlations.
/// </summary>
/// <param name="Best">The best-fitting key.</param>
/// <param name="RunnerUp">The second-best key; often the relative major or minor.</param>
/// <param name="BestScore">The best key's profile correlation (−1 … 1).</param>
/// <param name="RunnerUpScore">The runner-up's profile correlation.</param>
public readonly record struct SongKeyEstimate(
    ChordKeySignature Best, ChordKeySignature RunnerUp, double BestScore, double RunnerUpScore)
{
    /// <summary>A margin below this is a close call: the runner-up is a real alternative.</summary>
    internal const double CloseMargin = 0.05;

    /// <summary>A margin at or above this reads as fully confident.</summary>
    internal const double ConfidentMargin = 0.2;

    /// <summary>How far the best key leads the runner-up.</summary>
    public double Margin => BestScore - RunnerUpScore;

    /// <summary>0 … 1: the margin scaled so <see cref="ConfidentMargin" /> reads as certain.</summary>
    public double Confidence => Math.Clamp(Margin / ConfidentMargin, 0, 1);

    /// <summary>True when the runner-up is close enough that both should be shown as candidates.</summary>
    public bool IsClose => Margin < CloseMargin;

    /// <summary>True when the runner-up is the relative major or minor (same signature, other mode).</summary>
    public bool RunnerUpIsRelative => RunnerUp.Accidentals == Best.Accidentals && RunnerUp.IsMinor != Best.IsMinor;
}

/// <summary>
///     The song key shown to the charter. It reads what the song SOUNDS:
///     every arrangement's hardest level, each note at its sounding pitch
///     (<see cref="SoundingPitch" />: tuning and capo) shifted by the arrangement's
///     <c>CentOffset</c> to the nearest semitone, weighted by how long it rings
///     (an onset accent plus its sustain in beats). The same profile correlation
///     as <see cref="ChordKeyEstimator" /> ranks the 24 keys; a chroma histogram of
///     the audio goes through <see cref="FromHistogram" /> when there are no notes.
/// </summary>
public static class SongKeyEstimator
{
    /// <summary>What every note weighs before its sustain, so an unsustained note still counts.</summary>
    internal const double OnsetWeight = 0.5;

    /// <summary>Total weight below which there is too little to call a key.</summary>
    internal const double MinimumWeight = 4.0;

    /// <summary>The key of the notes in <paramref name="arrangements" />, or null when there are too few.</summary>
    public static SongKeyEstimate? FromArrangements(IEnumerable<Arrangement> arrangements)
    {
        ArgumentNullException.ThrowIfNull(arrangements);
        var histogram = new double[12];
        foreach (var arrangement in arrangements)
            AddSounding(arrangement, histogram);
        return FromHistogram(histogram);
    }

    /// <summary>
    ///     The best and second-best key for a 12-bin pitch-class weight (index 0 = C),
    ///     or null when it is missing, malformed or too light to say.
    /// </summary>
    public static SongKeyEstimate? FromHistogram(IReadOnlyList<double>? histogram)
    {
        if (histogram == null || histogram.Count != 12 || histogram.Sum() < MinimumWeight) return null;

        var best = (Key: new ChordKeySignature(0, false), Score: double.NegativeInfinity);
        var second = best;
        for (var tonic = 0; tonic < 12; tonic++)
        {
            foreach (var minor in (ReadOnlySpan<bool>)[false, true])
            {
                var score = ChordKeyEstimator.Correlation(histogram, tonic, minor);
                var key = ChordKeySignature.FromTonic(tonic, minor);
                if (score > best.Score)
                {
                    second = best;
                    best = (key, score);
                }
                else if (score > second.Score)
                {
                    second = (key, score);
                }
            }
        }

        return best.Score <= 0 ? null : new SongKeyEstimate(best.Key, second.Key, best.Score, second.Score);
    }

    /// <summary>Adds the hardest level's notes of one arrangement, duration-weighted, at sounding pitch.</summary>
    internal static void AddSounding(Arrangement arrangement, double[] histogram)
    {
        ChartLevel? hardest = null;
        foreach (var level in arrangement.Levels)
        {
            if (hardest == null || level.DifficultyIndex > hardest.DifficultyIndex)
                hardest = level;
        }

        if (hardest == null) return;
        var semitones = (int)Math.Round(arrangement.CentOffset / 100.0);

        void Add(int stringIndex, int fret, MuteType mute, FractionalPosition sustain)
        {
            if (mute == MuteType.Full || SoundingPitch.MidiFor(arrangement, stringIndex, fret) is not { } midi) return;
            histogram[ChordKeySignature.Mod12(midi + semitones)] += OnsetWeight + Math.Max(0, sustain.DoubleValue);
        }

        foreach (var note in hardest.Notes)
            Add(note.StringIndex, note.Fret, note.Mute, note.Sustain);
        foreach (var chord in hardest.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
                Add(chordNote.StringIndex, chordNote.Fret, chordNote.Mute, chordNote.Sustain);
        }
    }
}
