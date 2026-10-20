using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Tests.Analysis;

namespace DotCharter.Tests.Services.Editing.DraftMerge;

/// <summary>
///     A 16-beat song at 120 BPM (one beat = 500 ms) with one six-string arrangement whose phrases are
///     editor-authored markers: "intro" on beat 0, "verse" on beat 4, "chorus" on beat 8 and "END" on
///     beat 12. The level starts empty; each test puts its own sounds on it.
/// </summary>
internal sealed class DraftMergeFixture
{
    public DraftMergeFixture(int capo = 0)
    {
        foreach (var beat in AnalysisTestSignals.ConstantBeats(120, 16)) Project.Beats.Add(beat);
        Arrangement = new Arrangement { Name = "Lead", StringCount = 6, Capo = capo };
        foreach (var (beat, phrase) in new[] { (0, "intro"), (4, "verse"), (8, "chorus"), (12, "END") })
            Arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = phrase });
        Arrangement.Levels.Add(Level);
        Project.Arrangements.Add(Arrangement);
    }

    public ChartProject Project { get; } = new();

    public Arrangement Arrangement { get; }

    public ChartLevel Level { get; } = new();

    public static GuitarNote Note(int beat, int str, int fret, int half = 0) => new()
    {
        Position = new FractionalPosition(beat, half == 0 ? Fraction.Zero : new Fraction(1, 2)),
        StringIndex = str,
        Fret = fret,
        Sustain = new FractionalPosition(0, new Fraction(1, 4))
    };

    /// <summary>A chord on <paramref name="beat" /> with one chord note per (string, fret).</summary>
    public static Chord Chord(int beat, int templateId, params (int Str, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (str, fret) in notes) chord.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
        return chord;
    }

    /// <summary>Puts <paramref name="notes" /> on the level in position order, as a real level holds them.</summary>
    public void Seed(params GuitarNote[] notes)
    {
        foreach (var note in notes.OrderBy(n => n.Position).ThenBy(n => n.StringIndex)) Level.Notes.Add(note);
    }

    public DraftMergeReview Plan(IReadOnlyList<GuitarNote> draft, bool replaceExisting = false,
        IReadOnlyList<double>? confidences = null) =>
        DraftMergeReview.Plan(Project, Arrangement, Level, Project.Beats, draft, confidences, 0.5, replaceExisting);
}
