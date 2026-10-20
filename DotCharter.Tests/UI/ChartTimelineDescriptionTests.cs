using System.Globalization;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     The words the chart timeline's automation peer speaks (#858), built by the
///     pure <see cref="ChartTimelineDescription" />: selection + beats in, one
///     sentence out. The peer's own wiring is pinned headlessly by
///     <c>Perf/ChartRendererAutomationPeerUiPerfSmokeTests</c>.
/// </summary>
public sealed class ChartTimelineDescriptionTests
{
    private static readonly FakeLocalization English = FakeLocalization.English();

    [Fact]
    public void ASelectedChord_ReadsItsName_BarAndBeat_AndNoteCount()
    {
        var (arrangement, beats) = Chart();
        var selection = new SelectionService();
        // Beat index 46 in 4/4 = bar 12, beat 3.
        selection.SelectedChords.Add(ChordAt(46, templateId: 7));

        Assert.Equal("Chord Am, bar 12 beat 3, 4 notes",
            ChartTimelineDescription.DescribeSelection(selection, arrangement, beats, English));
    }

    [Fact]
    public void AnUntemplatedChord_IsStillAChord_WithoutAName()
    {
        var (arrangement, beats) = Chart();
        var selection = new SelectionService();
        selection.SelectedChords.Add(ChordAt(0, templateId: -1));

        Assert.Equal("Chord, bar 1 beat 1, 4 notes",
            ChartTimelineDescription.DescribeSelection(selection, arrangement, beats, English));
    }

    [Fact]
    public void NothingSelected_ReadsNoSelection() =>
        Assert.Equal("No selection",
            ChartTimelineDescription.DescribeSelection(new SelectionService(), null, Chart().Beats, English));

    [Fact]
    public void ASelectedNote_ReadsStringFretAndTechniques()
    {
        var (arrangement, beats) = Chart();
        var selection = new SelectionService();
        var note = new GuitarNote
        {
            Position = new FractionalPosition(2, new Fraction(1, 2)),
            StringIndex = 2,
            Fret = 5,
            Hopo = HopoType.HammerOn,
        };
        note.BendValues.Add(new BendValue());
        selection.SelectedNotes.Add(note);

        var beat = 3.5.ToString(CultureInfo.CurrentCulture);
        Assert.Equal($"Note, bar 1 beat {beat}, String 3, Fret 5, Bend, Hammer-on / pull-off",
            ChartTimelineDescription.DescribeSelection(selection, arrangement, beats, English));
    }

    [Fact]
    public void SeveralItems_ReadTheCount_AndWhereTheFirstSits()
    {
        var (arrangement, beats) = Chart();
        var selection = new SelectionService();
        selection.SelectedChords.Add(ChordAt(9, templateId: 7));
        selection.SelectedNotes.Add(new GuitarNote { Position = new FractionalPosition(4) });

        Assert.Equal("2 selected, first at bar 2 beat 1",
            ChartTimelineDescription.DescribeSelection(selection, arrangement, beats, English));
    }

    [Fact]
    public void TheSurface_IsNamedByArrangementAndPlayhead()
    {
        var (arrangement, beats) = Chart();

        // 500 ms per beat: 2,250 ms is inside beat index 4 = bar 2, beat 1.
        Assert.Equal("Chart timeline, Lead, bar 2 beat 1",
            ChartTimelineDescription.DescribeSurface(arrangement, beats, 2_250, English));
        Assert.Equal("Chart timeline", ChartTimelineDescription.DescribeSurface(null, beats, 0, English));
    }

    [Fact]
    public void BeforeTheFirstBeat_ThePlayheadReadsAsAClockTime()
    {
        var beats = new List<BeatMarker> { new() { Time = 5_000, IsMeasure = true } };

        Assert.Equal("1:02.5", ChartTimelineDescription.DescribeTime([], 62_500, English));
        Assert.Equal("0:01.0", ChartTimelineDescription.DescribeTime(beats, 1_000, English));
    }

    [Fact]
    public void TheHelpText_NamesTheUsersOwnBindings()
    {
        var keys = new KeybindConfig { SeekNext = new KeyChord(Avalonia.Input.Key.J) };

        var help = ChartTimelineDescription.DescribeKeys(keys, English);

        Assert.StartsWith(keys.SeekNext.ToDisplayString(English) + " / " + keys.SeekPrev.ToDisplayString(English), help);
        Assert.Contains(keys.SeekNextBeat.ToDisplayString(English), help);
    }

    [Fact]
    public void TheMainTimeline_IsTheTabStop_NotItsGrid()
    {
        // MainWindow.axaml: the chart is the element with the peer, so Tab must
        // land on it; the grid around it keeps click focus and the key routes.
        var markup = MainWindowSource.ReadAllText();
        var renderer = Element(markup, "x:Name=\"MainChartRenderer\"");
        var grid = Element(markup, "x:Name=\"TimelineGrid\"");
        Assert.Contains("Focusable=\"True\"", renderer);
        Assert.Contains("IsTabStop=\"False\"", grid);
    }

    private static string Element(string markup, string name)
    {
        var at = markup.IndexOf(name, StringComparison.Ordinal);
        Assert.True(at >= 0, $"MainWindow.axaml no longer declares {name}");
        var start = markup.LastIndexOf('<', at);
        return markup[start..markup.IndexOf('>', at)];
    }

    private static (Arrangement Arrangement, List<BeatMarker> Beats) Chart()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 7, Name = "Am" });
        var beats = Enumerable.Range(0, 64)
            .Select(i => new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 })
            .ToList();
        return (arrangement, beats);
    }

    private static Chord ChordAt(int beatId, int templateId)
    {
        var chord = new Chord { Position = new FractionalPosition(beatId), TemplateId = templateId };
        for (var s = 0; s < 4; s++) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 2 });
        return chord;
    }
}
