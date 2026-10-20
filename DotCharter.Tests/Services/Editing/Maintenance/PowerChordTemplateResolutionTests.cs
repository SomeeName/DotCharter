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
