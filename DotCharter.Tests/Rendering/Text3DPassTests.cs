using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media;
using DotCharter.Rendering.Scene;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <b>3D text may only be staged from the <c>UiElements</c> pass.</b> The
///     opaque and transparent passes both run before
///     <c>Fretboard3DEngine.RenderFrame</c> calls
///     <c>_textBatcher.SetState(FontAtlas, sceneMvp)</c>, so a <c>DrawText3D</c>
///     from either one hits a null atlas on the first frame and the PREVIOUS
///     frame's orthographic HUD matrix on every frame after — world coordinates
///     under a screen-pixel projection, i.e. a speck in a corner. It throws
///     nothing and logs nothing.
///     <para>
///         That rule cost three releases of invisible chord names (v0.41 →
///         v0.42.1) and the fix at the time was a code comment. It then recurred:
///         <c>NoteRendering/BendRenderer.RenderPeaks</c> — reached from
///         <c>NotesRenderComponent</c> and <c>NoteRendering/ChordRenderer</c>, both
///         in <c>TransparentElements</c> — drew every bend-peak label
///         ("v 1/2", "v Full", …) that way. So this is a LINT now, not a comment.
///     </para>
/// </summary>
public sealed class Text3DPassTests
{
    /// <summary>
    ///     Walks the static call graph out of every component the engine puts in
    ///     <c>OpaqueElements</c>/<c>TransparentElements</c> and asserts none of it
    ///     reaches <c>DrawText3D</c>. Types are matched by file name, which is this
    ///     assembly's convention for everything under <c>Components/</c>.
    /// </summary>
    [Fact]
    public void NoGeometryPassComponentReachesDrawText3D()
    {
        var (geometryRoots, textRoots) = SceneListRoots();
        var files = ComponentFilesByTypeName();

        // Guard against the lint quietly measuring nothing: if the engine's
        // registration syntax ever changes, these go empty and everything "passes".
        Assert.NotEmpty(geometryRoots);
        Assert.NotEmpty(textRoots);
        Assert.Contains("NotesRenderComponent", geometryRoots);
        Assert.Contains("ChordNameComponent", textRoots);

        var reached = TransitiveClosure(geometryRoots, files);

        // …and that the closure actually followed edges rather than stopping at
        // the roots. SustainRenderer is three hops in
        // (NotesRenderComponent -> SustainRenderer -> BendRenderer).
        Assert.Contains("SustainRenderer", reached);
        Assert.Contains("BendRenderer", reached);

        var offenders = reached
            .Where(type => files.TryGetValue(type, out var path) &&
                           StripCommentsAndStrings(File.ReadAllText(path))
                               .Contains("DrawText3D", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These types are reachable from the opaque/transparent passes and stage 3D text, which draws " +
            "nothing (null atlas on frame 1, then the previous frame's ortho HUD matrix). Defer the label " +
            "via RenderContext.Defer3DLabel and drain it from a UiElements component — see " +
            "BendLabelComponent. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The other half: the drain must actually be registered. A deferred label
    ///     with nobody draining it is the same invisible text by a different route.
    /// </summary>
    [Fact]
    public void TheDeferredLabelDrainIsRegisteredInTheUiPass()
    {
        var (_, textRoots) = SceneListRoots();

        Assert.Contains("BendLabelComponent", textRoots);
    }

    /// <summary>
    ///     The engine must reset the queue as it sets the frame up. Without it a
    ///     frame that bails before <c>RenderUi</c> leaves its labels behind and the
    ///     next frame draws them at stale positions — and the queue grows forever.
    /// </summary>
    [Fact]
    public void TheEngineClearsTheDeferredQueueEveryFrame()
    {
        var source = StripCommentsAndStrings(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Engines", "Fretboard3DEngine.cs")));

        Assert.Contains("ClearDeferred3DLabels()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AClearedQueueIsEmpty()
    {
        var context = new RenderContext();
        context.Defer3DLabel(new Deferred3DLabel("v Full", Vector3.One, Colors.White, 0.004f));

        context.ClearDeferred3DLabels();

        Assert.Equal(0, context.Deferred3DLabelCount);
        Assert.True(context.Deferred3DLabels.IsEmpty);
    }

    /// <summary>
    ///     Deferral must preserve the label verbatim — the producer computes the
    ///     world position, the drain only stages it.
    /// </summary>
    [Fact]
    public void ADeferredLabelSurvivesTheQueueUnchanged()
    {
        var context = new RenderContext();
        var label = new Deferred3DLabel("v 1 1/2", new Vector3(1.5f, 2.5f, 3.5f), Colors.White, 0.004f);

        context.Defer3DLabel(label);

        Assert.Equal(1, context.Deferred3DLabelCount);
        Assert.Equal(label, context.Deferred3DLabels[0]);
    }

    /// <summary>
    ///     The queue backs onto a GC-reused array that must GROW rather than throw
    ///     — a dense bend-heavy passage can exceed the initial 128.
    /// </summary>
    [Fact]
    public void TheQueueGrowsPastItsInitialCapacity()
    {
        var context = new RenderContext();
        for (var i = 0; i < 500; i++)
            context.Defer3DLabel(new Deferred3DLabel("v 2", new Vector3(i, 0, 0), Colors.White, 0.004f));

        Assert.Equal(500, context.Deferred3DLabelCount);
        Assert.Equal(499f, context.Deferred3DLabels[499].Position.X);
    }

    /// <summary>
    ///     …and reusing the context across frames must not re-allocate it: the
    ///     engine holds ONE `_frameContext` for the process's life, and the whole
    ///     point of the array is that the per-frame path allocates nothing.
    /// </summary>
    [Fact]
    public void RefillingAfterAClearAllocatesNothing()
    {
        var context = new RenderContext();
        for (var frame = 0; frame < 3; frame++)
        {
            context.ClearDeferred3DLabels();
            for (var i = 0; i < 200; i++)
                context.Defer3DLabel(new Deferred3DLabel("v 2", Vector3.Zero, Colors.White, 0.004f));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        context.ClearDeferred3DLabels();
        for (var i = 0; i < 200; i++)
            context.Defer3DLabel(new Deferred3DLabel("v 2", Vector3.Zero, Colors.White, 0.004f));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"A warm frame's deferrals allocated {allocated} bytes; expected 0.");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    ///     The component type names the engine registers, split into the geometry
    ///     passes (which may not stage text) and the text passes (which may).
    /// </summary>
    private static (HashSet<string> Geometry, HashSet<string> Text) SceneListRoots()
    {
        var source = StripCommentsAndStrings(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Engines", "Fretboard3DEngine.cs")));

        var geometry = new HashSet<string>(StringComparer.Ordinal);
        var text = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(source,
                     @"_scene\.(Opaque|Transparent|Ui|Hud)Elements\.Add\(\s*new\s+(\w+)\s*\("))
        {
            var target = m.Groups[1].Value is "Opaque" or "Transparent" ? geometry : text;
            target.Add(m.Groups[2].Value);
        }

        return (geometry, text);
    }

    /// <summary>Every type under <c>Components/</c>, keyed by file name.</summary>
    private static Dictionary<string, string> ComponentFilesByTypeName()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Components");
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p)!, p => p, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Everything reachable from <paramref name="roots" /> by a <c>Type.</c>
    ///     mention in the (comment- and string-stripped) source. Crude, but the
    ///     component call graph is shallow and entirely static, and erring towards
    ///     OVER-reaching is the safe direction for a lint like this.
    /// </summary>
    private static HashSet<string> TransitiveClosure(HashSet<string> roots, Dictionary<string, string> files)
    {
        var reached = new HashSet<string>(roots, StringComparer.Ordinal);
        var queue = new Queue<string>(roots);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!files.TryGetValue(type, out var path)) continue;
            var source = StripCommentsAndStrings(File.ReadAllText(path));

            foreach (var candidate in files.Keys)
            {
                if (reached.Contains(candidate)) continue;
                if (!Regex.IsMatch(source, $@"\b{Regex.Escape(candidate)}\b\s*\.")) continue;
                reached.Add(candidate);
                queue.Enqueue(candidate);
            }
        }

        return reached;
    }

    /// <summary>
    ///     Doc comments here NAME <c>DrawText3D</c> to explain the rule, and a lint
    ///     satisfied by prose is a lint that fires on its own documentation.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }

            if (source[i] == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }

                continue;
            }

            sb.Append(source[i]);
        }

        return sb.ToString();
    }
}
