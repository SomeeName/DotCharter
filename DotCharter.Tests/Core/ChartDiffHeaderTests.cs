using DotCharter.Core.Diff;

namespace DotCharter.Tests.Core;

/// <summary>
///     The arrangement HEADER — tuning, capo, cent offset, string count,
///     type/subtype, scroll speed, tone slots — was compared by nothing, so a
///     retuned or capo'd chart diffed as IDENTICAL. That is not merely a blank
///     dialog: <c>ChartDiffResult.IsEmpty</c> is the documented LAN "has update"
///     signal, and the sound pass can never cover this — the frets and strings
///     really are unchanged, only their meaning is.
/// </summary>
public sealed class ChartDiffHeaderTests
{
    private static Arrangement Build()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 5 });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static ChartStructureDiff HeaderEntryFor(Action<Arrangement> mutate)
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        mutate(newArrangement);

        var result = ChartDiffEngine.Diff(oldArrangement, newArrangement);

        Assert.False(result.IsEmpty);
        // The notes are untouched, so this must be reported as a HEADER change
        // and not smeared across every sound in the chart.
        Assert.True(result.SoundsIdentical);
        return Assert.Single(result.Structure, e => e.Kind == ChartDiffElementKind.ArrangementHeader);
    }

    [Fact]
    public void AnUnchangedHeaderProducesNoEntry()
    {
        var result = ChartDiffEngine.Diff(Build(), Build());

        Assert.True(result.IsEmpty);
        Assert.DoesNotContain(result.Structure, e => e.Kind == ChartDiffElementKind.ArrangementHeader);
    }

    [Fact]
    public void ARetuneIsSeen()
    {
        var entry = HeaderEntryFor(a => a.Tuning = ["D", "A", "D", "G", "B", "e"]);

        Assert.Contains("Tuning", entry.ChangedFields);
        Assert.Equal(ChartDiffChange.Changed, entry.Change);
    }

    [Fact]
    public void ARetuneThatOnlyMovesThePitchOffsetsIsSeen()
    {
        // Same string NAMES, different semitone offsets — the half a spelling
        // comparison alone would miss.
        var entry = HeaderEntryFor(a => a.TuningPitches[0] = new TuningOffset(-2));

        Assert.Contains("Tuning", entry.ChangedFields);
    }

    [Fact]
    public void ACapoIsSeen()
    {
        var entry = HeaderEntryFor(a => a.Capo = 3);

        Assert.Contains("Capo", entry.ChangedFields);
        Assert.Contains("capo 3", entry.Label);
    }

    [Theory]
    [InlineData("CentOffset")]
    [InlineData("StringCount")]
    [InlineData("ArrangementType")]
    [InlineData("Subtype")]
    [InlineData("PickedBass")]
    [InlineData("ScrollSpeed")]
    [InlineData("Tones")]
    public void EveryHeaderFieldTheDiffClaimsToCoverIsActuallyCompared(string field)
    {
        var entry = HeaderEntryFor(a =>
        {
            switch (field)
            {
                case "CentOffset": a.CentOffset = -10; break;
                case "StringCount": a.StringCount = 7; break;
                case "ArrangementType": a.ArrangementType = ArrangementType.Rhythm; break;
                case "Subtype": a.Subtype = ArrangementSubtype.Bonus; break;
                case "PickedBass": a.PickedBass = true; break;
                case "ScrollSpeed": a.ScrollSpeed = 2.0; break;
                case "Tones": a.ToneBase = "crunch"; break;
                default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }
        });

        Assert.Contains(field, entry.ChangedFields);
    }

    [Fact]
    public void TheHeaderEntryIsOneEntryNamingEveryChangedField()
    {
        var entry = HeaderEntryFor(a =>
        {
            a.Capo = 2;
            a.CentOffset = -10;
        });

        Assert.Equal(2, entry.ChangedFields.Count);
        Assert.Equal(-1, entry.DifficultyIndex); // arrangement-scoped
    }

    /// <summary>The two sides are PAIRED by name, so a differing name is the
    /// premise of the comparison rather than a finding.</summary>
    [Fact]
    public void ADifferentArrangementNameIsNotAHeaderChange()
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        newArrangement.Name = "Rhythm";

        Assert.True(ChartDiffEngine.Diff(oldArrangement, newArrangement).IsEmpty);
    }
}
