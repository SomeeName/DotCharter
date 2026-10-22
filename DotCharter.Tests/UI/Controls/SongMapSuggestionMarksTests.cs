using DotCharter.Services.Editing.HandShapes;
using DotCharter.Tests.Services.Editing.Highway;
using DotCharter.UI.Controls;
using static DotCharter.Tests.Services.Editing.HandShapes.ChordHandShapePlannerTests;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The song map's marks for OPEN hand-shape suggestions (#639): one band per bar the lane's
///     <see cref="HandShapeSuggestionLatch" /> answers, at the bar's span, under the section band —
///     the same latch and the same <see cref="SongMapMarks.CollectSuggestions" /> call
///     <c>MinimapOverviewControl.DrawSuggestions</c> makes. The marks follow the list: a new run
///     adds one, an accept removes it (and its Undo brings it back), the View toggle off clears them.
///     <see cref="DotCharter.Tests.Perf.MinimapUseMarksUiPerfSmokeTests" /> proves the real control
///     draws them.
/// </summary>
public sealed class SongMapSuggestionMarksTests
{
    private const double BeatMs = 500;
    private const double Scale = 0.01; // px per ms
    private const double Top = 13;
    private const double Height = 52;

    private static readonly (int String, int Fret)[] COpenArpeggio =
        [(1, 3), (2, 2), (3, 0), (4, 1), (5, 0), (4, 1), (3, 0), (2, 2)];

    private static readonly (int String, int Fret)[] AmAt5 = [(1, 7), (2, 7), (3, 5), (4, 5)];

    private readonly List<BeatMarker> _beats =
        [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * BeatMs, Bpm = 120 })];

    private readonly ChordKeyboardMutationTests.Harness _h = new();
    private readonly HandShapeSuggestionLatch _latch = new();
    private readonly List<SongMapMark> _marks = [];
    private HandShapeSuggestionSet _set = HandShapeSuggestionSet.Empty;
    private long _version;

    private ChartLevel Level => _h.Arrangement.Levels[0];

    private void Frame(bool enabled = true)
    {
        _set = _latch.Get(_h.Arrangement, Level, enabled, _version, 0);
        SongMapMarks.CollectSuggestions(_set.Bars, _beats, Scale, Top, Height, _marks);
    }

    [Fact]
    public void ASuggestion_IsABandAtItsSpan_UnderTheSectionBand()
    {
        Pick(0, COpenArpeggio);

        Frame();

        var band = Assert.Single(_marks).Bounds;
        var suggestion = Assert.Single(_set.Suggestions);
        Assert.Equal(suggestion.Position.GetPositionMs(_beats) * Scale, band.Left, 6);
        Assert.Equal(suggestion.EndPosition.GetPositionMs(_beats) * Scale, band.Right, 6);
        Assert.Equal(Top, band.Top, 6);
        Assert.Equal(Height, band.Bottom, 6);
    }

    [Fact]
    public void TheMarks_FollowTheList_AddAcceptUndoAndTheViewToggle()
    {
        Pick(0, COpenArpeggio);
        Frame();
        Assert.Single(_marks);

        // Add: a second run of single notes is a second suggestion.
        Pick(6, AmAt5);
        _version++;
        Frame();
        Assert.Equal(2, _marks.Count);
        var am = _marks[1].Bounds;

        // Accept the first: its band goes, the other stays where it was.
        var acceptor = new HandShapeSuggestionAcceptor(_h.History, new FakeAppLogger());
        Assert.Equal(1, acceptor.Accept(_h.Arrangement, Level, [_set.Suggestions[0]], arpeggio: false));
        _version++;
        Frame();
        Assert.Equal(am, Assert.Single(_marks).Bounds);

        // Undo brings it back.
        _h.History.Undo();
        _version++;
        Frame();
        Assert.Equal(2, _marks.Count);

        // View ▸ Show suggested hand shapes off: nothing to mark.
        Frame(enabled: false);
        Assert.Empty(_marks);
    }

    [Fact]
    public void AnUnchangedFrame_RunsNoDetector_AndAllocatesNothing()
    {
        Pick(0, COpenArpeggio);
        Pick(6, AmAt5);
        Frame();
        var computed = _latch.ComputeCount;

        AllocationProbe.AssertAllocationFree("warm song-map suggestion frame", () => Frame());

        Assert.Equal(computed, _latch.ComputeCount);
        Assert.Equal(2, _marks.Count);
    }

    private void Pick(double startBeat, (int String, int Fret)[] notes)
    {
        for (var i = 0; i < notes.Length; i++)
        {
            Level.Notes.Add(new GuitarNote
            {
                Position = Pos(startBeat + i * 0.5), StringIndex = notes[i].String, Fret = notes[i].Fret
            });
        }
    }
}
