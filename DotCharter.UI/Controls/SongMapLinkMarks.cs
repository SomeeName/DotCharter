using System.Globalization;
using Avalonia;
using Avalonia.Media;
using DotCharter.UI.Editing.Linking;

namespace DotCharter.UI.Controls;

/// <summary>One placement of the emphasised link group on the song map: its measures and the group's letter.</summary>
public readonly record struct SongMapLinkMark(Rect Bounds, string Letter, Color Color, LinkPlacement Placement);

/// <summary>
///     Linked measures on the song map (<see cref="MinimapOverviewControl" />): while a link group
///     is hovered or selected (<see cref="LinkHighlight.EmphasisGroupId" />), an outline over each
///     of its placements in the arrangement the map shows, with the group's letter in a chip at
///     the bottom-left corner. Nothing is drawn for any other group, and nothing when no group is
///     emphasised: the map carries no permanent link lines. Pure: the control draws exactly what
///     <see cref="Collect" /> answers.
/// </summary>
public static class SongMapLinkMarks
{
    /// <summary>Narrowest outline: a one-measure placement on a long song is still a mark.</summary>
    public const double MinWidth = 4;

    public const double ChipSize = 11;

    /// <summary>
    ///     Fills <paramref name="into" /> (cleared first) with a mark per placement of the group
    ///     <paramref name="emphasisGroupId" /> in <paramref name="arrangement" />, from
    ///     <paramref name="top" /> (under the section band) to <paramref name="height" />.
    /// </summary>
    public static void Collect(ChartProject? project, Arrangement? arrangement, string? emphasisGroupId, double scale,
        double top, double height, List<SongMapLinkMark> into)
    {
        into.Clear();
        if (project == null || arrangement == null || emphasisGroupId == null) return;
        LinkGroup? group = null;
        foreach (var g in project.LinkGroups)
        {
            if (g.Id != emphasisGroupId) continue;
            group = g;
            break;
        }

        if (group == null) return;
        var color = LinkLaneModel.ColorOf(project, group);
        var bandHeight = Math.Max(0, height - top);
        foreach (var placement in group.Placements)
        {
            if (!ReferenceEquals(placement.Arrangement, arrangement)) continue;
            var x = placement.Position.GetPositionMs(project.Beats) * scale;
            var w = Math.Max(MinWidth, placement.EndPosition.GetPositionMs(project.Beats) * scale - x);
            into.Add(new SongMapLinkMark(new Rect(x, top, w, bandHeight), group.Letter, color, placement));
        }
    }

    /// <summary>The letter chip of <paramref name="mark" />: inside its bottom-left corner.</summary>
    public static Rect ChipBounds(SongMapLinkMark mark) =>
        new(mark.Bounds.X + 2, mark.Bounds.Bottom - ChipSize - 2, ChipSize, ChipSize);
}

/// <summary>Draws <see cref="SongMapLinkMarks" />: brushes, pens and letters cached per colour and letter.</summary>
internal sealed class SongMapLinkPainter
{
    private static readonly Typeface LetterTypeface = new("Segoe UI", FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush LetterBrush = new SolidColorBrush(Color.FromRgb(20, 22, 30));

    private readonly Dictionary<Color, (IBrush Fill, IPen Outline, IBrush Chip)> _paints = [];
    private readonly Dictionary<string, FormattedText> _letters = new(StringComparer.Ordinal);

    public void Draw(DrawingContext context, IReadOnlyList<SongMapLinkMark> marks)
    {
        for (var i = 0; i < marks.Count; i++)
        {
            var mark = marks[i];
            var paint = PaintFor(mark.Color);
            context.DrawRectangle(paint.Fill, paint.Outline, mark.Bounds.Deflate(0.75));
            var chip = SongMapLinkMarks.ChipBounds(mark);
            if (chip.Right > mark.Bounds.Right) continue;
            context.DrawRectangle(paint.Chip, null, chip, 2, 2);
            var text = LetterFor(mark.Letter);
            context.DrawText(text, new Point(chip.X + (chip.Width - text.Width) / 2, chip.Y + (chip.Height - text.Height) / 2));
        }
    }

    private (IBrush Fill, IPen Outline, IBrush Chip) PaintFor(Color color)
    {
        if (_paints.TryGetValue(color, out var paint)) return paint;
        paint = (new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B)),
            new Pen(new SolidColorBrush(Color.FromArgb(235, color.R, color.G, color.B)), 1.5),
            new SolidColorBrush(color));
        _paints[color] = paint;
        return paint;
    }

    private FormattedText LetterFor(string letter)
    {
        if (_letters.TryGetValue(letter, out var text)) return text;
        text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LetterTypeface, 8.5,
            LetterBrush);
        _letters[letter] = text;
        return text;
    }
}
