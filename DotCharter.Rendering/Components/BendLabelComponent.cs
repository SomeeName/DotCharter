using System.Numerics;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
///     Draws the bend-peak labels ("v 1/2", "v Full", …) in the <b>Ui (3D text)
///     pass</b>, from the queue the transparent pass filled.
///     <para>
///         <b>The pass is the whole point of this type.</b> The labels are produced
///         by <c>NoteRendering/BendRenderer.RenderPeaks</c>, which is reached from
///         <c>NotesRenderComponent</c> and <c>NoteRendering/ChordRenderer</c> —
///         both in <c>TransparentElements</c>. They must not call
///         <c>TextBatcher.DrawText3D</c> directly from there, which is the exact
///         fault that makes chord names invisible: the
///         transparent pass runs BEFORE
///         <c>Fretboard3DEngine.RenderFrame</c>'s
///         <c>_textBatcher.SetState(FontAtlas, sceneMvp)</c>, so the glyphs would be
///         staged against a null atlas on the first frame and thereafter flushed
///         under the PREVIOUS frame's orthographic HUD matrix — which maps
///         world-unit coordinates into a speck in one screen corner. Silently: no
///         exception, no log line.
///     </para>
///     <para>
///         Unlike <see cref="ChordNameComponent" />, which recomputes its own
///         geometry, this one drains a queue. A bend peak's world position falls
///         out of the sustain tail's per-segment walk (string lane, the
///         capo/open-string branch, the anchor-relative note X) and has two entry
///         points — single notes and chord notes — so recomputing it here would
///         mean duplicating that walk twice and keeping both copies in step. The
///         producer knows the position; only the STAGING has to move.
///     </para>
///     <para>
///         The queue lives on the frame-reused <see cref="RenderContext" />, so
///         this is allocation-free per frame.
///     </para>
/// </summary>
public sealed class BendLabelComponent : IRenderable
{
    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context)
    {
        var labels = context.Deferred3DLabels;
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            context.TextBatcher.DrawText3D(
                label.Text, Matrix4x4.CreateTranslation(label.Position), label.Color, label.Scale);
        }
    }
}
