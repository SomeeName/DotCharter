using DotCharter.Core.Graphics;
using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The transparent pass writes no depth, so inside it DRAW ORDER is
///     occlusion. At the strike the note and chord heads land exactly on the
///     finger spots, and the fingering batch used to be recorded BEFORE them —
///     so the numbers vanished under the gems at the moment (and, paused, the
///     cursor position) a user looks for them. The headless probe measured it:
///     9 changed pixels with the overlay on versus 2,400 once it drew last.
///     <para>
///         Needs <see cref="FingeringRenderer.Enabled" />, a process-wide static,
///         so the class runs in its own serialized collection.
///     </para>
/// </summary>
[Collection("FingeringOverlayStatic")]
public sealed class NotesRenderComponentOrderTests
{
    [Fact]
    public void The_fingering_is_drawn_after_every_note_and_chord_head_and_hands_the_pass_on_untextured()
    {
        var render = new FakeVulkanRenderService();
        var resources = new RenderResources
        {
            NotesTexture = render.LoadTexture(1, 1, [255, 255, 255, 255]),
            FingeringTexture = render.LoadTexture(1, 1, [255, 255, 255, 255])
        };
        var component = new NotesRenderComponent();
        component.Initialize(render, resources);
        var batcher = new QuadBatcher(render);

        var previous = FingeringRenderer.Enabled;
        try
        {
            FingeringRenderer.Enabled = true;
            component.Render(StruckChord(batcher));
            batcher.Flush();
        }
        finally
        {
            FingeringRenderer.Enabled = previous;
        }

        var batches = render.QuadBatches;
        var fingering = batches.FindIndex(b => ReferenceEquals(b.Texture, resources.FingeringTexture));
        var lastNotes = batches.FindLastIndex(b => ReferenceEquals(b.Texture, resources.NotesTexture));
        var lastUntextured = batches.FindLastIndex(b => b.Texture is null);

        Assert.True(fingering >= 0, "the fixture drew no fingering — the order claim would be vacuous");
        Assert.True(lastNotes >= 0, "the fixture drew no textured heads — the order claim would be vacuous");
        Assert.True(fingering > lastNotes,
            $"fingering batch #{fingering} precedes the note-head batch #{lastNotes}: the gems paint over it");
        Assert.True(fingering > lastUntextured,
            $"fingering batch #{fingering} precedes untextured chord geometry #{lastUntextured}");
        Assert.Equal(fingering, batches.Count - 1);
    }

    private static RenderContext StruckChord(QuadBatcher batcher)
    {
        var template = new RenderChordTemplate { Id = 0, Name = "C" };
        (int String, int Fret, int Finger)[] grip = [(1, 3, 3), (2, 2, 2), (4, 1, 1)];
        for (var s = 0; s < 8; s++)
        {
            template.SetFret(s, -1);
            template.SetFinger(s, -1);
        }

        foreach (var (str, fret, finger) in grip)
        {
            template.SetFret(str, fret);
            template.SetFinger(str, finger);
        }

        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 1, Width = 4 }],
            AnchorCount = 1,
            Templates = [template],
            TemplateCount = 1,
            // A few ms after the playhead, so the heads are drawn (a head
            // strictly behind the playhead is culled).
            Chords = [new RenderChord { Time = 1_010, TemplateId = 0, NoteOffset = 0, NoteCount = 3 }],
            ChordCount = 1,
            ChordNotes =
            [
                new RenderChordNote { StringIndex = 1, Fret = 3 },
                new RenderChordNote { StringIndex = 2, Fret = 2 },
                new RenderChordNote { StringIndex = 4, Fret = 1 }
            ],
            ChordNoteCount = 3,
            Notes = [new RenderNote { Time = 1_020, StringIndex = 0, Fret = 5 }],
            NoteCount = 1
        };

        return new RenderContext
        {
            Snapshot = snapshot,
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
}

[CollectionDefinition("FingeringOverlayStatic", DisableParallelization = true)]
public sealed class FingeringOverlayStaticCollection;
