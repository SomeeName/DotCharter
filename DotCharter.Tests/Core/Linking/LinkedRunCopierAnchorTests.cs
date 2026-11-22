using DotCharter.Core.Linking;

namespace DotCharter.Tests.Core.Linking;

/// <summary>
///     The fret-hand position a propagated copy hands to what follows it: the copy replaces
///     the target run's anchors, so the target's own position at the run's end must be pinned there
///     or every unlinked note after the run moves to the source's last anchor.
/// </summary>
public sealed class LinkedRunCopierAnchorTests
{
    private static (ChartProject Project, ChartLevel Level) Chart()
    {
        var project = new ChartProject();
        foreach (var beat in LinkFixture.Beats(4, null)) project.Beats.Add(beat);
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        // Source run, bar 0: ends on fret 9.
        level.Anchors.Add(new FretAnchor(LinkFixture.At(0, 0), 5));
        level.Notes.Add(LinkFixture.Note(LinkFixture.At(0, 0), 0, 5, 0.5));
        level.Anchors.Add(new FretAnchor(LinkFixture.At(0, 2), 9));
        level.Notes.Add(LinkFixture.Note(LinkFixture.At(0, 2), 0, 9, 0.5));

        // Target run, bar 2: fret 5 throughout, and an unlinked note after it at beat 13.
        level.Anchors.Add(new FretAnchor(LinkFixture.At(2, 0), 5));
        level.Notes.Add(LinkFixture.Note(LinkFixture.At(2, 0), 0, 5, 0.5));
        level.Notes.Add(LinkFixture.Note(LinkFixture.At(3, 1), 1, 6, 0.5));
        return (project, level);
    }

    private static FretAnchor InEffect(ChartLevel level, FractionalPosition at) =>
        level.Anchors.Where(a => a.Position <= at).MaxBy(a => a.Position)!;

    private static LinkedRunCopy Plan(ChartProject project, ChartLevel level)
    {
        var arrangement = project.Arrangements[0];
        var index = MeasureContentIndex.ForHardestLevel(arrangement, project.Beats, MeasureContentOptions.Detection);
        return LinkedRunCopier.Plan(index, LinkFixture.At(0, 0), LinkFixture.At(1, 0), arrangement, level,
            LinkFixture.At(2, 0), project.Beats, MeasureContentOptions.Detection);
    }

    [Fact]
    public void A_copied_last_anchor_does_not_move_the_hand_of_the_note_after_the_run()
    {
        var (project, level) = Chart();
        var after = LinkFixture.At(3, 1);
        Assert.Equal(5, InEffect(level, after).Fret);

        Plan(project, level).Apply();

        Assert.Equal(9, InEffect(level, LinkFixture.At(2, 2)).Fret);
        Assert.Equal(5, InEffect(level, after).Fret);
    }

    [Fact]
    public void Revert_removes_the_pinned_end_anchor_again()
    {
        var (project, level) = Chart();
        var before = level.Anchors.Select(a => (a.Position, a.Fret)).OrderBy(a => a.Position).ToList();

        var copy = Plan(project, level);
        copy.Apply();
        copy.Revert();

        Assert.Equal(before, level.Anchors.Select(a => (a.Position, a.Fret)).OrderBy(a => a.Position).ToList());
    }

    [Fact]
    public void Nothing_is_pinned_when_no_sound_follows_the_run()
    {
        var (project, level) = Chart();
        level.Notes.Remove(level.Notes.Single(n => n.Position == LinkFixture.At(3, 1)));

        Plan(project, level).Apply();

        Assert.DoesNotContain(level.Anchors, a => a.Position == LinkFixture.At(3, 0));
    }
}
