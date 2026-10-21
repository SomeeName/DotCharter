using DotCharter.Tests.Services.State.Linking;
using DotCharter.UI.Controls;
using DotCharter.UI.Editing.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.UI.Linking;

/// <summary>
///     The song map's link marks: only the hovered or selected group, one outline per placement
///     of the arrangement the map shows, over exactly its measures; nothing when no group is emphasised.
/// </summary>
public sealed class SongMapLinkMarksTests
{
    private const double Scale = 0.05;
    private const double Top = 13;
    private const double Height = 60;

    [Fact]
    public void Marks_only_the_emphasised_groups_placements_in_the_shown_arrangement()
    {
        var h = LinkHarness.WithRhythm();
        var a = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 1));
        var b = h.Link((h.Lead, 7), (h.Rhythm, 7));
        var marks = new List<SongMapLinkMark>();
        var beats = h.Project.Beats;

        SongMapLinkMarks.Collect(h.Project, h.Lead, a.Id, Scale, Top, Height, marks);

        Assert.Equal(2, marks.Count);
        Assert.All(marks, m => Assert.Same(h.Lead, m.Placement.Arrangement));
        Assert.All(marks, m => Assert.Equal(a.Letter, m.Letter));
        Assert.All(marks, m => Assert.Equal(LinkLaneModel.ColorOf(h.Project, a), m.Color));
        Assert.Equal(At(1, 0).GetPositionMs(beats) * Scale, marks[0].Bounds.X, 6);
        Assert.Equal((At(2, 0).GetPositionMs(beats) - At(1, 0).GetPositionMs(beats)) * Scale, marks[0].Bounds.Width, 6);
        Assert.Equal(At(4, 0).GetPositionMs(beats) * Scale, marks[1].Bounds.X, 6);
        Assert.Equal(Top, marks[0].Bounds.Y);
        Assert.Equal(Height, marks[0].Bounds.Bottom);

        SongMapLinkMarks.Collect(h.Project, h.Rhythm, b.Id, Scale, Top, Height, marks);
        var only = Assert.Single(marks);
        Assert.Equal(b.Letter, only.Letter);
        Assert.Equal(At(7, 0).GetPositionMs(beats) * Scale, only.Bounds.X, 6);
    }

    [Fact]
    public void Nothing_is_marked_without_an_emphasised_group()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4));
        var marks = new List<SongMapLinkMark> { default };

        SongMapLinkMarks.Collect(h.Project, h.Lead, null, Scale, Top, Height, marks);
        Assert.Empty(marks);
        SongMapLinkMarks.Collect(h.Project, h.Lead, "gone", Scale, Top, Height, marks);
        Assert.Empty(marks);
    }

    [Fact]
    public void The_letter_chip_sits_inside_the_marks_bottom_left_corner()
    {
        var mark = new SongMapLinkMark(new Avalonia.Rect(100, Top, 40, Height - Top), "B", default, new LinkPlacement());

        var chip = SongMapLinkMarks.ChipBounds(mark);

        Assert.True(mark.Bounds.Contains(chip.TopLeft) && mark.Bounds.Contains(chip.BottomRight));
        Assert.True(chip.Bottom > mark.Bounds.Center.Y);
        Assert.True(chip.X < mark.Bounds.Center.X);
    }
}
