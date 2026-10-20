using System.Reflection;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The two things the owner reported dead on the Amp &amp; FX board: you
///     could not DRAG a unit to a new place in the chain, and the preset control
///     was a bare list rather than the tone BROWSER the classic rack had.
///
///     <para>
///         Both failures were of the same silent kind — the parts existed and
///         nothing connected them. <c>FireDragSession</c>, <c>FireDropMarker</c>
///         and <c>FireSlotHitTest</c> were built and unit-tested and then
///         referenced by no view; <c>PresetBrowserViewModel</c> carried the rail,
///         the cards, the search and save/overwrite/delete and the Fire screen
///         bound none of it. A build is clean either way and every existing test
///         passes, which is exactly why these guards are reachability guards.
///     </para>
/// </summary>
public sealed class RigBoardDragAndToneBrowserTests
{
    // ── the drop arithmetic ──────────────────────────────────────────────────

    /// <summary>
    ///     Insertion index (a GAP, <c>[0, count]</c>) → final position (a SLOT,
    ///     <c>[0, count-1]</c>). They differ by one when the unit moves forwards,
    ///     because it vacates its own place on the way past — get this wrong and
    ///     every forward drag lands one unit short, which still "works" and is
    ///     wrong on every single drag.
    /// </summary>
    [Theory]
    // forwards: unit 0 dropped in the gap after unit 2 lands AT 2
    [InlineData(0, 3, 4, 2)]
    [InlineData(0, 4, 4, 3)]
    // backwards: no vacating, so the gap index IS the destination
    [InlineData(3, 1, 4, 1)]
    [InlineData(3, 0, 4, 0)]
    // the two no-op gaps either side of the unit itself
    [InlineData(1, 1, 4, 1)]
    [InlineData(1, 2, 4, 1)]
    // clamped, never thrown: a marker past the end of a reflowed board
    [InlineData(0, 99, 4, 3)]
    [InlineData(0, 0, 0, 0)]
    public void AGapIndexBecomesTheRightFinalPosition(
        int from, int insertion, int count, int expected) => Assert.Equal(expected, RigBoardDragController.TargetIndex(from, insertion, count));

    /// <summary>A drag that ends where it began must resolve to the unit's own
    /// index — <c>MoveTo</c> treats that as a no-op and never touches the native
    /// chain.</summary>
    [Fact]
    public void DroppingAUnitBackOnItselfIsANoOp()
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i, RigBoardDragController.TargetIndex(i, i, 5));
            Assert.Equal(i, RigBoardDragController.TargetIndex(i, i + 1, 5));
        }
    }

    /// <summary>
    ///     The RUN-scoped drop arithmetic the lane fold needs: a run's items are
    ///     NOT contiguous in the flat chain (lane A and lane B interleave in
    ///     signal order), so a gap inside one run maps through the run's own
    ///     flat indices. The gaps touching the dragged unit itself must return
    ///     its own index — the flat-contiguous shortcut would shuffle the unit
    ///     past the OTHER lane's nodes on a visual no-op.
    /// </summary>
    [Theory]
    // a contiguous run (the serial board) behaves exactly like TargetIndex
    [InlineData(new[] { 0, 1, 2, 3 }, 0, 3, 2)]
    [InlineData(new[] { 0, 1, 2, 3 }, 3, 0, 0)]
    // interleaved lane: lane A sits at flat 1 and 4 with lane B between
    [InlineData(new[] { 1, 4 }, 1, 2, 4)]   // forwards past the lane's last item
    [InlineData(new[] { 1, 4 }, 4, 0, 1)]   // backwards to the lane's head
    // the no-op gaps either side of the unit itself, interleaved or not
    [InlineData(new[] { 1, 4 }, 1, 0, 1)]
    [InlineData(new[] { 1, 4 }, 1, 1, 1)]
    [InlineData(new[] { 1, 4 }, 4, 1, 4)]
    [InlineData(new[] { 1, 4 }, 4, 2, 4)]
    // three lane items with a foreign node inside the span
    [InlineData(new[] { 2, 3, 6 }, 2, 3, 6)] // to the lane's tail: insert-after-6 (7), minus the vacated slot
    [InlineData(new[] { 2, 3, 6 }, 6, 0, 2)] // to the lane's head
    // degenerate: an empty run can only keep the unit where it is
    [InlineData(new int[0], 7, 0, 7)]
    public void ALaneGapBecomesTheRightFlatPosition(
        int[] runFlatIndices, int fromFlat, int gapInRun, int expected)
    {
        Assert.Equal(expected,
            RigBoardDragController.TargetFlatIndex(runFlatIndices, fromFlat, gapInRun));
    }

    /// <summary>
    ///     The CROSS-RUN half: dropping a unit into a run it is not part of —
    ///     serial→lane, lane→lane, lane→serial, the gesture the owner reached
    ///     for and the first cut refused. No own-gap no-op exists here (the unit
    ///     has no gap of its own in a foreign run), and the tail gap of a
    ///     foreign run must be a real destination, not mistaken for one — that
    ///     mistake is exactly what <c>TargetFlatIndex</c>'s not-found index
    ///     would make of it.
    /// </summary>
    [Theory]
    // serial node (flat 0) into an interleaved lane at 2/4: before its head → 1
    // (the unit vacates flat 0 on the way forwards)
    [InlineData(new[] { 2, 4 }, 0, 0, -1, 1)]
    // …and at the lane's tail: after flat 4 is 5, minus the vacated slot → 4
    [InlineData(new[] { 2, 4 }, 0, 2, -1, 4)]
    // backwards: a late node (flat 6) to a lane's head — no vacating adjustment
    [InlineData(new[] { 1, 3 }, 6, 0, -1, 1)]
    // the foreign run's TAIL for a unit sitting just past the region (e.g. the
    // node after the merger): "after the run's last item" is flat 4 — right
    // before the merger — NOT the unit's own slot, or the drop would leave it
    // outside the region it was dropped into
    [InlineData(new[] { 2, 3 }, 5, 2, -1, 4)]
    // an EMPTY lane: the fallback is the region's body start (after the
    // splitter); a unit from before the splitter vacates a slot on the way
    [InlineData(new int[0], 0, 0, 2, 1)]
    [InlineData(new int[0], 5, 0, 2, 2)]
    // no live region to fall back to — refuse rather than guess
    [InlineData(new int[0], 5, 0, -1, -1)]
    public void ACrossRunGapBecomesTheRightFlatPosition(
        int[] runFlatIndices, int fromFlat, int gapInRun, int emptyRunFallback, int expected)
    {
        Assert.Equal(expected, RigBoardDragController.TargetFlatIndexInto(
            runFlatIndices, fromFlat, gapInRun, emptyRunFallback));
    }

    /// <summary>
    ///     The GEAR-RAIL drop arithmetic: a new unit is appended natively and
    ///     then moved, so the slot is the RAW insertion index — no vacated-slot
    ///     adjustment, because the new node comes from the tail. An empty lane
    ///     resolves to the region's body start; the branch tag (applied after)
    ///     is what decides the lane.
    /// </summary>
    [Theory]
    [InlineData(new[] { 0, 1, 2 }, 0, -1, 0)]  // serial head
    [InlineData(new[] { 0, 1, 2 }, 3, -1, 3)]  // serial tail
    [InlineData(new[] { 2, 4 }, 1, -1, 4)]     // between two interleaved lane items
    [InlineData(new[] { 2, 4 }, 2, -1, 5)]     // the lane's tail: right after its last
    [InlineData(new[] { 2, 4 }, 99, -1, 5)]    // clamped, never thrown
    [InlineData(new int[0], 0, 3, 3)]          // empty lane → the region's body start
    [InlineData(new int[0], 0, -1, -1)]        // no region → refuse
    public void AGearDropResolvesToTheRawInsertionSlot(
        int[] runFlatIndices, int gapInRun, int emptyRunFallback, int expected)
    {
        Assert.Equal(expected, RigBoardDragController.InsertFlatIndex(
            runFlatIndices, gapInRun, emptyRunFallback));
    }

    // ── the browser's reachability ───────────────────────────────────────────

    /// <summary>
    ///     Everything the tone drawer needs, in the two places it has to exist:
    ///     as a real member on the view model, and as a binding in the screen's
    ///     markup. The rack had all of these and the Fire screen had none of
    ///     them; a member with no binding is a feature nobody can reach, and a
    ///     binding with no member is a silent no-op (a failed binding logs and
    ///     the control just sits there).
    /// </summary>
    [Theory]
    [InlineData(typeof(PresetBrowserViewModel), "ApplyCardCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "BeginSaveAsNewCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "ConfirmSaveCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "CancelSaveCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "OverwriteCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "DeleteCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "PrevPresetCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "NextPresetCommand")]
    [InlineData(typeof(PresetBrowserViewModel), "Rail")]
    [InlineData(typeof(PresetBrowserViewModel), "Cards")]
    [InlineData(typeof(PresetBrowserViewModel), "SearchText")]
    [InlineData(typeof(PresetBrowserViewModel), "SelectedRail")]
    [InlineData(typeof(PresetBrowserViewModel), "SaveCategories")]
    // Since W4 the markup gates on AmpFxRigViewModel.IsTonesMode, which IS
    // PedalboardViewModel.IsBrowserOpen under a word that says what it does —
    // so the FLAG is checked through the screen model and the rig's own
    // property is still reached by ToggleBrowserCommand below.
    [InlineData(typeof(AmpFxRigViewModel), "IsTonesMode")]
    [InlineData(typeof(PedalboardViewModel), "ToggleBrowserCommand")]
    public void TheDrawerBindsAMemberThatExists(Type owner, string member)
    {
        Assert.True(
            owner.GetProperty(member, BindingFlags.Public | BindingFlags.Instance) != null,
            $"{owner.Name}.{member} is gone — the tone drawer's binding is now a silent no-op");

        // The screen (header pill, save dialog) and the Tones view (the library)
        // together are the rig's markup.
        Assert.Contains(member, PlayModeMarkup(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The drawer is a SECTION AT THE BOARD'S FOOT — not a screen, and not a
    ///     centred modal either.
    ///
    ///     <para>
    ///         It shipped as a scrimmed card in the middle of the screen and the
    ///         owner rejected that: "the button that opens all the presets also
    ///         shouldnt be a own screen — both in the previous design and in the
    ///         html design example it opens a section below". A modal in the
    ///         middle covers the board you are auditioning the tone against.
    ///     </para>
    ///
    ///     <para>
    ///         So it takes the module dock's own row, which is what makes the
    ///         board shrink rather than be covered — and the two must be mutually
    ///         exclusive, because drawing both put the dock's "nothing selected"
    ///         copy straight through the drawer's preset cards.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheDrawerIsASectionBelowTheBoard_NotAModalAndNotAScreen()
    {
        // The Tones mode is its own view since 2026-09-27 (RigTonesView), hosted
        // by the rig screen as `RigTonesMode`. The surface is read from THAT file
        // and the host from the screen's; a guard reading only the screen would
        // pass or fail on a name that no longer lives there.
        var tones = TonesMarkup();
        var start = tones.IndexOf("x:Name=\"RigToneBrowser\"", StringComparison.Ordinal);
        Assert.True(start > 0, "the tone drawer is gone from RigTonesView");

        // From a little BEFORE the name: the Border opens with its Grid.Row and
        // the x:Name follows it on the same line.
        var element = tones[Math.Max(0, start - 200)..Math.Min(tones.Length, start + 400)];

        // Row 1 of the Tones view's root grid — the read-only chain strip is
        // row 0. It was row 1 of the inline RigTonesMode grid (W4), row 0 of
        // RigWorkspace (W1) and row 3 of RigFlatBody before that. Still NOT a
        // scrim over the screen, which is the part of this case that has never
        // changed.
        Assert.Contains("Grid.Row=\"1\"", element, StringComparison.Ordinal);
        Assert.DoesNotContain("FireOverlayScrim", element, StringComparison.Ordinal);
        Assert.DoesNotContain("FireOverlayScrim", tones, StringComparison.Ordinal);

        var markup = ScreenMarkup();
        var mode = markup.IndexOf("x:Name=\"RigTonesMode\"", StringComparison.Ordinal);
        Assert.True(mode > 0, "the Tones mode host is gone");
        var host = markup[Math.Max(0, mode - 200)..Math.Min(markup.Length, mode + 200)];
        Assert.Contains("views:RigTonesView", host, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsTonesMode}\"", host, StringComparison.Ordinal);

        // …and the mode REPLACES the canvas body rather than covering it: the
        // two are siblings in one cell, each gated on the flag.
        var body = markup.IndexOf("x:Name=\"RigFlatBody\"", StringComparison.Ordinal);
        Assert.True(body > 0, "the canvas body is gone");
        Assert.Contains("IsVisible=\"{Binding !IsTonesMode}\"",
            markup[body..Math.Min(markup.Length, body + 200)], StringComparison.Ordinal);

        // The module editor's dock is NOT hidden by the tone surface's own flag
        // — it is inside the canvas body, which the mode replaces wholesale.
        var dock = markup.IndexOf("x:Name=\"RigDock\"", StringComparison.Ordinal);
        Assert.True(dock > 0, "the module editor dock is gone");
        Assert.DoesNotContain("IsVisible=\"{Binding !Rig.IsBrowserOpen}\"",
            markup[dock..Math.Min(markup.Length, dock + 300)], StringComparison.Ordinal);

        // …and it is NOT a PlayModeScreen value, which is what would make it a
        // screen you navigate to and have to navigate back from.
        Assert.DoesNotContain("ToneBrowser", Enum.GetNames<PlayModeScreen>());
    }

    /// <summary>The board's drag handle is the card HEAD. If that class is ever
    /// renamed in the markup the controller stops arming and the board silently
    /// goes back to being un-reorderable.</summary>
    [Fact]
    public void TheBoardCardStillCarriesTheGrabHandleClass() => Assert.Contains("FireUnitHead", PlayModeMarkup(), StringComparison.Ordinal);

    /// <summary>The rig's whole markup: the screen plus its Tones view.</summary>
    private static string PlayModeMarkup() => ScreenMarkup() + TonesMarkup();

    private static string ScreenMarkup() => View("AmpFxRigView.axaml");

    private static string TonesMarkup() => View("RigTonesView.axaml");

    private static string View(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", file);
        Assert.True(File.Exists(path), $"{file} moved — this guard is blind");
        return File.ReadAllText(path);
    }
}
