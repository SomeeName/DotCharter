using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P5, bug (a): a chord's bend applied through the Bends page reaches every target of the "Apply to" rung and
///     lands on EACH target's own onset and sustain — the same shape, never the source's absolute positions. Driven
///     through the page's own path: the curve editor <see cref="StagedBendsViewModel.BuildEditor" /> builds for the
///     inspector, a preset (which only stages), then the footer's Apply and Confirm.
/// </summary>
public sealed class BendScopeApplyTests
{
    private const int Strand = 1;

    public static TheoryData<ChordApplyRung, int> Rungs() => new()
    {
        { ChordApplyRung.Selected, 2 },
        { ChordApplyRung.EveryUse, 5 },
        { ChordApplyRung.SameGrip, 8 }
    };

    [Theory]
    [MemberData(nameof(Rungs))]
    public void AScopedBend_LandsOnEachTargetsOwnOnsetAndSustain(ChordApplyRung rung, int expected)
    {
        var f = new ChordLadderFixture();
        // Different sustains (each shorter than the beat to the next chord, so no bend is cut short by its neighbour).
        var k = 0;
        foreach (var chord in f.AllChords)
        {
            foreach (var note in chord.ChordNotes) note.Sustain = new FractionalPosition(new Fraction(1 + k % 3, 4));
            k++;
        }

        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.Chord, HasChords = true };
        sections.ChooseCommand.Execute(SoundSection.Bends);
        using var pages = InspectorStagedPagesTests.Pages(f, sections);
        f.Choose(rung);
        var source = f.First;
        var strand = source.ChordNotes.Single(n => n.StringIndex == Strand);
        var editor = pages.Bends.BuildEditor(source, Strand, () => source.Position, () => source.Position + strand.Sustain);
        var steps = f.History.GetUndoSteps().Count;

        editor.PresetBendReleaseCommand.Execute(null);
        Assert.All(f.AllChords, c => Assert.All(c.ChordNotes, n => Assert.Empty(n.BendValues)));
        Assert.True(pages.Footer.Count >= 1, "the preset staged nothing");

        pages.Footer.ApplyCommand.Execute(null);
        if (pages.Footer.IsReviewing) pages.Footer.ConfirmCommand.Execute(null);

        var bent = f.AllChords.Where(c => c.ChordNotes.Any(n => n.BendValues.Count > 0)).ToList();
        Assert.Equal(expected, bent.Count);
        Assert.Equal(steps + 1, f.History.GetUndoSteps().Count);
        var shape = Shape(f, source);
        Assert.True(shape.Count >= 3 && shape.Max(p => p.Step) > 0, "the source carries no bend-and-release");
        foreach (var target in bent)
        {
            Assert.All(target.ChordNotes.Where(n => n.StringIndex != Strand), n => Assert.Empty(n.BendValues));
            var landed = Shape(f, target);
            Assert.Equal(shape.Count, landed.Count);
            foreach (var (want, got) in shape.Zip(landed))
            {
                Assert.Equal(want.Step, got.Step, 6);
                Assert.True(Math.Abs(want.At - got.At) < 1e-6,
                    $"a point lands at {got.At:0.###} of the target's sustain, the source's at {want.At:0.###}");
            }
        }
    }

    /// <summary>The curve on the strand as (where in the sound's own sustain, 0..1, step).</summary>
    private static List<(double At, double Step)> Shape(ChordLadderFixture f, Chord chord)
    {
        var note = chord.ChordNotes.Single(n => n.StringIndex == Strand);
        var onset = chord.Position.GetPositionMs(f.Project.Beats);
        var length = (chord.Position + note.Sustain).GetPositionMs(f.Project.Beats) - onset;
        return [.. note.BendValues.Select(v => ((v.Position.GetPositionMs(f.Project.Beats) - onset) / length, v.Step))];
    }
}
