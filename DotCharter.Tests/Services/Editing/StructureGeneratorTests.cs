using System.Reflection;
using DotCharter.Services.Editing.Structure;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     The structure pass, and above all the LINE it must not cross. Every
///     number quoted here was measured over the 89 hand-finished arrangements in
///     <c>TestData/EOF</c> before a line of it was written
///     (<c>LocalOnly_StructureGroundTruthProbe</c>).
/// </summary>
public sealed class StructureGeneratorTests
{
    /// <summary>A 120 BPM grid: one beat every 500 ms.</summary>
    private static List<BeatMarker> Beats(int count = 200)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });
        return beats;
    }

    private static GuitarNote Note(int beat, double sustainBeats = 0)
    {
        var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 0, Fret = 5 };
        if (sustainBeats > 0) note.Sustain = new FractionalPosition((int)sustainBeats);
        return note;
    }

    private static Arrangement WithSounds(params GuitarNote[] notes)
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        foreach (var note in notes) level.Notes.Add(note);
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static GuitarNote NoteAt(int beat, int fret) =>
        new() { Position = new FractionalPosition(beat), StringIndex = 0, Fret = fret };

    private static void Section(Arrangement arrangement, int beat, string name) =>
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Section = name });

    // ------------------------------------------------------- COUNT and END

    /// <summary>89 of 89 hand-finished arrangements carry exactly one COUNT and
    ///     one END. Nothing in the editor made them, which is why the inspector's
    ///     NoEndPhrase was permanent furniture.</summary>
    [Fact]
    public void ACleanArrangementGetsBothCountAndEnd()
    {
        var arrangement = WithSounds(Note(4), Note(8, 2));

        var plan = StructureGenerator.Plan(arrangement, Beats());

        Assert.Equal(FractionalPosition.Zero, plan.CountPhrase);
        Assert.NotNull(plan.EndPhrase);
        // The END sits on the first beat AFTER everything stops sounding — the
        // last note starts at 8 and rings for 2.
        Assert.True(plan.EndPhrase!.Value > new FractionalPosition(10));
    }

    [Fact]
    public void PhrasesThatAlreadyExistAreNotProposedAgain()
    {
        var arrangement = WithSounds(Note(4));
        arrangement.EventPoints.Add(new EventPoint { Position = FractionalPosition.Zero, Phrase = "COUNT" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(50), Phrase = "END" });

        var plan = StructureGenerator.Plan(arrangement, Beats());

        Assert.Null(plan.CountPhrase);
        Assert.Null(plan.EndPhrase);
    }

    /// <summary>An imported chart carries its phrases in <c>Arrangement.Phrases</c>
    ///     rather than on markers — the pass must see those too or it duplicates
    ///     every END on the first run.</summary>
    [Fact]
    public void APhraseOnTheImportedListAlsoCounts()
    {
        var arrangement = WithSounds(Note(4));
        arrangement.Phrases.Add("COUNT");
        arrangement.Phrases.Add("END");

        var plan = StructureGenerator.Plan(arrangement, Beats());

        Assert.Null(plan.CountPhrase);
        Assert.Null(plan.EndPhrase);
    }

    // ------------------------------------------------------------ noguitar

    /// <summary>171 of 171 <c>noguitar</c> sections in the corpus contain no
    ///     sound at all, so a bracketed silence is the one section a machine can
    ///     name with certainty.</summary>
    [Fact]
    public void ASilenceBracketedByASectionBecomesNoGuitar()
    {
        // Sound to beat 4, silence, sound resumes at beat 24 (10 s at 120 BPM).
        var arrangement = WithSounds(Note(0), Note(4), Note(24), Note(28));
        Section(arrangement, 0, "verse");
        Section(arrangement, 24, "chorus");

        var plan = StructureGenerator.Plan(arrangement, Beats());

        var at = Assert.Single(plan.NoGuitarSections);
        Assert.True(at > new FractionalPosition(4) && at < new FractionalPosition(24));
    }

    /// <summary>
    ///     THE SAFETY PROPERTY. A section runs until the NEXT section, so a
    ///     noguitar dropped into a gap whose resume point nobody marked would
    ///     swallow the music after it and label it "no guitar" — a chart-wide
    ///     error produced by a convenience.
    /// </summary>
    [Fact]
    public void ASilenceWithNoSectionAtTheResumePointIsLeftAlone()
    {
        var arrangement = WithSounds(Note(0), Note(4), Note(24), Note(28));
        Section(arrangement, 0, "verse");
        // …and nothing at beat 24, where the guitar comes back.

        Assert.Empty(StructureGenerator.Plan(arrangement, Beats()).NoGuitarSections);
    }

    [Fact]
    public void AShortRestIsARestAndNotASection()
    {
        // Two beats of silence at 120 BPM = 1 s, under the 2 s floor.
        var arrangement = WithSounds(Note(0), Note(4), Note(6), Note(10));
        Section(arrangement, 0, "verse");
        Section(arrangement, 6, "chorus");

        Assert.Empty(StructureGenerator.Plan(arrangement, Beats()).NoGuitarSections);
    }

    [Fact]
    public void ASilenceTheCharterAlreadyMarkedIsLeftAlone()
    {
        var arrangement = WithSounds(Note(0), Note(4), Note(24), Note(28));
        Section(arrangement, 0, "verse");
        Section(arrangement, 8, "breakdown"); // the charter's own call inside the gap
        Section(arrangement, 24, "chorus");

        Assert.Empty(StructureGenerator.Plan(arrangement, Beats()).NoGuitarSections);
    }

    /// <summary>
    ///     A sustain still counts as sound. Measuring the gap from note ONSETS
    ///     would call a four-bar ringing chord a silence and bury it under a
    ///     noguitar section.
    /// </summary>
    [Fact]
    public void ARingingSustainIsNotSilence()
    {
        var arrangement = WithSounds(Note(0), Note(4, 19), Note(24));
        Section(arrangement, 0, "verse");
        Section(arrangement, 24, "chorus");

        Assert.Empty(StructureGenerator.Plan(arrangement, Beats()).NoGuitarSections);
    }

    // ---------------------------------------------------------- the refusal

    /// <summary>
    ///     The pass proposes no section NAME other than <c>noguitar</c>, ever.
    ///     Zero of the 24 GuitarPro sources in the corpus carry a section marker
    ///     and only 14.8% of sections follow a rest, so where a verse becomes a
    ///     chorus has no signal to derive it from.
    /// </summary>
    [Fact]
    public void NoSectionBoundaryIsEverInvented()
    {
        var arrangement = WithSounds(Note(0), Note(4), Note(8), Note(12), Note(16), Note(20));

        var plan = StructureGenerator.Plan(arrangement, Beats());

        Assert.Empty(plan.NoGuitarSections);
        // COUNT and END are phrases, not sections — the pass adds no section at
        // all to a chart that is one continuous passage.
        Assert.Equal(2, plan.Count);
    }

    /// <summary>
    ///     …and REPETITION does not change that. "Sections repeat, so propagate
    ///     the label instead of inventing it" is the obvious next idea and it was
    ///     measured before it was refused (2026-07-27,
    ///     <c>LocalOnly_SectionStructureProbe.CanASectionLabelBePropagatedFromContent</c>,
    ///     2,511 predictable sections over the 89 corpus arrangements):
    ///     nearest-EARLIER-neighbour by note content names the repeat correctly
    ///     56.2% of the time, and 69.5% at its best operating point — where it has
    ///     already given up 58% of the recall. The content-FREE rule "copy the
    ///     PREVIOUS section's name" scores 68.9% at full coverage on the very same
    ///     rows, and 76.2% on the subset where the content matcher is most
    ///     confident. Content loses everywhere, so a repeat is not a reason to
    ///     propose a name.
    /// </summary>
    [Fact]
    public void AChartWhoseSectionsObviouslyRepeat_StillGetsNoProposedSection()
    {
        // A distinct four-note figure at beats 0-6, a different one at 8-14, then
        // the FIRST figure again, note for note, at 16-22 — and beat 16 is
        // deliberately left unnamed.
        var arrangement = WithSounds(
            NoteAt(0, 3), NoteAt(2, 5), NoteAt(4, 7), NoteAt(6, 5),
            NoteAt(8, 10), NoteAt(10, 12), NoteAt(12, 10), NoteAt(14, 8),
            NoteAt(16, 3), NoteAt(18, 5), NoteAt(20, 7), NoteAt(22, 5));
        Section(arrangement, 0, "verse");
        Section(arrangement, 8, "chorus");

        var plan = StructureGenerator.Plan(arrangement, Beats());

        Assert.Empty(plan.NoGuitarSections);
        // COUNT and END are phrases. Not one section is proposed for the repeat.
        Assert.Equal(2, plan.Count);
    }

    /// <summary>
    ///     THE TYPE ITSELF REFUSES. A <see cref="StructurePlan" /> carries
    ///     POSITIONS only — no member can hold a section or phrase NAME, because
    ///     the only name it ever implies is <c>noguitar</c>, the one a machine can
    ///     be certain of. Boundary DETECTION was measured the same day and is no
    ///     better: over 12,778 candidate measure starts (base rate 18.3%), the best
    ///     single content signal reaches 39.8% and an IN-SAMPLE fitted mix of every
    ///     signal reaches 48.9%, against 20.9% for "fire every N measures". Half
    ///     the proposals would be wrong. If this test fails, read
    ///     <see cref="StructureGenerator" />'s summary before deleting it.
    /// </summary>
    [Fact]
    public void StructurePlan_CannotExpressAGuessedName()
    {
        var offenders = typeof(StructurePlan)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => CarriesAString(p.PropertyType))
            .Select(p => $"{p.PropertyType.Name} {p.Name}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "StructurePlan grew a member that can carry a NAME: " + string.Join(", ", offenders) +
            ". Naming a section from chart content was measured against TestData/EOF and lost to " +
            "'copy the previous section's name' — see StructureGenerator's summary.");
    }

    private static bool CarriesAString(Type type) =>
        type == typeof(string)
        || (type.IsGenericType && type.GetGenericArguments().Any(CarriesAString))
        || (type.IsArray && CarriesAString(type.GetElementType()!));

    [Fact]
    public void AnArrangementWithNoBeatsOrNoSoundsPlansNothingRatherThanThrowing()
    {
        Assert.Equal(0, StructureGenerator.Plan(new Arrangement(), []).Count);

        var empty = WithSounds();
        Section(empty, 0, "verse");
        Assert.Empty(StructureGenerator.Plan(empty, Beats()).NoGuitarSections);
    }
}
