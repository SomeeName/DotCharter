using DotCharter.Core.Diff;

namespace DotCharter.Tests.Core;

/// <summary>
///     <c>ChartDiffResult.IsEmpty</c> is the documented LAN "has update"
///     signal, so a field the diff cannot see is a field that lets a peer be
///     told a changed chart is unchanged. Several real, exportable authoring
///     decisions were simply never compared.
/// </summary>
public sealed class ChartDiffCoverageTests
{
    private static readonly FieldExclusion[] Exclusions =
    [
        new(typeof(GuitarNote), nameof(GuitarNote.IgnoreDerived), "Save-time provenance, not an authored technique."),
        new(typeof(GuitarNote), nameof(GuitarNote.GraceSlide), "Import-transient hint consumed before the chart is authored."),
        new(typeof(Chord), nameof(Chord.IgnoreDerived), "Save-time provenance, not an authored technique.")
    ];

    [Fact]
    public void EveryExcludedFieldStillExistsAndHasAReason()
    {
        foreach (var exclusion in Exclusions)
        {
            Assert.NotNull(exclusion.Owner.GetProperty(exclusion.Property));
            Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason));
        }
    }

    [Theory]
    [InlineData(typeof(GuitarNote))]
    [InlineData(typeof(Chord))]
    [InlineData(typeof(ChordNote))]
    public void EveryAuthoredSoundFieldIsCompared(Type type)
    {
        foreach (var property in DomainFieldProbe.SettableProperties(type))
        {
            if (Exclusions.Any(e => e.Owner == type && e.Property == property.Name)) continue;
            var oldSound = DomainFieldProbe.Populated(type);
            var newSound = DomainFieldProbe.Populated(type);
            var oldArrangement = ArrangementWith(oldSound);
            var newArrangement = ArrangementWith(newSound);
            Assert.True(ChartDiffEngine.Diff(oldArrangement, newArrangement).IsEmpty);

            property.SetValue(newSound, DomainFieldProbe.Alternate(property.PropertyType));

            Assert.False(ChartDiffEngine.Diff(oldArrangement, newArrangement).IsEmpty,
                $"The diff missed {type.Name}.{property.Name}.");
        }
    }

    private static Arrangement ArrangementWith(object sound)
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        switch (sound)
        {
            case GuitarNote note:
                level.Notes.Add(note);
                break;
            case Chord chord:
                level.Chords.Add(chord);
                break;
            case ChordNote chordNote:
                var strum = new Chord();
                strum.ChordNotes.Add(chordNote);
                level.Chords.Add(strum);
                break;
            default:
                throw new ArgumentException("Expected a guitar sound.", nameof(sound));
        }

        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static (Arrangement Old, Arrangement New) Pair(Action<GuitarNote> mutate)
    {
        static Arrangement Build()
        {
            var arrangement = new Arrangement { Name = "Lead" };
            var level = new ChartLevel();
            level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 5 });
            arrangement.Levels.Add(level);
            return arrangement;
        }

        var oldArr = Build();
        var newArr = Build();
        mutate(newArr.Levels[0].Notes[0]);
        return (oldArr, newArr);
    }

    private static (Arrangement Old, Arrangement New) ChordPair(Action<Chord> mutate)
    {
        static Arrangement Build()
        {
            var arrangement = new Arrangement { Name = "Lead" };
            var level = new ChartLevel();
            var chord = new Chord { TemplateId = 0 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
            level.Chords.Add(chord);
            arrangement.Levels.Add(level);
            return arrangement;
        }

        var oldArr = Build();
        var newArr = Build();
        mutate(newArr.Levels[0].Chords[0]);
        return (oldArr, newArr);
    }

    [Fact]
    public void AnIdenticalChartStillDiffsAsEmpty()
    {
        var (a, b) = Pair(_ => { });

        Assert.True(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void PassOtherNotesOnANoteIsSeen()
    {
        var (a, b) = Pair(n => n.PassOtherNotes = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void LinkNextOnANoteIsSeen()
    {
        var (a, b) = Pair(n => n.LinkNext = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void SplitIntoNotesOnAChordIsSeen()
    {
        var (a, b) = ChordPair(c => c.SplitIntoNotes = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void ForceNoNotesOnAChordIsSeen()
    {
        var (a, b) = ChordPair(c => c.ForceNoNotes = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void PerStringAccentIsSeen()
    {
        // Only the CHORD-level accent was compared, so accenting one string of
        // a strum reported no change at all.
        var (a, b) = ChordPair(c => c.ChordNotes[1].Accent = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void PerStringIgnoreIsSeen()
    {
        var (a, b) = ChordPair(c => c.ChordNotes[0].Ignore = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }
}
