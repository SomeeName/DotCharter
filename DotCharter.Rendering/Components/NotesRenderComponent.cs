using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

public sealed class NotesRenderComponent : IRenderable
{
    private RenderResources _resources = null!;

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources) => _resources = resources;

    public void Render(RenderContext context)
    {
        context.QuadBatcher.SetTexture(null);
        SustainRenderer.Render(context);

        RenderHeadsFarthestFirst(context);
        // NOTE: chord NAMES are not drawn here. They are 3D TEXT, and text may
        // only be staged from the Ui pass — see ChordNameComponent.

        // LAST: this pass writes no depth, so draw order is occlusion, and at
        // the strike the note and chord heads land exactly on the finger spots.
        // Drawn before them (as it was), the numbers vanished under the gems at
        // the one moment — and, in a paused editor, the one cursor position — a
        // user looks for them. See NotesRenderComponentOrderTests.
        context.QuadBatcher.SetTexture(_resources.FingeringTexture);
        FingeringRenderer.Render(context, _resources);

        // Hand the pass on untextured, as it always was: the hit-feedback
        // components after this one never bind a texture of their own.
        context.QuadBatcher.SetTexture(null);
    }

    /// <summary>
    ///     Single-note heads, arpeggio brackets and chord boxes as ONE walk from the
    ///     farthest object to the nearest. This pass writes no depth, so draw order
    ///     is occlusion: staged nearest first, or every single head before every
    ///     chord, a far gem showed through the near gem in front of it and a far
    ///     chord box covered a near single note. Each list is in time order (the
    ///     snapshot builds them so), so a merge from their ends is enough. On a
    ///     tie the old group order holds: the head, then the bracket, then the box.
    ///     See NoteHeadDrawOrderTests and HeadlessNoteOcclusionRenderTests.
    /// </summary>
    private void RenderHeadsFarthestFirst(RenderContext context)
    {
        var snapshot = context.Snapshot;
        var batcher = context.QuadBatcher;
        var note = snapshot.NoteCount - 1;
        var shape = snapshot.HandShapeCount - 1;
        var chord = snapshot.ChordCount - 1;
        while (note >= 0 || shape >= 0 || chord >= 0)
        {
            var noteTime = note >= 0 ? snapshot.Notes[note].Time : double.NegativeInfinity;
            var shapeTime = shape >= 0 ? snapshot.HandShapes[shape].Time : double.NegativeInfinity;
            var chordTime = chord >= 0 ? snapshot.Chords[chord].Time : double.NegativeInfinity;
            if (note >= 0 && noteTime >= shapeTime && noteTime >= chordTime)
            {
                batcher.SetTexture(_resources.NotesTexture);
                NoteHeadRenderer.RenderNote(context, note--);
            }
            else if (shape >= 0 && shapeTime >= chordTime)
            {
                batcher.SetTexture(null);
                HandShapeRenderer.RenderHandShape(context, shape--);
            }
            else
            {
                batcher.SetTexture(null);
                ChordRenderer.RenderChord(context, _resources, chord--);
            }
        }

        batcher.SetTexture(null);
    }
}
