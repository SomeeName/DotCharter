using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Pays the FIRST-SHOW cost of an <c>IsVisible=false</c> subtree ahead of
///     time, off the user's critical path.
/// </summary>
/// <remarks>
///     <para>
///         Avalonia skips <c>MeasureCore</c> — and therefore style application,
///         <c>ApplyTemplate</c>, resource resolution and text layout — for an
///         invisible element. That is exactly why hiding a subtree is free per
///         frame (the rule this codebase enforces everywhere), and exactly why
///         the first time the user shows it, the whole realization lands in one
///         UI-thread block. Measured against the owner's report ("switching
///         views, opening dialogs, switching tabs always lags"): 20-137 ms per
///         settings tab, 1-41 ms per Play Mode screen, 5-110 ms per dialog —
///         each once per session, per surface.
///     </para>
///     <para>
///         <b>The realization is cached, which is the whole reason this works.</b>
///         Once a subtree has been measured, hiding it and showing it again costs
///         ~0.1 ms: the styles stay applied, the templates stay built and the
///         measure stays valid. One off-critical-path pass per surface buys every
///         later switch.
///     </para>
///     <para>
///         <b>It must NOT flip <c>IsVisible</c> to do that.</b> Five dialog views
///         (<c>SetBpm</c>, <c>Beat</c>, <c>Fhp</c>, <c>TempoMapOperation</c>,
///         <c>LatencyCalibrationWizard</c>) pull keyboard focus on
///         <c>IsVisible → true</c> and <c>SheetMusicView</c> schedules a rebuild — a
///         prewarm that toggled visibility would fire all of them behind the
///         user's back, and would fight the binding that owns the property.
///         Instead the root's own template is applied explicitly and its (visible)
///         visual children are measured directly: <c>Measure</c> only consults the
///         element's OWN <c>IsVisible</c>, so a visible child of a hidden parent
///         realizes in full without the parent's visibility ever changing.
///     </para>
///     <para>
///         Nested hidden subtrees are deliberately NOT recursed into. A collapsed
///         "Advanced" section inside a settings tab stays unrealized, which bounds
///         the prewarm to the surfaces one switch actually reveals.
///     </para>
/// </remarks>
public static class HiddenSubtreePrewarmer
{
    /// <summary>
    ///     Realizes one hidden subtree. The constraint is the part's own layout
    ///     SLOT (see <see cref="SlotSizeOf" />), falling back to
    ///     <paramref name="fallback" /> — normally the window size — when the slot
    ///     has not been arranged yet.
    ///     <para>
    ///         Never throws: a view that cannot measure in isolation must not take
    ///         the window down — it just stays cold and pays on first show.
    ///     </para>
    /// </summary>
    /// <returns><c>true</c> when the subtree was realized.</returns>
    public static bool Realize(Control part, Size fallback)
    {
        if (part.IsVisible) return false;

        var available = SlotSizeOf(part, fallback);
        if (!IsUsable(available)) return false;

        try
        {
            // The root's own template + styles: MeasureCore would normally do
            // this, and it is skipped for as long as the root stays hidden.
            part.ApplyTemplate();

            var realized = false;
            foreach (var child in part.GetVisualChildren().OfType<Layoutable>())
            {
                // The child is visible even though its parent is not, so its own
                // MeasureCore runs and realizes the whole subtree beneath it.
                child.Measure(available);
                // Arrange too: several layers (ScrollViewer viewports, wrapping
                // text, the ItemsControl panels) only finish their work here, and
                // an unarranged child re-runs it on the real first show.
                child.Arrange(new Rect(available));
                realized = true;
            }

            return realized;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Prewarm of {part.GetType().Name} failed: {ex}");
            return false;
        }
    }

    /// <summary>
    ///     Re-realizes a hidden subtree that was realized BEFORE and has gone stale
    /// since — its lists changed while it was hidden (the Song tab is
    ///     prewarmed at startup with no project, then the project opens behind it).
    /// </summary>
    /// <remarks>
    ///     <see cref="Realize" /> alone does nothing for that case: the stale nodes
    ///     are DEEP (a list's panel, a new row) and their ancestors' measures are
    ///     still valid, so measuring the part's children returns at once with the
    ///     cached size and the whole re-layout lands on the user's click. So every
    ///     stale node that the real show would lay out — one reached through visible
    ///     elements only, as <see cref="Realize" /> reaches them — has its ancestor
    ///     chain up to the part invalidated first, and then the part is realized.
    /// </remarks>
    /// <returns>The number of stale nodes found; 0 means nothing was done.</returns>
    public static int RealizeStale(Control part, Size fallback)
    {
        if (part.IsVisible) return 0;

        var found = 0;
        // A pass can invalidate what it just laid out (a list's extent changes and
        // its scroll bar appears), which the real layout manager absorbs by
        // looping; so does this, a bounded number of times.
        for (var pass = 0; pass < MaxStalePasses; pass++)
        {
            var stale = new List<Layoutable>();
            CollectStale(part, stale);
            if (stale.Count == 0) break;
            found += stale.Count;

            foreach (var node in stale)
            {
                for (var v = node as Visual; v is not null && !ReferenceEquals(v, part); v = v.GetVisualParent())
                    (v as Layoutable)?.InvalidateMeasure();
            }

            if (!Realize(part, fallback)) break;
        }

        return found;
    }

    private const int MaxStalePasses = 4;

    private static void CollectStale(Visual parent, List<Layoutable> stale)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            // A hidden child is a gate of its own: neither the real show nor
            // Realize lays it out, so its staleness is not this part's to pay.
            if (!child.IsVisible) continue;
            // Nor is a child a chunked panel still holds back: its chunk is
            // RealizeNextChunk's to grant, one turn at a time.
            if (parent is IChunkedPanel chunked && child is Control held && chunked.Withholds(held)) continue;
            if (child is Layoutable { IsMeasureValid: false } or Layoutable { IsArrangeValid: false })
                stale.Add((Layoutable)child);
            CollectStale(child, stale);
        }
    }

    /// <summary>
    ///     Lays out ONE more chunk of the first <see cref="IChunkedPanel" /> in the
    ///     realized hidden <paramref name="part" /> that still holds children back
    ///     (<see cref="ChunkedWrapPanel" />, <see cref="ChunkedStackPanel" />, a
    ///     <see cref="MasonryPanel" /> with a chunk size) — the drain's unit of work
    /// for a long non-virtualized list or a stack of heavy groups, one
    ///     call per dispatcher turn.
    /// </summary>
    /// <remarks>
    ///     Only panels the part's own layout reaches are considered — through
    ///     visible elements, as <see cref="Realize" /> reaches them; a held-back
    ///     child has no subtree yet, so a panel nested in one is found once its
    ///     chunk is laid out. The chunk is
    ///     granted and then laid out by <see cref="RealizeStale" />, which re-measures
    ///     the chain from the panel up to the part and nothing beside it.
    /// </remarks>
    /// <returns><c>false</c> when there was nothing left to lay out.</returns>
    public static bool RealizeNextChunk(Control part, Size fallback)
    {
        if (part.IsVisible) return false;
        var panel = ChunkedPanelsUnder(part).FirstOrDefault(p => p.HasBacklog);
        if (panel is null || !panel.RealizeNextChunk()) return false;
        RealizeStale(part, fallback);
        return true;
    }

    /// <summary>The <see cref="IChunkedPanel" />s a layout of <paramref name="part" />
    /// reaches (through visible elements only), in tree order, parents first.</summary>
    public static IEnumerable<IChunkedPanel> ChunkedPanelsUnder(Visual part)
    {
        foreach (var child in part.GetVisualChildren())
        {
            if (!child.IsVisible) continue;
            if (child is IChunkedPanel panel) yield return panel;
            foreach (var nested in ChunkedPanelsUnder(child))
                yield return nested;
        }
    }

    /// <summary>
    ///     Realizes <paramref name="parts" /> ONE PER dispatcher turn at
    ///     <see cref="DispatcherPriority.Background" />, so a 250-500 ms total
    ///     never lands as a single freeze and real input always preempts it —
    ///     and only while the input has been QUIET
    ///     (<see cref="PrewarmPacer" />): a step that has started cannot be
    ///     preempted, and one landing under a drag is the post-launch lag.
    ///     Call only once the first frame has been presented.
    ///     <para>
    /// <b>The list is LAZY:</b> <paramref name="parts" /> is
    ///         enumerated one item per turn, so whatever PRODUCES it — building a
    ///         deferred body, materializing a dialog view, listing an owner — is
    ///         paced like a realize instead of landing as one turn in front of all
    ///         of them. A <c>null</c> item is a turn that did only that producing
    ///         work. An exception from the producer ends the drain through
    ///         <paramref name="onDone" /> after <paramref name="onError" />.
    ///     </para>
    /// </summary>
    /// <param name="parts">The subtrees to realize, in the order the user is most
    ///     likely to reach them, produced lazily.</param>
    /// <param name="available">Evaluated per part — the window may be resized
    ///     between turns.</param>
    /// <param name="onDone">Runs on the UI thread once the producer is exhausted,
    ///     with (realized, total).</param>
    /// <param name="onError">Hears an exception thrown by the producer.</param>
    /// <param name="onStep">Hears every turn's cost (<see cref="DrainStep" />), so the
    ///     caller can log the long ones — a turn cannot be interrupted, so the longest
    ///     turn is what a click during the drain waits for.</param>
    public static void ScheduleDrain(
        IEnumerable<Control?> parts, Func<Size> available, Action<int, int>? onDone = null,
        Action<Exception>? onError = null, Action<DrainStep>? onStep = null)
    {
        var items = parts.GetEnumerator();
        var total = 0;
        var realized = 0;
        Control? settle = null;
        Control? chunking = null;

        void Step()
        {
            var start = Stopwatch.GetTimestamp();
            // The turn after a part is realized SETTLES it (see Settle): a
            // virtualizing list inside it learns its viewport only now.
            if (settle is { } settling)
            {
                settle = null;
                Settle(settling, available());
                chunking = settling;
                onStep?.Invoke(new DrainStep("settle", settling, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                PrewarmPacer.Post(Step);
                return;
            }

            // …and every turn after that lays out one more chunk of a long list
            // in it (IChunkedPanel) until none is left — a click waits for one
            // chunk, never the whole list.
            if (chunking is { } chunked)
            {
                if (RealizeNextChunk(chunked, available()))
                {
                    onStep?.Invoke(new DrainStep("chunk", chunked, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                    PrewarmPacer.Post(Step);
                    return;
                }

                chunking = null;
            }

            bool more;
            try
            {
                more = items.MoveNext();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                more = false;
            }

            if (!more)
            {
                items.Dispose();
                onDone?.Invoke(realized, total);
                return;
            }

            if (items.Current is { } part)
            {
                total++;
                if (Realize(part, available()))
                {
                    realized++;
                    settle = part;
                }
            }

            onStep?.Invoke(new DrainStep(items.Current is null ? "build" : "realize", items.Current,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds));
            PrewarmPacer.Post(Step);
        }

        PrewarmPacer.Post(Step);
    }

    /// <summary>
    ///     The second half of realizing a part, one dispatcher turn after
    ///     <see cref="Realize" />: runs the window's layout pass, which hands every
    ///     VIRTUALIZING panel in the just-realized subtree its effective viewport,
    ///     and then lays out whatever that invalidated (<see cref="RealizeStale" />).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <c>VirtualizingStackPanel</c> realizes rows for the viewport the
    ///         layout manager reports AFTER a layout pass, and
    ///         <see cref="Realize" />'s direct <c>Measure</c> is not one: it measured
    ///         the Amp &amp; FX rig's gear list against an infinite height, built one
    ///         row of 51, and the viewport that arrived afterwards invalidated the
    ///         panel under a hidden overlay that no layout pass ever measures again.
    ///         The user's first open of the rig then built the five visible rows
    ///         (185 controls, ten faceplates) on the click — ~60 ms of a 315 ms first
    /// open. The same holds for any list in any prewarmed surface.
    ///     </para>
    ///     <para>
    ///         Never writes <c>IsVisible</c>; costs one visual-tree walk when nothing
    ///         went stale.
    ///     </para>
    /// </remarks>
    /// <returns>The number of stale nodes laid out; 0 when the part was settled already.</returns>
    public static int Settle(Control part, Size fallback)
    {
        if (part.IsVisible) return 0;
        // The pass the dispatcher would run before the next frame anyway, run now;
        // the realize half below swallows a view that cannot measure alone.
        part.UpdateLayout();
        return RealizeStale(part, fallback);
    }

    /// <summary>
    ///     The constraint <paramref name="part" /> will really be measured with:
    ///     the size of the layout slot it sits in, minus its own margin. Falls
    ///     back to <paramref name="fallback" /> when the parent has not been
    ///     arranged yet.
    /// </summary>
    /// <remarks>
    ///     Measuring against the WINDOW size instead leaves the cached measure
    ///     wrong by exactly the chrome around the slot, so the real first show
    ///     re-measures — measured at 4.3 ms median / 23.6 ms worst per settings
    ///     tab, versus 0.2 ms once the slot size is used. Same realization, wrong
    ///     constraint: worth getting right.
    /// </remarks>
    public static Size SlotSizeOf(Control part, Size fallback)
    {
        var slot = (part.GetVisualParent() as Layoutable)?.Bounds.Size ?? default;
        if (!IsUsable(slot)) slot = fallback;

        var m = part.Margin;
        return new Size(
            Math.Max(0, slot.Width - m.Left - m.Right),
            Math.Max(0, slot.Height - m.Top - m.Bottom));
    }

    /// <summary>The hidden direct children of <paramref name="host" />, in
    /// declaration order. Empty when the host is null.</summary>
    public static IReadOnlyList<Control> HiddenChildrenOf(Panel? host) =>
        host is null ? [] : [.. host.Children.OfType<Control>().Where(c => !c.IsVisible)];

    /// <summary>The hidden content of <paramref name="host" /> — the shape a
    /// dialog view takes (a <c>UserControl</c> around one
    /// <c>IsVisible="{Binding IsOpen}"</c> scrim).</summary>
    public static IReadOnlyList<Control> HiddenContentOf(ContentControl? host) =>
        host?.Content is Control { IsVisible: false } content ? [content] : [];

    /// <summary>
    ///     Builds the <c>ContentTemplate</c> body of a DEFERRED host without laying
    ///     it out: the host's own template plus one <c>ContentPresenter.UpdateChild()</c>,
    ///     which is the construction half only. Style application, measure and
    ///     arrange stay with the drain, one surface per <c>Background</c> turn.
    /// </summary>
    /// <remarks>
    ///     A template body does not exist until a layout pass reaches its presenter,
    ///     and a host under a hidden root never runs one — which is the whole point
    ///     of declaring a screen that way (it keeps its construction out of the
    ///     startup phases). It also means nothing inside it can be FOUND, so an
    ///     owner that lists named surfaces inside such a body calls this first.
    ///     Never writes <c>IsVisible</c>. Returns whether a body exists afterwards
    ///     (a host whose content is still <c>null</c> builds nothing).
    /// </remarks>
    public static bool BuildDeferredBody(ContentControl? host)
    {
        if (host is null) return false;
        host.ApplyTemplate();
        host.Presenter?.UpdateChild();
        return host.Presenter?.Child is not null;
    }

    /// <summary>
    ///     The hidden controls named in <paramref name="names" />, in that order,
    ///     found anywhere under <paramref name="root" /> — the shape a gated body
    ///     NESTED inside a surface another sweep already reaches takes (a dialog's
    ///     selection-gated editor, a settings tab's Advanced section, a hub's
    ///     sub-tab). The prewarmer stops at the first hidden root on purpose, so
    ///     "the parent is prewarmed" buys these nothing; an owner lists them
    ///     AFTER their gated parent instead.
    /// </summary>
    /// <remarks>
    ///     Resolved through the namescope first and the LOGICAL tree second, never
    ///     the visual tree: the drain collects every surface up front, before any
    ///     parent has been realized, and a nested <c>UserControl</c>'s markup is
    ///     not a visual descendant until its own template has applied. It IS a
    ///     logical descendant from <c>InitializeComponent</c> on. A name that
    ///     resolves to nothing is skipped — the cross-check that every listed
    ///     name is declared in the markup lives in the tests.
    /// </remarks>
    public static IReadOnlyList<Control> HiddenNamedDescendants(Control? root, IEnumerable<string> names)
    {
        if (root is null) return [];
        var found = new List<Control>();
        List<Control>? logical = null;
        foreach (var name in names)
        {
            var part = root.FindControl<Control>(name);
            if (part is null)
            {
                logical ??= [.. root.GetLogicalDescendants().OfType<Control>()];
                part = logical.FirstOrDefault(c => c.Name == name);
            }
            if (part is { IsVisible: false }) found.Add(part);
        }
        return found;
    }

    /// <summary>One turn of <see cref="ScheduleDrain" />: what it did (<c>build</c> — the
    /// producer's own work, <c>realize</c>, <c>settle</c> or <c>chunk</c>), the part it did it to
    /// (null for a build) and what the turn cost, producing included.</summary>
    public readonly record struct DrainStep(string Kind, Control? Part, double Ms)
    {
        /// <summary>The part's name, else its type; empty for a build.</summary>
        public string PartName => Part is null ? "" : string.IsNullOrEmpty(Part.Name) ? Part.GetType().Name : Part.Name;
    }

    private static bool IsUsable(Size size) =>
        size is { Width: > 0, Height: > 0 } &&
        !double.IsNaN(size.Width) && !double.IsNaN(size.Height) &&
        !double.IsInfinity(size.Width) && !double.IsInfinity(size.Height);
}
