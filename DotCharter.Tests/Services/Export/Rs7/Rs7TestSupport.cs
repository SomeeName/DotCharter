namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>One sounding note of a chart, as the rs7string tests compare them: time
/// (whole ms), CHART string (lowest = 0), fret and the MIDI pitch it sounds.</summary>
internal sealed record Rs7Sound(int TimeMs, int String, int Fret, int Midi, bool InChord);

/// <summary>Shared helpers for the rs7string (#736) tests. The package reader's half (import, scan)
/// is a partial in the optional plugin's tests.</summary>
internal static partial class Rs7TestSupport
{
    public const double BeatMs = 750.0; // 80 BPM, as rs7chart

    /// <summary>Every sound in an arrangement's top level, sorted.</summary>
    public static List<Rs7Sound> Sounds(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var level = arrangement.Levels.OrderBy(l => l.DifficultyIndex).Last();
        var standard = TuningUtilities.GetStandardMidiPitches(arrangement.StringCount);
        int Midi(int s, int fret) => standard[s] + TuningOffsetResolution.For(arrangement, s) + fret;
        var sounds = level.Notes
            .Select(n => new Rs7Sound(Ms(n.Position, beats), n.StringIndex, n.Fret, Midi(n.StringIndex, n.Fret), false))
            .Concat(level.Chords.SelectMany(c => c.ChordNotes.Select(cn =>
                new Rs7Sound(Ms(c.Position, beats), cn.StringIndex, cn.Fret, Midi(cn.StringIndex, cn.Fret), true))))
            .OrderBy(s => s.TimeMs).ThenBy(s => s.String)
            .ToList();
        return sounds;
    }

    public static int Ms(FractionalPosition position, IList<BeatMarker> beats) =>
        TimelineMath.ToWholeMs(position.GetPositionMs(beats));

    public static List<BeatMarker> Beats(int count)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * BeatMs,
                IsMeasure = i % 4 == 0,
                Bpm = 80,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return beats;
    }
}
