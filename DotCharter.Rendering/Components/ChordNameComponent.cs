using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

/// <summary>
///     Draws the Rocksmith-style chord names, in the <b>Ui (3D text) pass</b>.
///     <para>
///         <b>The pass is the whole point of this type, and getting it wrong is
///         why chord names go invisible.</b> They must not
///         be drawn by <see cref="NotesRenderComponent" />, which lives in
///         <c>TransparentElements</c> — and the transparent pass runs BEFORE
///         <c>Fretboard3DEngine.RenderFrame</c> calls
///         <c>_textBatcher.SetState(FontAtlas, sceneMvp)</c>. Two failures fall
///         out of that ordering, and between them they lose every frame:
///     </para>
///     <list type="number">
///         <item>
///             On the first frame the batcher's atlas is still null, and
///             <c>TextBatcher.DrawText3D</c> answers a null atlas by returning
///             immediately — silently, with no log line.
///         </item>
///         <item>
///             On every frame after that the atlas is set, but the matrix the
///             batcher carries is the one left by the previous frame's LAST
///             <c>SetState</c> — the orthographic HUD matrix. So the glyphs would be
///             staged, then flushed under an ortho projection that expects
///             screen-space pixels, which puts world coordinates nowhere near the
///             viewport.
///         </item>
///     </list>
///     <para>
///         Moving the label's world X, Y or scale cannot help —
///         it only moves something that is never going to be drawn. If you
///         are debugging "3D text does not appear", check WHICH SCENE LIST the
///         component is in before touching any geometry: text belongs in
///         <c>UiElements</c> (or <c>HudElements</c> for the ortho HUD), never in
///         the opaque or transparent lists.
///     </para>
///     <para>
///         Separate from <see cref="TextOverlayComponent" /> rather than folded
///         into it because the two answer to different data (chord/hand-shape
///         templates vs. the fret ruler) and one of them being empty must never
///         be able to suppress the other.
///     </para>
/// </summary>
public sealed class ChordNameComponent : IRenderable
{
    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    public void Render(RenderContext context) => ChordNameRenderer.Render(context);
}
