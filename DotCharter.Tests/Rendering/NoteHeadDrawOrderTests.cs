using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Note heads, chord boxes and arpeggio brackets are drawn in the transparent
///     pass, which writes no depth: the order they are staged in IS their
///     occlusion. A farther object staged after a nearer one paints over it, so
///     the heads must be staged FARTHEST FIRST — as ONE walk across single notes,
///     chords and hand shapes. Single heads used to be staged nearest first, and
///     every single head before every hand shape and chord, so a far gem showed
///     through the near gem in front of it and a far chord box covered a near
///     single note. <see cref="Headless.HeadlessNoteOcclusionRenderTests" />
///     checks the pixels.
/// </summary>
[Collection("FingeringOverlayStatic")]
public sealed class NoteHeadDrawOrderTests
{
    private const int QuadFloats = QuadBatcher.FloatsPerVertex * 4;
    private const double NowMs = 1_000;

    /// <summary>Past the anticipation window, which stages a frame at a fixed
    /// near depth for a head that is about to land.</summary>
    private const double FirstMs = 1_600;

    private const double StepMs = 200;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Heads_chords_and_brackets_are_staged_farthest_first(bool fingering)
    {
        // Interleaved in time: note, chord, note, chord, note, arpeggio + its chord.
        var objectTimes = Enumerable.Range(0, 6).Select(i => FirstMs + i * StepMs).ToArray();
        var render = new FakeVulkanRenderService();
        var resources = new RenderResources
        {
            NotesTexture = render.LoadTexture(1, 1, [255, 255, 255, 255]),
            FingeringTexture = render.LoadTexture(1, 1, [255, 255, 255, 255])
        };
        var component = new NotesRenderComponent();
        component.Initialize(render, resources);
        var batcher = new QuadBatcher(render);
        var context = Context(batcher, objectTimes);

        var previous = FingeringRenderer.Enabled;
        try
        {
            FingeringRenderer.Enabled = fingering;
            component.Render(context);
            batcher.Flush();
        }
        finally
        {
            FingeringRenderer.Enabled = previous;
        }

        var objectZ = objectTimes.Select(t => context.GetZDepthForTime(t, NowMs)).ToArray();
        var halfGap = (objectZ[1] - objectZ[0]) / 2f;
        var stagedPerObject = new int[objectZ.Length];
        var nearestSoFar = int.MaxValue;
        var quad = 0;
        foreach (var (batch, texture) in render.QuadBatchVertices.Zip(render.QuadBatches.Select(b => b.Texture)))
        {
            // The fingering numbers draw last ON PURPOSE (NotesRenderComponentOrderTests).
            if (ReferenceEquals(texture, resources.FingeringTexture)) continue;
            for (var q = 0; q < batch.Length; q += QuadFloats, quad++)
            {
                var z = MeanZ(batch.AsSpan(q, QuadFloats));
                var owner = Array.FindIndex(objectZ, oz => Math.Abs(oz - z) < halfGap);
                Assert.True(owner >= 0, $"quad #{quad} at z {z} belongs to no object of the scene");
                stagedPerObject[owner]++;
                if (owner > nearestSoFar)
                {
                    Assert.Fail($"quad #{quad} of the object at {objectTimes[owner]} ms is staged after the NEARER " +
                                $"object at {objectTimes[nearestSoFar]} ms and paints over it");
                }

                nearestSoFar = owner;
            }
        }

        for (var i = 0; i < objectZ.Length; i++)
            Assert.True(stagedPerObject[i] > 0, $"the object at {objectTimes[i]} ms drew nothing: the scene tests less");
    }

    private static float MeanZ(ReadOnlySpan<float> quad)
    {
        var sum = 0f;
        for (var v = 0; v < 4; v++) sum += quad[v * QuadBatcher.FloatsPerVertex + 2];
        return sum / 4f;
    }

    private static RenderContext Context(QuadBatcher batcher, double[] t)
    {
        var arpeggio = new RenderChordTemplate { Id = 0, Name = "A", Arpeggio = true };
        for (var s = 0; s < 8; s++)
        {
            arpeggio.SetFret(s, s is 1 or 2 ? 7 : -1);
            arpeggio.SetFinger(s, s is 1 or 2 ? 1 : -1);
        }

        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 5, Width = 4 }],
            AnchorCount = 1,
            Templates = [arpeggio],
            TemplateCount = 1,
            Notes =
            [
                new RenderNote { Time = t[0], StringIndex = 2, Fret = 7 },
                new RenderNote { Time = t[2], StringIndex = 3, Fret = 0 },
                new RenderNote { Time = t[4], StringIndex = 1, Fret = 8, Mute = (byte)MuteType.Palm }
            ],
            NoteCount = 3,
            Chords =
            [
                new RenderChord { Time = t[1], TemplateId = -1, NoteOffset = 0, NoteCount = 2 },
                new RenderChord { Time = t[3], TemplateId = -1, NoteOffset = 2, NoteCount = 2 },
                new RenderChord { Time = t[5], TemplateId = 0, NoteOffset = 4, NoteCount = 2 }
            ],
            ChordCount = 3,
            ChordNotes =
            [
                new RenderChordNote { StringIndex = 1, Fret = 7 },
                new RenderChordNote { StringIndex = 2, Fret = 7 },
                new RenderChordNote { StringIndex = 2, Fret = 6 },
                new RenderChordNote { StringIndex = 3, Fret = 6 },
                new RenderChordNote { StringIndex = 1, Fret = 7 },
                new RenderChordNote { StringIndex = 2, Fret = 7 }
            ],
            ChordNoteCount = 6,
            HandShapes = [new RenderHandShape { Time = t[5], EndTime = t[5] + StepMs / 2, TemplateId = 0 }],
            HandShapeCount = 1
        };

        return new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = NowMs,
            BoardWidth = 10f,
            QuadBatcher = batcher,
            TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
            FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
            FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
            HorizonMs = HighwaySpeedPolicy.HorizonMs(
                HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
        };
    }
}
