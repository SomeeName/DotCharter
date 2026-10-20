using DotCharter.Rendering.Timeline2D;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The string-edit mask changes what every per-string keyboard verb does, so
///     it has to be ON SCREEN. The editing-rework audit is explicit that the one
///     thing not to copy from rscharter is its <c>typingNumber</c> cursor —
///     invisible modal state that silently changes what a key means (03 §6.2,
///     §7.11). <c>LaneLayer</c> marks the masked lanes on the headers.
///     <para>
///         No GPU: <see cref="TimelineBatcher" /> is a plain float-buffer builder,
///         so "did the layer draw the pip" is a vertex-count question.
///     </para>
/// </summary>
public sealed class LaneLayerStringMaskTests
{
    [Fact]
    public void AnEmptyMaskDrawsNothingExtra()
    {
        // Empty = every string = the pre-0.35 frame, byte for byte.
        var baseline = RenderWith(StringEditMask.Empty);
        var alsoEmpty = RenderWith(StringEditMask.FromBits(0));

        Assert.Equal(baseline, alsoEmpty);
    }

    [Fact]
    public void EachMaskedStringDrawsOneMarker()
    {
        var empty = RenderWith(StringEditMask.Empty);
        var one = RenderWith(StringEditMask.FromStrings(1));
        var two = RenderWith(StringEditMask.FromStrings(1, 3));

        var perPip = one - empty;
        Assert.True(perPip > 0, "a masked lane must be marked on its header");
        Assert.Equal(2 * perPip, two - empty);
    }

    [Fact]
    public void MaskingOutALaneKeepsItsGuideLine()
    {
        // The unmasked lanes are DIMMED (a colour change), never removed — the
        // lane is still a lane, it just is not a keyboard target right now. So
        // the only geometry delta is the pips.
        var empty = RenderWith(StringEditMask.Empty);
        var allSixMasked = RenderWith(StringEditMask.FromStrings(0, 1, 2, 3, 4, 5));
        var one = RenderWith(StringEditMask.FromStrings(0));

        Assert.Equal(6 * (one - empty), allSixMasked - empty);
    }

    [Fact]
    public void AMaskBitBeyondTheArrangementsStringsDrawsNothing()
    {
        var empty = RenderWith(StringEditMask.Empty);
        var offNeck = RenderWith(StringEditMask.FromStrings(8));

        Assert.Equal(empty, offNeck);
    }

    private static int RenderWith(StringEditMask mask)
    {
        var batcher = new TimelineBatcher();
        new LaneLayer().Render(batcher, Context(mask));
        return batcher.SolidVertexCount + batcher.GeneralVertexCount;
    }

    private static ChartRenderContext Context(StringEditMask mask) => new()
    {
        Width = 900,
        Height = 400,
        CurrentZoom = 1.0,
        ViewStartMs = 0,
        ViewEndMs = 900,
        TopMargin = 20f,
        BottomMargin = 20f,
        LaneAreaHeight = 360f,
        LaneHeight = 60f,
        StringCount = 6,
        // Labels off: they route through the font atlas, which is a GPU-side
        // resource. The mask marker is plain geometry.
        DrawLabels = false,
        HoveredStringIndex = -1,
        StringEditMask = mask,
        Editor = BuildEditor()
    };

    private static ChartEditorViewModel BuildEditor()
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var selection = new SelectionService();
        var history = new HistoryService(logger);
        var config = new FakeConfigurationService();

        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arr);
        state.ActiveProject = project;
        state.ActiveArrangement = arr;
        state.ActiveLevel = arr.Levels[0];

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var tracks = new TrackContextRegistry(state, selection);
        return new ChartEditorViewModel(state, transport, history, selection, new TempoService(logger), tracks, FakeLocalization.KeysOnly());
    }
}
