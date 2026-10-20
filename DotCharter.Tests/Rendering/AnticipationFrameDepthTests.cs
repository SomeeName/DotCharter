using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     As a note arrives it paints a landing-target frame on the board. The
///     frame sat at exactly z = 0, the string plane, where the opaque pass has
///     already depth-written every string and its halo — so the two z-fought:
///     the frame's edges broke into teeth along each string and whole sides
///     dropped out (seen in a 1920×1080 headless review frame). The board face
///     is a depth STACK (capo −0.05 → fret bars −0.01 → inlay dots −0.005 →
///     strings 0); the frame has to take its own slot in it.
/// </summary>
public sealed class AnticipationFrameDepthTests
{
    private const float StringPlaneZ = 0f;
    private const float InlayZ = -0.005f;

    [Theory]
    [InlineData(5)] // fretted: the textured frame sprite
    [InlineData(0)] // open string: the flat bar across the anchor
    public void The_landing_frame_sits_between_the_strings_and_the_inlays(int fret)
    {
        var batcher = new QuadBatcher(null!);
        NoteHeadRenderer.Render(Context(batcher, fret));

        var boardQuads = 0;
        var data = batcher.StagedVertices.ToArray();
        for (var q = 0; q < data.Length; q += QuadBatcher.FloatsPerVertex * 4)
        {
            // The head itself is ~1 world unit up the highway 80 ms out; only
            // the landing frame is on the board face.
            var z = data[q + 2];
            if (Math.Abs(z) > 0.05f) continue;
            boardQuads++;
            for (var v = 0; v < 4; v++)
            {
                var vz = data[q + v * QuadBatcher.FloatsPerVertex + 2];
                Assert.True(vz < StringPlaneZ,
                    $"landing frame vertex at z={vz}: on the string plane it z-fights the strings");
                Assert.True(vz > InlayZ,
                    $"landing frame vertex at z={vz}: in front of the inlay dots it would cover them");
            }
        }

        Assert.True(boardQuads > 0, "no landing frame was staged — the fixture is not exercising it");
    }

    /// <summary>
    ///     An open-string note is a flat bar in its string colour across the
    ///     anchor. It was staged with vertex TYPE 5 (a constant misnamed
    ///     <c>NoteHeadZOffset</c>), the chord-pane mode whose near-fade takes alpha
    ///     to ~10 % at the strike line — so open notes vanished as they had to be
    ///     played, and their landing cue never showed. No mode with the near-fade
    ///     (2, 5, 9, 10, 11 in <c>highway.frag</c>) may draw one.
    /// </summary>
    [Fact]
    public void An_open_note_bar_is_never_drawn_in_a_mode_that_fades_it_out_at_the_strike()
    {
        var batcher = new QuadBatcher(null!);
        NoteHeadRenderer.Render(Context(batcher, fret: 0));

        var data = batcher.StagedVertices.ToArray();
        var quads = data.Length / (QuadBatcher.FloatsPerVertex * 4);
        Assert.True(quads >= 2, "expected the open bar and its landing cue");
        for (var q = 0; q < data.Length; q += QuadBatcher.FloatsPerVertex * 4)
        {
            var type = MathF.Round(data[q + 12]);
            if (type is 8f) continue; // halo glow (fretted only); not a bar
            Assert.DoesNotContain(type, new[] { 2f, 5f, 9f, 10f, 11f });
        }
    }

    private static RenderContext Context(QuadBatcher batcher, int fret) => new()
    {
        Snapshot = new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 3, Width = 4 }],
            AnchorCount = 1,
            Notes = [new RenderNote { Time = 1_080, StringIndex = 2, Fret = (byte)fret }],
            NoteCount = 1
        },
        TimeMs = 1_000,
        BoardWidth = 10f,
        QuadBatcher = batcher,
        TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.DefaultScrollSpeed),
        FadeEndZ = HighwaySpeedPolicy.DefaultDrawDistanceZ,
        FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.DefaultDrawDistanceZ),
        HorizonMs = HighwaySpeedPolicy.HorizonMs(
            HighwaySpeedPolicy.DefaultScrollSpeed, HighwaySpeedPolicy.DefaultDrawDistanceZ)
    };
}
