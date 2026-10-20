using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Inspector at the owner's REAL pane size — 694 x 534, the
///     <c>LayoutInspectorWidth</c> / <c>LayoutDockHeight</c> in his
///     <c>appconfig.json</c> — with a real project loaded and a real sound
///     selected (#117, #118, #119).
///     <para>
///         Every other inspector smoke test lays the tabs out with no
///         DataContext, so every <c>IsVisible</c> gate on the Sound tab is
///         false and its HEIGHT is never measured. This one binds the app's own
///         <see cref="InspectorViewModel" /> to the 240-sound stress chart the UI
///         sweep uses and asserts the geometry the issues are about: a plain
///         note's Placement and Technique sections each fit one pane, the rail
///         marks a bent note's Bends entry, and a chord's repeated finding
///         renders as ONE row.
///     </para>
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (Skia drawing) each case also
///         writes a PNG to <c>TestResults/inspector-fit/</c> — the before/after
///         evidence for the PR. The assertions run identically without it.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SoundTabFitUiPerfSmokeTests
{
    private const double PaneWidth = 694;
    private const double PaneHeight = 534;

    private static string ArtifactDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "inspector-fit");

    /// <summary>
    ///     #118, on the #601 rail: one plain note (no findings, no bends) needs ONE pane height per
    ///     section — Placement's and Technique's bottom edges sit inside the Sound tab's scroll
    ///     viewport with the scroll offset at zero. Before #118 the note needed ~1,060 px.
    /// </summary>
    [Fact]
    public async Task APlainNote_FitsTheOwnersPane() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(pane =>
        {
            var note = pane.Level.Notes.First(n => n.BendValues.Count == 0 && !pane.Vm.Issues.Items.Any(i => ReferenceEquals(i.Note, n)));
            Select(pane, note);
            Capture(pane, "note-plain-694x534");

            var sound = pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            AssertFits(pane, Named<Control>(sound, "SoundPlacementCard"), "Placement");
            pane.Vm.Sections.ChooseCommand.Execute(SoundSection.Technique);
            Settle(pane.Window);
            AssertFits(pane, Named<Control>(sound, "SoundNoteTechniqueCard"), "Technique");
        }), CancellationToken.None);

    /// <summary>
    ///     Bends is a section of its own since #601 (it was a disclosure that opened by itself for a
    ///     bent note, #118). The rail must not switch sections by itself, so it MARKS a bent sound
    ///     instead: the Bends entry wears a dot for a bent note and none for a plain one.
    /// </summary>
    [Fact]
    public async Task TheBendsEntry_MarksABentNote() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(pane =>
        {
            var plain = pane.Level.Notes.First(n => n.BendValues.Count == 0);
            var bent = pane.Level.Notes.Last(n => n.BendValues.Count == 0);
            bent.BendValues.Add(new BendValue(bent.Position, 1.0));

            var sound = pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            var dot = Named<Control>(sound, "SoundRailBendsDot");

            Select(pane, plain);
            Assert.False(dot.IsEffectivelyVisible, "the Bends entry is marked for a note with no bend.");

            Select(pane, bent);
            Capture(pane, "note-bent-694x534");
            Assert.True(dot.IsEffectivelyVisible, "the Bends entry is not marked for a note that HAS a bend.");
            Assert.Equal(SoundSection.Placement, pane.Vm.Sections.Section);

            Select(pane, plain);
            Assert.False(dot.IsEffectivelyVisible, "the Bends mark stayed after moving back to a plain note.");
        }), CancellationToken.None);

    /// <summary>
    ///     #117: a chord whose notes break the SAME rule prints that sentence
    ///     once, and the header badge counts problems, not notes.
    /// </summary>
    [Fact]
    public async Task AChordsRepeatedFinding_RendersOnce() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(pane =>
        {
            var chord = pane.Level.Chords.FirstOrDefault(c =>
                pane.Vm.Issues.Items.Count(i => ReferenceEquals(i.Chord, c)) >
                pane.Vm.Issues.Items.Where(i => ReferenceEquals(i.Chord, c)).Select(i => (i.Type, i.Message)).Distinct().Count());
            Assert.True(chord != null, "the fixture has no chord with a finding repeated across its notes — the case #117 is about.");

            Select(pane, chord!);
            // #601: the findings are their own section of the rail.
            pane.Vm.Sections.ChooseCommand.Execute(SoundSection.Findings);
            Settle(pane.Window);
            Capture(pane, "chord-694x534");

            var raw = pane.Vm.Issues.Items.Where(i => ReferenceEquals(i.Chord, chord)).ToList();
            var distinct = raw.Select(i => (i.Type, i.Message)).Distinct().Count();
            var sound = pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            var list = Named<ItemsControl>(sound, "SoundFindingsList");
            var rows = list.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("FieldIssue"));

            Console.WriteLine($"[inspector-fit] chord: {raw.Count} raw findings, {distinct} distinct, {rows} rows rendered.");
            Assert.Equal(distinct, rows);
            Assert.Equal(distinct, pane.Vm.SelectionIssueCount);
            Assert.True(rows < raw.Count, "the fold did nothing: as many rows as raw findings.");
        }), CancellationToken.None);

    /// <summary>
    ///     #119, on the #601 rail: nothing selected — the next task, its verbs and the playhead
    ///     block lay out, and the Issues tab (the ONE home of the export check) is captured next
    ///     to it.
    /// </summary>
    [Fact]
    public async Task TheEmptyStateAndTheIssuesTab_LayOut() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(pane =>
        {
            pane.Selection.ClearSelections();
            Settle(pane.Window);
            Capture(pane, "empty-694x534");

            var sound = pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            foreach (var name in new[] { "SoundTaskCard", "SoundTaskTitle", "SoundTaskPlayhead" })
            {
                var part = Named<Control>(sound, name);
                Assert.True(part.IsEffectivelyVisible && part.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {part.Bounds}");
            }

            pane.Vm.SelectedTabIndex = InspectorViewModel.IssuesTabIndex;
            Settle(pane.Window);
            Capture(pane, "issues-694x534");
            var issues = pane.View.GetVisualDescendants().OfType<IssuesInspectorView>().Single();
            Assert.True(issues.Bounds is { Width: > 1, Height: > 1 }, $"the Issues tab laid out as {issues.Bounds}");
        }), CancellationToken.None);

    /// <summary>
    ///     #530, end to end through the real view: a chord where only one string
    ///     slides reads as MIXED (blank field, each string spelled out, the line
    ///     laid out), a typed target moves each string by the same offset, a
    ///     target that would push a string off the fretboard is refused with a
    ///     visible reason and the field put back, and the string-edit mask
    ///     narrows both the readout and the write to one string. The pure rule
    ///     is pinned in the default gate by <c>ChordSlideScopeTests</c>.
    /// </summary>
    [Fact]
    public async Task APartialChordSlide_ReadsPerString_AndATypedTargetMovesEachString() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(pane =>
        {
            var state = App.Services!.GetRequiredService<ProjectState>();
            var chord = pane.Level.Chords.First(c => c.ChordNotes.Count >= 2);
            chord.ChordNotes.Clear();
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, SlideTo = 6 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 8 });
            var low = () => chord.ChordNotes.Single(n => n.StringIndex == 1);
            var high = () => chord.ChordNotes.Single(n => n.StringIndex == 2);

            try
            {
                Select(pane, chord);
                // #601: the slide field lives in the chord's Technique section.
                pane.Vm.Sections.ChooseCommand.Execute(SoundSection.Technique);
                Settle(pane.Window);
                Capture(pane, "chord-partial-slide-694x534");

                var sound = pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();
                var field = Named<NumericUpDown>(sound, "ChordSlideToField");
                var summary = Named<TextBlock>(sound, "ChordSlideMixedSummary");
                var refusal = Named<Border>(sound, "ChordSlideRefusalBar");

                Assert.True(pane.Vm.ChordSlideIsMixed, "a chord whose 5 slides and whose 8 holds read as one agreed slide.");
                Assert.Null(field.Value);
                Assert.True(summary.IsEffectivelyVisible, "the per-string slide line is hidden for a partial slide.");
                Assert.True(summary.Bounds is { Width: > 1, Height: > 1 }, $"the per-string slide line laid out as {summary.Bounds}.");
                Assert.Contains("5→6", summary.Text);
                Assert.Contains("8", summary.Text);

                // Typed on both strings: the 5's target, and the 8 moves by the same +2.
                field.Value = 7;
                Settle(pane.Window);
                Assert.Equal(7, low().SlideTo);
                Assert.Equal(10, high().SlideTo);
                Assert.False(pane.Vm.ChordSlideIsMixed);
                Assert.Equal(7m, field.Value);
                Assert.False(summary.IsEffectivelyVisible);

                // 22 would put the 8 on fret 25: nothing changes, and the panel says why.
                field.Value = 22;
                Settle(pane.Window);
                Capture(pane, "chord-slide-refused-694x534");
                Assert.Equal(7, low().SlideTo);
                Assert.Equal(10, high().SlideTo);
                Assert.True(refusal.IsEffectivelyVisible, "a refused slide target left no visible reason.");
                Assert.True(refusal.Bounds is { Width: > 1, Height: > 1 }, $"the refusal laid out as {refusal.Bounds}.");
                Assert.Equal(7m, field.Value);

                // The mask narrows it to the 8 alone: its own target reads, and a
                // typed one moves only it.
                state.StringEditMask = StringEditMask.FromStrings(2);
                Settle(pane.Window);
                Assert.Equal(10m, field.Value);
                Assert.False(refusal.IsEffectivelyVisible, "a stale refusal outlived a re-read of the chord.");
                field.Value = 9;
                Settle(pane.Window);
                Assert.Equal(7, low().SlideTo);
                Assert.Equal(9, high().SlideTo);
            }
            finally
            {
                state.StringEditMask = StringEditMask.Empty;
            }
        }), CancellationToken.None);

    /// <summary>
    ///     #564, through the real view: a selected chord shows ONE line saying whether it ships as
    ///     Rocksmith's repeat box and why (not) — the export rule's own verdict for that chord, in the
    ///     app's language — laid out non-zero; a second chord in the selection hides it. The reasons
    ///     themselves are pinned in the default gate by <c>RepeatChordVerdictTests</c>.
    /// </summary>
    [Fact]
    public async Task ASelectedChord_SaysWhetherItShipsAsARepeat() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(pane =>
        {
            var state = App.Services!.GetRequiredService<ProjectState>();
            var localization = App.Services!.GetRequiredService<DotCharter.Localization.ILocalizationService>();
            var arrangement = state.ActiveArrangement!;
            var verdicts = DotCharter.Services.Export.Rocksmith.RepeatChords.Explain(arrangement.ChordTemplates,
                pane.Level.Notes, pane.Level.Chords, pane.Level.HandShapes, state.Beats);
            var chord = pane.Level.Chords.First(c => c.ChordNotes.Count >= 2);

            Select(pane, chord);
            // #601: the repeat line lives in the chord's Technique section, under "Whole chord".
            pane.Vm.Sections.ChooseCommand.Execute(SoundSection.Technique);
            Settle(pane.Window);
            Capture(pane, "chord-repeat-verdict-694x534");

            var sound = pane.View.GetVisualDescendants().OfType<SoundInspectorView>().Single();
            var line = Named<TextBlock>(sound, "ChordRepeatVerdictLine");
            Assert.True(line.IsEffectivelyVisible, "a selected chord shows no repeat line.");
            Assert.True(line.Bounds is { Width: > 1, Height: > 1 }, $"the repeat line laid out as {line.Bounds}.");
            Assert.Equal(RepeatVerdictText.Format(verdicts[chord], arrangement.Tuning.Length, arrangement.Tuning, localization), line.Text);

            pane.Selection.SelectedChords.Add(pane.Level.Chords.First(c => !ReferenceEquals(c, chord)));
            Settle(pane.Window);
            Assert.False(line.IsEffectivelyVisible, "the repeat line speaks for ONE chord, not a selection.");
        }), CancellationToken.None);

    // ── harness ────────────────────────────────────────────────────────────

    private sealed record Pane(
        Window Window, InspectorView View, InspectorViewModel Vm, ISelectionService Selection, ChartLevel Level);

    private static void WithPane(Action<Pane> body)
    {
        var services = App.Services
                       ?? throw new InvalidOperationException("the headless app session did not boot.");
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();
        var vm = services.GetRequiredService<InspectorViewModel>();

        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        var previousTab = vm.SelectedTabIndex;
        Window? window = null;
        try
        {
            var level = Seed(state);
            vm.SelectedTabIndex = 0;
            vm.Issues.RefreshIssues();

            var view = new InspectorView { DataContext = vm };
            window = new Window { Width = PaneWidth, Height = PaneHeight, Content = view };
            window.Show();
            Settle(window);

            body(new Pane(window, view, vm, selection, level));
        }
        finally
        {
            selection.ClearSelections();
            vm.SelectedTabIndex = previousTab;
            window?.Close();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The UI sweep's fixture, one arrangement of it: the 240-sound
    /// stress chart with its anchors, hand shapes and tone changes, so the lint
    /// finds what it finds on a real chart.</summary>
    private static ChartLevel Seed(ProjectState state)
    {
        var chart = StressChartFactory.Chart(soundCount: 240, lengthMinutes: 2);
        var project = new ChartProject { Chart = { SongName = "Inspector fit fixture", Artist = "DotCharter test harness" } };
        foreach (var beat in chart.Beats) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        var level = new ChartLevel { DifficultyIndex = 0 };
        foreach (var note in chart.Notes) level.Notes.Add(note);
        foreach (var chord in chart.Chords) level.Chords.Add(chord);
        foreach (var shape in chart.HandShapes) level.HandShapes.Add(shape);
        foreach (var anchor in chart.Anchors) level.Anchors.Add(anchor);
        arrangement.Levels.Add(level);
        foreach (var point in chart.EventPoints) arrangement.EventPoints.Add(point);
        foreach (var tone in chart.Tones) arrangement.ToneChanges.Add(tone);
        project.Arrangements.Add(arrangement);

        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        return level;
    }

    /// <summary>The section's bottom edge sits inside the scroll viewport with the offset at zero.</summary>
    private static void AssertFits(Pane pane, Control section, string what)
    {
        Assert.True(section.IsEffectivelyVisible && section.Bounds is { Width: > 1, Height: > 1 },
            $"{what} laid out as {section.Bounds} — the selection did not reach the tab.");
        var host = section.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.Equal(0, host.Offset.Y);
        var viewportBottom = host.TranslatePoint(new Point(0, host.Viewport.Height), pane.Window)!.Value.Y;
        var bottom = section.TranslatePoint(new Point(0, section.Bounds.Height), pane.Window)!.Value.Y;
        Console.WriteLine($"[inspector-fit] plain note, {what}: bottom {bottom:0} px, viewport bottom {viewportBottom:0} px.");
        Assert.True(bottom <= viewportBottom + 0.5,
            $"a plain note's {what} section ends at {bottom:0} px but the Sound tab's viewport ends at " +
            $"{viewportBottom:0} px at {PaneWidth}x{PaneHeight} — one note no longer fits one pane (#118).");
    }

    private static void Select(Pane pane, object sound)
    {
        pane.Selection.ClearSelections();
        if (sound is GuitarNote note) pane.Selection.SelectedNotes.Add(note);
        else if (sound is Chord chord) pane.Selection.SelectedChords.Add(chord);
        Settle(pane.Window);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named '{name}' in {root.GetType().Name} — repoint this test.");

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Pane pane, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        Directory.CreateDirectory(ArtifactDirectory);
        Save(pane.Window, Path.Combine(ArtifactDirectory, name + ".png"));

        // The whole tab, unclipped, so the before/after shows what scrolls.
        pane.Window.Height = 2000;
        Settle(pane.Window);
        Save(pane.Window, Path.Combine(ArtifactDirectory, name.Replace("694x534", "694x2000", StringComparison.Ordinal) + ".png"));
        pane.Window.Height = PaneHeight;
        Settle(pane.Window);
    }

    private static void Save(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(path, new PngBitmapEncoderOptions());
    }
}
