using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     "Muted strums → muted chord" (#566), the pure planner. The owner's pattern from a Guitar Pro
///     tab: a chord with a one-chord hand shape, then fret-hand-muted single notes on ONE of its
///     strings; they become muted copies of the chord on its template, inside its hand shape, and
///     ship as Rocksmith repeat boxes. Every plan is committed through a real history.
/// </summary>
public sealed class MutedStrumChordsTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly Arrangement _arrangement = new() { Name = "Rhythm", StringCount = 6 };
    private readonly ChartLevel _level = new();
    private readonly ChordTemplate _six = new() { Id = 0, Name = "D5", Frets = new() { [1] = 6, [2] = 6 } };
    private readonly ChordTemplate _five = new() { Id = 1, Name = "", Frets = new() { [1] = 5, [2] = 6 } };

    public MutedStrumChordsTests()
    {
        _arrangement.ChordTemplates.Add(_six);
        _arrangement.ChordTemplates.Add(_five);
        _arrangement.Levels.Add(_level);
    }

    private static FractionalPosition B(int beat) => new(beat);

    private static FractionalPosition Half(int beat) => new(beat, new Fraction(1, 2));

    private Chord AddChord(ChordTemplate template, int beat, bool bar = true)
    {
        var chord = new Chord { Position = B(beat), TemplateId = template.Id };
        foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        _level.Chords.Add(chord);
        if (bar) _level.HandShapes.Add(new HandShape { Position = B(beat), EndPosition = Half(beat), TemplateId = template.Id });
        return chord;
    }

    private GuitarNote AddNote(int beat, int stringIndex, int fret, MuteType mute = MuteType.Full)
    {
        var note = new GuitarNote { Position = B(beat), StringIndex = stringIndex, Fret = fret, Mute = mute };
        _level.Notes.Add(note);
        return note;
    }

    /// <summary>The owner's screenshot: 6/6, two muted 6s on the D string, 6/5, two more, then 6/6 again.</summary>
    private void OwnersPattern()
    {
        AddChord(_six, 0);
        AddNote(1, 2, 6);
        AddNote(2, 2, 6);
        AddChord(_five, 3);
        AddNote(4, 2, 6);
        AddNote(5, 2, 6);
        AddChord(_six, 6);
    }

    private MutedStrumChordsPlan PlanAll() =>
        MutedStrumChords.Plan(_arrangement, _level, _level.Notes.ToHashSet(), _ => true);

    private MutedStrumChordsPlan Commit(MutedStrumChordsPlan plan)
    {
        Assert.False(plan.IsEmpty);
        Assert.True(_history.Do(plan.Action!));
        return plan;
    }

    private Chord ChordAt(int beat) => _level.Chords.Single(c => c.Position == B(beat));

    private HandShape BarAt(int beat) => _level.HandShapes.Single(h => h.Position == B(beat));

    [Fact]
    public void OwnersPattern_MutedStrumsBecomeMutedCopiesOnTheSameTemplate_InsideTheChordsHandShape()
    {
        OwnersPattern();

        var plan = Commit(PlanAll());

        Assert.Equal(4, plan.Converted);
        Assert.Empty(_level.Notes);
        foreach (var (beat, template) in new[] { (1, _six), (2, _six), (4, _five), (5, _five) })
        {
            var copy = ChordAt(beat);
            Assert.Equal(template.Id, copy.TemplateId);
            Assert.Equal(template.Frets, ChordTemplateResolver.FretsOf(copy));
            Assert.True(copy.IsFullyMuted());
        }

        // Each bar now reaches the next DIFFERENT chord, and stops there.
        Assert.Equal(B(3), BarAt(0).EndPosition);
        Assert.Equal(B(6), BarAt(3).EndPosition);
        Assert.Equal(Half(6), BarAt(6).EndPosition);
        Assert.Equal(3, _level.HandShapes.Count);
        Assert.Equal(2, plan.HandShapesExtended);
        Assert.Single(_history.GetUndoSteps());
    }

    [Fact]
    public void TemplatesAreShared_NeverMinted()
    {
        OwnersPattern();
        var before = _arrangement.ChordTemplates.ToList();

        var plan = Commit(PlanAll());

        Assert.Equal(0, plan.MintedTemplates);
        Assert.Equal(before, _arrangement.ChordTemplates);
        Assert.Same(_six, _arrangement.ChordTemplates[0]);
        Assert.Equal(new Dictionary<int, int> { [1] = 6, [2] = 6 }, _six.Frets);
    }

    [Fact]
    public void AnUntemplatedChord_MintsItsGripOnce_SharedByEveryCopy()
    {
        var loose = new Chord { Position = B(0), TemplateId = -1 };
        loose.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 9 });
        loose.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 9 });
        _level.Chords.Add(loose);
        AddNote(1, 3, 9);
        AddNote(2, 4, 9);

        var plan = Commit(PlanAll());

        Assert.Equal(1, plan.MintedTemplates);
        var minted = _arrangement.ChordTemplates.Last();
        Assert.Equal(minted.Id, ChordAt(1).TemplateId);
        Assert.Equal(minted.Id, ChordAt(2).TemplateId);
        Assert.Equal(-1, loose.TemplateId);
        Assert.Equal(minted.Id, BarAt(0).TemplateId);
    }

    [Fact]
    public void AChordWithNoStrumsAfterIt_MintsNothing()
    {
        var loose = new Chord { Position = B(0), TemplateId = -1 };
        loose.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 9 });
        loose.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 9 });
        _level.Chords.Add(loose);
        AddChord(_six, 2);
        AddNote(3, 2, 6);

        var plan = Commit(PlanAll());

        Assert.Equal(0, plan.MintedTemplates);
        Assert.Equal(2, _arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void AMutedNoteOnAStringTheChordDoesNotPlay_IsLeftAlone()
    {
        AddChord(_six, 0);
        var stray = AddNote(1, 4, 6);

        var plan = PlanAll();

        Assert.True(plan.IsEmpty);
        Assert.Contains(stray, _level.Notes);
    }

    [Fact]
    public void AnUnmutedOrPalmMutedSingleNote_IsLeftAlone()
    {
        AddChord(_six, 0);
        AddNote(1, 2, 6, MuteType.None);
        AddChord(_six, 3);
        AddNote(4, 2, 6, MuteType.Palm);

        Assert.True(PlanAll().IsEmpty);
    }

    [Fact]
    public void AMutedNoteWithAStringTechnique_IsLeftAlone()
    {
        AddChord(_six, 0);
        var note = AddNote(1, 2, 6);
        note.SlideTo = 8;

        Assert.True(PlanAll().IsEmpty);
    }

    [Fact]
    public void ANoteOutsideTheCandidates_IsLeftAlone_AndEndsTheRun()
    {
        AddChord(_six, 0);
        var outside = AddNote(1, 2, 6);
        var inside = AddNote(2, 2, 6);

        var plan = MutedStrumChords.Plan(_arrangement, _level, new HashSet<GuitarNote> { inside }, _ => true);

        Assert.True(plan.IsEmpty);
        Assert.Contains(outside, _level.Notes);
    }

    [Fact]
    public void WithNoBarOnTheChord_ANewBarOnItsTemplate_StopsBeforeTheNextBar()
    {
        AddChord(_six, 0, bar: false);
        AddNote(1, 2, 6);
        AddNote(2, 2, 6);
        var next = new HandShape { Position = Half(2), EndPosition = B(4), TemplateId = _five.Id };
        _level.HandShapes.Add(next);
        AddChord(_five, 3, bar: false);

        var plan = Commit(PlanAll());

        Assert.Equal(1, plan.HandShapesAdded);
        var bar = BarAt(0);
        Assert.Equal(_six.Id, bar.TemplateId);
        Assert.Equal("D5", bar.Name);
        Assert.Equal(Half(2), bar.EndPosition);
        Assert.Equal(Half(2), next.Position);
        AssertNoBarsOverlap();
    }

    [Fact]
    public void ABarSpansAnIdenticalChordAndItsStrums_UpToTheNextDifferentChord()
    {
        AddChord(_six, 0);
        AddNote(1, 2, 6);
        AddChord(_six, 2, bar: false);
        AddNote(3, 2, 6);
        AddChord(_five, 4);

        var plan = Commit(PlanAll());

        Assert.Equal(2, plan.Converted);
        Assert.Equal(B(4), BarAt(0).EndPosition);
        Assert.Equal(2, _level.HandShapes.Count);
        AssertNoBarsOverlap();
    }

    [Fact]
    public void TheConvertedStrums_ShipAsRepeatBoxes()
    {
        OwnersPattern();
        Commit(PlanAll());

        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
        var repeats = RepeatChords.Compute(_arrangement.ChordTemplates, _level.Notes, _level.Chords, _level.HandShapes, beats);

        foreach (var beat in new[] { 1, 2, 4, 5 }) Assert.Contains(ChordAt(beat), repeats);
        foreach (var beat in new[] { 0, 3, 6 }) Assert.DoesNotContain(ChordAt(beat), repeats);
    }

    [Fact]
    public void OneUndoRestoresEverything_AndRedoReappliesIt()
    {
        OwnersPattern();
        var before = Dump();

        Commit(PlanAll());
        var after = Dump();
        Assert.NotEqual(before, after);

        _history.Undo();
        Assert.Equal(before, Dump());
        Assert.False(_history.CanUndo);

        _history.Redo();
        Assert.Equal(after, Dump());
    }

    [Fact]
    public void NothingToDo_IsAnEmptyPlan()
    {
        AddChord(_six, 0);
        AddChord(_five, 1);

        var plan = PlanAll();

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.Converted);
    }

    private void AssertNoBarsOverlap()
    {
        var bars = _level.HandShapes.OrderBy(h => h.Position).ToList();
        for (var i = 1; i < bars.Count; i++) Assert.True(bars[i - 1].EndPosition <= bars[i].Position);
    }

    /// <summary>Every field the command touches: notes, chords (with their strings), bars and templates.</summary>
    private string Dump()
    {
        var notes = _level.Notes.OrderBy(n => n.Position).ThenBy(n => n.StringIndex)
            .Select(n => $"N {n.Position} s{n.StringIndex} f{n.Fret} {n.Mute} {n.Sustain} a{n.Accent} i{n.Ignore}");
        var chords = _level.Chords.OrderBy(c => c.Position)
            .Select(c => $"C {c.Position} t{c.TemplateId} a{c.Accent} i{c.Ignore} " + string.Join(",",
                c.ChordNotes.Select(n => $"{n.StringIndex}:{n.Fret}:{n.Mute}:{n.Sustain}:{n.Accent}")));
        var bars = _level.HandShapes.OrderBy(h => h.Position).Select(h => $"H {h.Position}-{h.EndPosition} t{h.TemplateId} {h.Name}");
        var templates = _arrangement.ChordTemplates.Select(t =>
            $"T {t.Id} {t.Name} " + string.Join(",", t.Frets.OrderBy(f => f.Key).Select(f => $"{f.Key}:{f.Value}")));
        return string.Join("\n", notes.Concat(chords).Concat(bars).Concat(templates));
    }
}
