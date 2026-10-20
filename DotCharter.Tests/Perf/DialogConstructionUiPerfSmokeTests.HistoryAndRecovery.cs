// The undo-history, checkpoints and crash-recovery dialogs, including the painted-bounds geometry that proves the autosave stamp never overlaps the name block.

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI;
using DotCharter.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>
    ///     Edit history's row anatomy and its footer count. Everything here is
    ///     invisible to the shared grammar checks above, because those lay every
    ///     dialog out with NO DataContext — an ItemsControl with no items has no
    ///     rows at all, so the entire tree under the list frame goes unmeasured.
    ///     <para>
    ///         Three things a wash of accent colour could be hiding: a dot that
    ///         draws for BOTH states at once (the two Ellipses are separate
    ///         elements, so "IsVisible on the wrong one" renders as a dot that
    ///         never changes), a CURRENT tag that measured nothing because its
    ///         mono size token did not resolve, and a footer summary that is
    ///         empty because nothing ever called the rebuild that derives it.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EditHistory_ShowsOneDotPerRow_TagsCurrentAndBranch_AndCountsTheTree()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // The fake's Default variant carries a current row and a branch row;
            // the Summary it prints is derived from the rows by HistoryViewModel
            // itself (HistoryViewModelTests), so here it only has to reach the footer.
            var entry = HostedDialogs.Single(e => e.Contract == typeof(IHistoryScreen));
            WithHostedDialog(entry, FakeVariantNames.Default, (dialog, _) =>
            {
                var vm = (IHistoryScreen)dialog.DataContext!;

                var frame = Descendants<Border>(dialog)
                    .FirstOrDefault(b => b.Classes.Contains("DlgListFrame"));
                Assert.True(frame is not null, "the history list has no DlgListFrame");

                var rows = Descendants<Button>(frame!).ToList();
                Assert.Equal(vm.Nodes.Count, rows.Count);

                var currents = 0;
                var branches = 0;
                foreach (var row in rows)
                {
                    Assert.True(row.Bounds.Width > 1 && row.Bounds.Height > 1,
                        $"a history row laid out {row.Bounds} — it rendered nothing");

                    var dots = Descendants<Avalonia.Controls.Shapes.Ellipse>(row)
                        .Where(e => e.IsEffectivelyVisible).ToList();
                    Assert.True(dots.Count == 1,
                        $"a history row shows {dots.Count} state dots — exactly one of the two " +
                        "(current / not current) may ever be visible");
                    Assert.True(dots[0].Bounds.Width > 1 && dots[0].Bounds.Height > 1,
                        $"a history row's state dot laid out {dots[0].Bounds}");

                    var wash = Descendants<Border>(row).First(b => b.Name == "CurrentWash");
                    var current = Descendants<Border>(row).First(b => b.Name == "CurrentTag");
                    var branch = Descendants<Border>(row).First(b => b.Name == "BranchTag");
                    Assert.Equal(wash.IsEffectivelyVisible, current.IsEffectivelyVisible);
                    Assert.False(current.IsEffectivelyVisible && branch.IsEffectivelyVisible,
                        "a history row is tagged CURRENT and BRANCH at once");

                    if (current.IsEffectivelyVisible)
                    {
                        currents++;
                        Assert.True(current.Bounds.Width > 1 && current.Bounds.Height > 1,
                            $"the CURRENT tag laid out {current.Bounds} — it rendered nothing");
                        Assert.True(wash.Bounds.Width > 1,
                            $"the current row's wash laid out {wash.Bounds}");
                    }

                    if (branch.IsEffectivelyVisible)
                    {
                        branches++;
                        Assert.True(branch.Bounds.Width > 1 && branch.Bounds.Height > 1,
                            $"the BRANCH tag laid out {branch.Bounds} — it rendered nothing");
                    }
                }

                Assert.True(currents >= 1, "no history row is marked as the current state");
                Assert.True(branches >= 1, "no history row is tagged as a branch");

                var summary = Descendants<TextBlock>(dialog).FirstOrDefault(t => t.Name == "HistorySummary");
                Assert.True(summary is not null, "the footer summary (HistorySummary) is gone");
                Assert.Equal(vm.Summary, summary!.Text);
                Assert.True(summary.Bounds.Width > 1,
                    $"the footer summary laid out {summary.Bounds} — it rendered nothing");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Checkpoints' empty state, which is what the dialog shows the first
    ///     time anyone opens it: two lines, not one, and the second one QUIETER
    ///     than the first. <c>Border.DlgEmpty</c> dresses every TextBlock inside
    ///     it identically, so the hint's own size is set inline — a setter that
    ///     stopped applying would leave two lines at the same weight, which
    ///     renders fine and says the wrong thing.
    /// </summary>
    [Fact]
    public async Task Checkpoints_EmptyState_SaysBothWhatIsMissingAndWhatSavingWouldCapture()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.Contract == typeof(ICheckpointsScreen));
            WithHostedDialog(entry, FakeVariantNames.Empty, (dialog, _) =>
            {
                var empty = Descendants<Border>(dialog).FirstOrDefault(b => b.Classes.Contains("DlgEmpty"));
                Assert.True(empty is not null, "Checkpoints has no DlgEmpty state");
                Assert.True(empty!.Bounds.Height > 1,
                    $"the empty state laid out {empty.Bounds} — it rendered nothing");

                var lines = Descendants<TextBlock>(empty).ToList();
                Assert.Equal(2, lines.Count);
                foreach (var line in lines)
                {
                    Assert.False(string.IsNullOrWhiteSpace(line.Text), "an empty-state line says nothing");
                    Assert.True(line.Bounds.Width > 1 && line.Bounds.Height > 1,
                        $"an empty-state line laid out {line.Bounds}");
                }

                var hint = Descendants<TextBlock>(dialog).First(t => t.Name == "CheckpointsEmptyHint");
                // Literals on purpose, like the type-ladder case above: reading
                // the tokens would move both sides of the comparison together.
                Assert.Equal(FireTypeLadder.Size("FireSizeMeta"), hint.FontSize);
                Assert.Equal(FireTypeLadder.Size("FireSizeCaption"), lines.First(t => !ReferenceEquals(t, hint)).FontSize);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Crash recovery: the notice strip that opens the dialog, and a row that
    ///     prints its kind tag, its project path and its autosave stamp. The kind
    ///     tag is two mutually exclusive Borders (a saved project is neutral, a
    ///     never-saved one is amber), which is exactly the shape that renders
    ///     both at once when a negation is dropped.
    /// </summary>
    [Fact]
    public async Task Recovery_ShowsTheNotice_AndTagsEachSessionWithItsKindPathAndStamp()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var scope = App.Services!.CreateScope();
            var vm = RecoveryOver(scope, [(@"C:\charts\Matasaburo.rscp", "Matasaburo"), ("", "New song")]);

            var dialog = new RecoveryDialogView { DataContext = vm };
            WithLaidOutDialog(dialog, () =>
            {
                dialog.UpdateLayout();

                var notice = Descendants<Border>(dialog)
                    .FirstOrDefault(b => b.Classes.Contains("DlgStrip") && b.Classes.Contains("Notice"));
                Assert.True(notice is not null, "the recovery dialog opens without its reassurance strip");
                Assert.True(notice!.Bounds.Height > 1 && notice.Background is not null,
                    $"the notice strip laid out {notice.Bounds} / fill={notice.Background}");

                var rows = Descendants<Border>(dialog)
                    .Where(b => b.Classes.Contains("DlgListRow")).ToList();
                Assert.Equal(vm.Sessions.Count, rows.Count);

                var unsaved = 0;
                foreach (var row in rows)
                {
                    var saved = Descendants<Border>(row).First(b => b.Name == "KindTagSaved");
                    var never = Descendants<Border>(row).First(b => b.Name == "KindTagUnsaved");
                    Assert.True(saved.IsEffectivelyVisible ^ never.IsEffectivelyVisible,
                        "a recovery row shows both kind tags at once (or neither)");
                    var shown = saved.IsEffectivelyVisible ? saved : never;
                    if (!saved.IsEffectivelyVisible) unsaved++;
                    Assert.True(shown.Bounds.Width > 1 && shown.Bounds.Height > 1,
                        $"a recovery row's kind tag laid out {shown.Bounds}");

                    var path = Descendants<TextBlock>(row).First(t => t.Name == "SessionPath");
                    Assert.False(string.IsNullOrWhiteSpace(path.Text),
                        "a recovery row prints no path and no 'never saved' sentence");
                    Assert.True(path.Bounds.Width > 1, $"a recovery row's path laid out {path.Bounds}");

                    var stamp = Descendants<StackPanel>(row).First(p => p.Name == "SessionSavedAt");
                    Assert.True(stamp.IsEffectivelyVisible && stamp.Bounds.Width > 1,
                        $"a recovery row's autosave stamp laid out {stamp.Bounds}");
                    // The DATE is what tells two snapshots apart, so it is never trimmed:
                    // it lays out at least as wide as it asked to be.
                    var date = Descendants<TextBlock>(row).First(t => t.Name == "SessionSavedAtValue");
                    Assert.False(string.IsNullOrWhiteSpace(date.Text), "a recovery row prints no autosave date");
                    Assert.True(date.Bounds.Width >= date.DesiredSize.Width - 0.5,
                        $"the autosave date '{date.Text}' laid out {date.Bounds.Width:0.#} px of the " +
                        $"{date.DesiredSize.Width:0.#} it needs — it is cut");
                }

                Assert.Equal(1, unsaved);
                Assert.Contains(vm.Sessions.Count.ToString(), vm.Subtitle, StringComparison.Ordinal);
            });

        }, CancellationToken.None);
    }

    /// <summary>
    ///     The recovery row's autosave stamp may never be painted over the name
    ///     block beside it — the owner reported exactly that from a screenshot,
    ///     with "last autosaved …" sitting on top of an already-ellipsised
    ///     "Autosave only — no project file…".
    ///     <para>
    ///         The cause was NOT the star column being starved: measured, it got
    ///         216 px of the row's 666. It was that the name line was a HORIZONTAL
    ///         STACKPANEL, which measures its children with infinite width, then
    ///         arranges each at its own DesiredSize from x=0 and clips nothing —
    ///         so the kind tag was arranged at x=194..284 inside a 216 px panel,
    ///         54 px into the stamp's column, and the stamp (declared later, so
    ///         higher in z) painted over it. That is the same "a NoWrap TextBlock
    ///         in a horizontal StackPanel is never told a width" trap the UI guide
    ///         already documents; here it escaped a GRID CELL, which is what makes
    ///         it look like an overlap rather than a truncation.
    ///     </para>
    ///     <para>
    ///         So the assertion is on the geometry the bug had, not on the markup
    ///         that caused it: the union of everything column 0 PAINTS must not
    ///         intersect the stamp. It is checked with an ordinary title and with
    ///         one far too long for the row, because the fault only appears once
    ///         the name plus its tag outgrow the slot.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Recovery_TheAutosaveStamp_NeverPaintsOverTheNameBlock()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var scope = App.Services!.CreateScope();
            var vm = RecoveryOver(scope,
                     [
                         ("", "New song"),
                         ("", "A very long unsaved song title nobody would type, but the row must survive it"),
                         (@"C:\charts\Yorushika - Matasaburo\project.rscp", "Matasaburo")
                     ]);

            var dialog = new RecoveryDialogView { DataContext = vm };
            WithLaidOutDialog(dialog, () =>
            {
                dialog.UpdateLayout();

                var rows = Descendants<Border>(dialog)
                    .Where(b => b.Classes.Contains("DlgListRow")).ToList();
                Assert.Equal(vm.Sessions.Count, rows.Count);

                foreach (var row in rows)
                {
                    var facts = Descendants<StackPanel>(row).First(p => p.Name == "SessionFacts");
                    var stamp = Descendants<StackPanel>(row).First(p => p.Name == "SessionSavedAt");
                    Assert.True(stamp.IsEffectivelyVisible, "the autosave stamp is not being shown at all");

                    var painted = PaintedBounds(facts, row);
                    var stampRect = BoundsIn(stamp, row);
                    var overlap = painted.Intersect(stampRect);

                    Assert.True(overlap.Width <= 0.5 || overlap.Height <= 0.5,
                        $"the name block paints {painted} and the autosave stamp sits at {stampRect} — " +
                        $"they overlap by {overlap.Width:0.#}x{overlap.Height:0.#} px");

                    // …and the block must still be a block: an overlap can also be
                    // "fixed" by collapsing the thing that overflowed.
                    Assert.True(painted.Width > 1 && painted.Height > 1,
                        $"the name block laid out {painted}");
                }
            });

        }, CancellationToken.None);
    }

    /// <summary>Everything <paramref name="root" />'s subtree actually PAINTS, in
    /// <paramref name="space" />'s coordinates. Arranged bounds alone are not
    /// enough: a panel reports the slot it was given while its children are laid
    /// out past it, which is precisely the fault above.</summary>
    private static Rect PaintedBounds(Visual root, Visual space)
    {
        var rect = BoundsIn(root, space);
        foreach (var descendant in root.GetVisualDescendants())
        {
            if (descendant is Control { IsEffectivelyVisible: false }) continue;
            if (descendant.Bounds.Width <= 0 || descendant.Bounds.Height <= 0) continue;
            rect = rect.Union(BoundsIn(descendant, space));
        }

        return rect;
    }

    private static Rect BoundsIn(Visual visual, Visual space) =>
        new(visual.TranslatePoint(new Point(0, 0), space) ?? default, visual.Bounds.Size);

    /// <summary>
    ///     The REAL recovery view model, opened the way the startup launcher opens
    ///     it — so the rows carry the product's own name, tag, path and stamp text.
    /// </summary>
    private static IRecoveryScreen RecoveryOver(IServiceScope scope, (string Path, string Song)[] sessions)
    {
        var vm = scope.ServiceProvider.GetRequiredService<IRecoveryScreen>();
        ((DotCharter.Presentation.Dialogs.IDialogSetup<RecoveryRequest>)vm).Configure(new RecoveryRequest(
        [
            .. sessions.Select(s => new DotCharter.Services.Infrastructure.Recovery.RecoverableSession
            {
                SnapshotPath = @"C:\recovery\x.rscp",
                SidecarPath = @"C:\recovery\x.rscp.json",
                Metadata = new DotCharter.Services.Infrastructure.Recovery.RecoverySnapshotMetadata
                {
                    OriginalProjectPath = s.Path,
                    SongName = s.Song,
                    TimestampUtc = DateTimeOffset.UtcNow
                }
            })
        ]));
        return vm;
    }
}
