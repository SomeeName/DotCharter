namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     "Convert to power chord" open-coded its own find-or-create over
///     <see cref="ChordTemplate.Equals" /> instead of going through
///     <c>ChordTemplateResolver</c> — the one documented editing resolver, whose
///     key is FRETS within one arpeggio flavour.
///     <para>
///         <c>Equals</c> is VALUE equality over every field, NAME and FINGERS
///         included, so the shape the chart already declares as "A5" with the
///         charter's fingering on it never matched the bare shape the tool
///         built: the conversion minted a SECOND, nameless, unfingered template
///         at the identical frets and pointed the new chord at that one.
///     </para>
/// </summary>
public sealed class PowerChordTemplateResolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotesAtOnePosition_BecomeOneChordAndKeepTheirAuthoredTechniques(bool big)
    {
        var (h, arrangement, root) = Setup();
        var level = arrangement.Levels[0];
        var fifth = new GuitarNote { Position = root.Position, StringIndex = 1, Fret = 7, Vibrato = true };
        level.Notes.Add(fifth);
        h.Selection.SelectedNotes.Add(fifth);

        h.Maintenance.ConvertSelectedNotesToPowerChord(big);

        var chord = Assert.Single(level.Chords);
        Assert.Empty(level.Notes);
        Assert.Equal(big ? 3 : 2, chord.ChordNotes.Count);
        Assert.True(chord.ChordNotes.Single(n => n.StringIndex == fifth.StringIndex).Vibrato);
        Assert.Equal(chord.ChordNotes.Count, chord.ChordNotes.Select(n => n.StringIndex).Distinct().Count());
        Assert.True(arrangement.FindChordTemplate(chord.TemplateId)!.DescribesChordNotes(chord.ChordNotes));
        Assert.Same(chord, Assert.Single(h.Selection.SelectedChords));
        h.History.Undo();
        Assert.Empty(level.Chords);
        Assert.Contains(root, level.Notes);
        Assert.Contains(fifth, level.Notes);
    }

    [Fact]
    public void AnExistingChordAtTheConvertedPosition_IsCombinedInTheSameUndoStep()
    {
        var (h, arrangement, root) = Setup();
        var level = arrangement.Levels[0];
        var existing = new Chord { Position = root.Position, Accent = true };
        existing.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 6, Vibrato = true });
        level.Chords.Add(existing);

        h.Maintenance.ConvertSelectedNotesToPowerChord(big: false);

        var chord = Assert.Single(level.Chords);
        Assert.True(chord.Accent);
        Assert.Equal(3, chord.ChordNotes.Count);
        Assert.True(chord.ChordNotes.Single(n => n.StringIndex == 3).Vibrato);
        Assert.True(arrangement.FindChordTemplate(chord.TemplateId)!.DescribesChordNotes(chord.ChordNotes));
        h.History.Undo();
        Assert.Same(existing, Assert.Single(level.Chords));
        Assert.Same(root, Assert.Single(level.Notes));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ReplacingAnExistingChord_RemovesItsStrandedTemplateAndUndoRestoresIt()
    {
        var (h, arrangement, root) = Setup();
        var level = arrangement.Levels[0];
        var originalTemplate = new ChordTemplate
        {
            Id = 0,
            Frets = { [3] = 6, [4] = 8 }
        };
        arrangement.ChordTemplates.Add(originalTemplate);
        var existing = new Chord { Position = root.Position, TemplateId = originalTemplate.Id };
        existing.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 6 });
        existing.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 8 });
        level.Chords.Add(existing);

        h.Maintenance.ConvertSelectedNotesToPowerChord(big: false);

        var combined = Assert.Single(level.Chords);
        var combinedTemplate = Assert.Single(arrangement.ChordTemplates);
        Assert.NotSame(originalTemplate, combinedTemplate);
        Assert.Equal(combinedTemplate.Id, combined.TemplateId);
        Assert.True(combinedTemplate.DescribesChordNotes(combined.ChordNotes));
        Assert.Empty(level.Notes);

        h.History.Undo();

        Assert.Same(originalTemplate, Assert.Single(arrangement.ChordTemplates));
        Assert.Equal(0, originalTemplate.Id);
        Assert.Same(existing, Assert.Single(level.Chords));
        Assert.Equal(0, existing.TemplateId);
        Assert.Same(originalTemplate, arrangement.FindChordTemplate(existing.TemplateId));
        Assert.Same(root, Assert.Single(level.Notes));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void ASelectedUpperStringAtTheRootPosition_IsKeptAsAnAuthoredChordNote()
    {
        var (h, arrangement, root) = Setup();
        var level = arrangement.Levels[0];
        var upper = new GuitarNote { Position = root.Position, StringIndex = 5, Fret = 8, Vibrato = true };
        level.Notes.Add(upper);
        h.Selection.SelectedNotes.Add(upper);

        h.Maintenance.ConvertSelectedNotesToPowerChord(big: false);

        var chord = Assert.Single(level.Chords);
        Assert.Empty(level.Notes);
        Assert.Equal(3, chord.ChordNotes.Count);
        var kept = Assert.Single(chord.ChordNotes, n => n.StringIndex == upper.StringIndex);
        Assert.Equal(upper.Fret, kept.Fret);
        Assert.True(kept.Vibrato);
        Assert.True(arrangement.FindChordTemplate(chord.TemplateId)!.DescribesChordNotes(chord.ChordNotes));
    }

    [Fact]
    public void ConvertingANote_AdoptsTheTemplateTheChartAlreadyDeclaresForThatShape()
    {
        var (h, arrangement, note) = Setup();
        var existing = new ChordTemplate
        {
            Id = 0,
            Name = "A5",
            Frets = { [0] = 5, [1] = 7 },
            Fingers = { [0] = 1, [1] = 3 }
        };
        arrangement.ChordTemplates.Add(existing);

        h.Maintenance.ConvertSelectedNotesToPowerChord(big: false);

        var chord = Assert.Single(arrangement.Levels[0].Chords);
        Assert.Same(existing, Assert.Single(arrangement.ChordTemplates));
        Assert.Equal(existing.Id, chord.TemplateId);
        _ = note;
    }

    [Fact]
    public void AMintedPowerChordTemplate_ArrivesChartingReady()
    {
        var (h, arrangement, _) = Setup();

        h.Maintenance.ConvertSelectedNotesToPowerChord(big: false);

        var chord = Assert.Single(arrangement.Levels[0].Chords);
        var template = Assert.Single(arrangement.ChordTemplates);
        Assert.Equal(template.Id, chord.TemplateId);
        // The resolver runs ChordTemplateFingerSetter over a minted template;
        // the hand-rolled copy left every shape it created unfingered.
        Assert.NotEmpty(template.Fingers);
    }

    [Fact]
    public void ConvertingTwoNotesOnTheSameShape_MintsOneTemplate()
    {
        var (h, arrangement, first) = Setup();
        var level = arrangement.Levels[0];
        var second = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 5 };
        level.Notes.Add(second);
        h.Selection.SelectedNotes.Add(second);

        h.Maintenance.ConvertSelectedNotesToPowerChord(big: false);

        Assert.Equal(2, level.Chords.Count);
        var template = Assert.Single(arrangement.ChordTemplates);
        Assert.All(level.Chords, c => Assert.Equal(template.Id, c.TemplateId));
        _ = first;
    }

    private static (ChartPowerToolsTests.Harness H, Arrangement Arrangement, GuitarNote Note) Setup()
    {
        var h = new ChartPowerToolsTests.Harness();
        var arrangement = h.State.ActiveArrangement!;
        for (var i = 0; i < arrangement.StringCount; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var level = arrangement.Levels[0];
        level.Notes.Clear();
        var note = new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 5 };
        level.Notes.Add(note);
        h.Selection.SelectedNotes.Add(note);
        return (h, arrangement, note);
    }
}
