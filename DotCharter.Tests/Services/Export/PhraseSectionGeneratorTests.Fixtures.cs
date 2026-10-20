using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

public sealed partial class PhraseSectionGeneratorTests : IDisposable
{
    private const double Tolerance = 2.0; // ms — float-second storage granularity

    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-phrasegen-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- helpers -----------------------------------------------------------

    private static List<BeatMarker> CreateBeats(int count = 64, double bpm = 120.0)
    {
        // 4/4 at 120 BPM: a beat every 500 ms, a measure every 4th beat.
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = bpm,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return beats;
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    private static Arrangement NewArrangement()
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Name = "Lead",
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    private static void AddNote(Arrangement arr, IList<BeatMarker> beats, double ms, int stringIndex = 0,
        int fret = 3, double sustainMs = 0, bool linkNext = false)
    {
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = Pos(beats, ms),
            StringIndex = stringIndex,
            Fret = fret,
            Sustain = sustainMs > 0 ? Pos(beats, ms + sustainMs) - Pos(beats, ms) : FractionalPosition.Zero,
            LinkNext = linkNext
        });
    }

    private static List<(string Section, double TimeMs)> Sections(Arrangement arr, IList<BeatMarker> beats)
    {
        return arr.EventPoints
            .Where(ep => !string.IsNullOrEmpty(ep.Section))
            .OrderBy(ep => ep.Position)
            .Select(ep => (ep.Section, ep.Position.GetPositionMs(beats)))
            .ToList();
    }

    private static List<(string Phrase, double TimeMs)> IterationsByTime(Arrangement arr, IList<BeatMarker> beats)
    {
        return arr.PhraseIterations
            .OrderBy(pi => pi.Position)
            .Select(pi => (arr.Phrases[pi.PhraseId], pi.Position.GetPositionMs(beats)))
            .ToList();
    }
}
