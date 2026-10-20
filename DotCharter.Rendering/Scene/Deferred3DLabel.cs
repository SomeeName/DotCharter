using System.Numerics;
using Avalonia.Media;

namespace DotCharter.Rendering.Scene;

/// <summary>
///     One piece of in-world text whose POSITION is known during a geometry pass
///     but which must not be staged until the Ui (3D text) pass.
///     <para>
///         <b>3D text may only be staged from the <c>UiElements</c> pass</b> — the
///         opaque and transparent passes both run before
///         <c>Fretboard3DEngine.RenderFrame</c> calls
///         <c>_textBatcher.SetState(FontAtlas, sceneMvp)</c>, so glyphs staged
///         from them hit a null atlas on frame 1 and the PREVIOUS frame's
///         orthographic HUD matrix on every frame after, with no exception and no
///         log line. That is the rule that cost three releases of invisible chord
///         names (see <c>Components/ChordNameComponent</c>).
///     </para>
///     <para>
///         Chord names solve it by recomputing their geometry from scratch inside
///         a Ui-pass component. A bend peak cannot: its world position falls out
///         of the sustain tail's per-segment walk (string lane, capo/open-string
///         branch, the anchor-relative note X), and a Ui-pass component would have
///         to duplicate that whole walk for both the single-note and the chord-note
///         entry points and then keep the copy in step. So the geometry pass
///         DEFERS the label — position and all — and
///         <c>Components/BendLabelComponent</c> drains the queue once the text
///         batcher is pointed at the scene matrix.
///     </para>
///     <para>
///         A struct in a `RenderContext`-owned, GC-reused array: the per-frame path
///         allocates nothing (the label strings are precomputed in
///         <c>BendRenderer.BendLabels</c>).
///     </para>
/// </summary>
public readonly record struct Deferred3DLabel(string Text, Vector3 Position, Color Color, float Scale);
