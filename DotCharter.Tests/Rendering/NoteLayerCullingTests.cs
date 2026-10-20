using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The note path's screen-X culling. The frame snapshot deliberately reaches
///     ±5000 ms past the viewport (sustains that start off-screen left, sticky
///     prefixes), so the layer ITERATES sounds it must not draw — at 1 px/ms on a
///     1920 px timeline that is 6.2× the geometry the viewport can show. Every
///     other 2D layer culls; these tests pin that the note layers do too.
///     <para>
///         The assertion is behavioural — "a sound entirely outside the viewport
///         contributes no vertices" — not the shape of the test that implements it.
///         No GPU: <see cref="TimelineBatcher" /> is a plain float-buffer builder.
///     </para>
/// </summary>
public sealed class NoteLayerCullingTests
{
    private const double ViewWidth = 900.0;
    private const double Zoom = 1.0; // 1 px per ms, so ms and px are interchangeable

    [Fact]
    public void A_note_entirely_off_screen_contributes_no_vertices()
    {
        var batcher = new TimelineBatcher();

        // 4000 ms is well inside the snapshot's +5000 ms buffer, so the layer
        // really does walk this note — it is 3100 px past the right edge.
        new NoteLayer().Render(batcher, Context(Notes((4000, 0))));

        Assert.Equal(0, batcher.SolidVertexCount);
        Assert.Equal(0, batcher.GeneralVertexCount);
    }

    [Fact]
    public void A_note_inside_the_view_still_draws()
    {
        var batcher = new TimelineBatcher();

        new NoteLayer().Render(batcher, Context(Notes((400, 0))));

        Assert.True(batcher.SolidVertexCount + batcher.GeneralVertexCount > 0,
            "a visible note must still emit geometry");
    }

    [Fact]
    public void A_sustain_starting_off_screen_left_still_draws_its_visible_tail()
    {
        var batcher = new TimelineBatcher();

        // This is exactly why the ±5000 ms snapshot buffer exists: the head sits
        // at -2000 px but the tail crosses the whole viewport. Culling on the
        // head alone would blank it.
        new NoteLayer().Render(batcher, Context(Notes((-2000, 3000))));

        Assert.True(batcher.SolidVertexCount + batcher.GeneralVertexCount > 0,
            "a sustain whose tail reaches the viewport must not be culled");
    }

    [Fact]
    public void Off_screen_neighbours_do_not_add_to_the_frame_cost()
    {
        var visibleOnly = new TimelineBatcher();
        new NoteLayer().Render(visibleOnly, Context(Notes((100, 0), (500, 0))));

        var withNeighbours = new TimelineBatcher();
        new NoteLayer().Render(withNeighbours, Context(Notes(
            (-4800, 0), (-3000, 0), (-1500, 0),   // the snapshot's left buffer
            (100, 0), (500, 0),                    // the only two on screen
            (2500, 0), (3800, 0), (4900, 0))));    // the right buffer

        Assert.Equal(visibleOnly.SolidVertexCount, withNeighbours.SolidVertexCount);
        Assert.Equal(visibleOnly.GeneralVertexCount, withNeighbours.GeneralVertexCount);
    }

    [Fact]
    public void An_off_screen_chord_contributes_no_vertices()
    {
        var batcher = new TimelineBatcher();

        new NoteLayer().Render(batcher, Context(Chord(4000)));

        Assert.Equal(0, batcher.SolidVertexCount);
        Assert.Equal(0, batcher.GeneralVertexCount);
    }

    [Fact]
    public void A_visible_chord_still_draws_its_panel_and_heads()
    {
        var batcher = new TimelineBatcher();

        new NoteLayer().Render(batcher, Context(Chord(400)));

        Assert.True(batcher.SolidVertexCount + batcher.GeneralVertexCount > 0,
            "a visible chord must still emit geometry");
    }

    // ---- fixtures -----------------------------------------------------------

    private static FrameSnapshot Notes(params (double TimeMs, double SustainMs)[] notes)
    {
        var snapshot = new FrameSnapshot { StringCount = 6 };
        var rendered = new RenderNote[notes.Length];
        for (var i = 0; i < notes.Length; i++)
        {
            rendered[i] = new RenderNote
            {
                Time = notes[i].TimeMs,
                Sustain = notes[i].SustainMs,
                StringIndex = 2,
                Fret = 5
            };
        }

        snapshot.Notes = rendered;
        snapshot.NoteCount = rendered.Length;
        return snapshot;
    }

    private static FrameSnapshot Chord(double timeMs)
    {
        var snapshot = new FrameSnapshot
        {
            StringCount = 6, ChordNotes =
            [
                new RenderChordNote { StringIndex = 1, Fret = 3 },
                new RenderChordNote { StringIndex = 2, Fret = 5 },
                new RenderChordNote { StringIndex = 3, Fret = 5 }
            ],
            Chords = [new RenderChord { Time = timeMs, NoteOffset = 0, NoteCount = 3, TemplateId = 1 }],
            ChordCount = 1
        };
        return snapshot;
    }

    private static ChartRenderContext Context(FrameSnapshot snapshot) => new()
    {
        Width = ViewWidth,
        Height = 400,
        CurrentZoom = Zoom,
        ViewStartMs = 0,
        ViewEndMs = ViewWidth / Zoom,
        TopMargin = 20f,
        BottomMargin = 20f,
        LaneAreaHeight = 360f,
        LaneHeight = 60f,
        StringCount = 6,
        DrawLabels = true,
        Snapshot = snapshot
    };
}
