using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #211: on a laptop-sized window the chart editor's Inspector had no body. The timeline's
///     360 px floor (412 with the song map) won outright in <c>MainSplitGrid</c> and the bottom dock
///     got the remainder — 190 px at 1280x720, where the Sound tab's body measured 0 px and its
///     footer ended 27 px BELOW the status rail's top edge; 9 px of body at 1280x800.
///     <para>
///         The real <see cref="MainWindow" /> over the UI sweep's seeded song, resized between
///         sizes (one shell window per case, never closed by the test — see the <c>finally</c>).
///         Asserted per size: the Sound tab's section viewport is at least <see cref="MinBodyPx" />
///         (since #601 the tab has no pinned footer: every verb is in its section, under ONE 28 px
///         navigation row), and the timeline keeps at least
///         <see cref="EditorRowFloors.TimelineCompactPx" />. On the two TALL windows nothing may
///         change: the floors do not bind. Height-shaped, so the class is
///         in the release's de-DE / ja-JP layout filter. With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> it
///         also writes the PNGs (<see cref="NarrowWidthLayout.ArtifactDirectory" />).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class EditorShortWindowUiPerfSmokeTests
{
    private static readonly (int Width, int Height, bool Tall)[] Sizes =
    [
        (1920, 1080, true),
        (1280, 800, false),
        (1280, 720, false),
        (2560, 1440, true)
    ];

    [Fact]
    public async Task TheInspector_KeepsABody_AndTheTimelineItsFloor_AtEverySize() =>
        await HeadlessSessionFixture.Dispatch(() => WithShell((window, _, state, selection) =>
        {
            foreach (var (width, height, tall) in Sizes)
            {
                window.Width = width;
                window.Height = height;
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"editor-{width}x{height}");
                AssertLayout(window, width, height, tall, selected: false);

                if (tall) continue;

                // A multi-selection: the Sound tab's Selection section and its bulk verbs.
                var level = state.ActiveArrangement!.Levels[0];
                selection.SelectedNotes.Add(level.Notes[0]);
                selection.SelectedNotes.Add(level.Notes[1]);
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"editor-{width}x{height}-selected");
                AssertLayout(window, width, height, tall, selected: true);
                selection.ClearSelections();
                NarrowWidthLayout.Settle(window);
            }
        }), CancellationToken.None);

    /// <summary>
    ///     #235: selecting Annotations, Phrases or the Riff Library wrapped the Inspector's tab strip
    ///     to a second row at 1280 px (the selected tab kept its caption at the densest level), and
    ///     #233: at 1280x720 the Annotations tab showed one of six rows (half of one in German) beside
    ///     a 60-105 px note box. Every tab is selected in turn at both sizes, with the seeded song's
    ///     three-digit Issues count and six annotations of realistic length on the project.
    /// </summary>
    [Fact]
    public async Task TheTabStrip_StaysOneRow_AndAnnotationsKeepTheirRows_AtEverySize() =>
        await HeadlessSessionFixture.Dispatch(() => WithShell((window, vm, state, _) =>
        {
            SeedAnnotations(state.ActiveProject!);
            vm.Inspector.Issues.RefreshIssues();
            foreach (var (width, height) in new[] { (1280, 720), (1920, 1080) })
            {
                window.Width = width;
                window.Height = height;
                vm.Inspector.SelectedTabIndex = AnnotationsTab;
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"editor-{width}x{height}-annotations");
                AssertAnnotations(window, width, height);

                for (var tab = 0; tab < InspectorTabCount; tab++)
                {
                    vm.Inspector.SelectedTabIndex = tab;
                    NarrowWidthLayout.Settle(window);
                    if (tab == InspectorTabCount - 1) NarrowWidthLayout.Capture(window, $"editor-{width}x{height}-riff-library");
                    AssertTabStripIsOneRow(window, width, height, tab);
                }
            }

            vm.Inspector.SelectedTabIndex = 0;
        }), CancellationToken.None);

    /// <summary>The tab count in <c>Views/Editor/InspectorView.axaml</c>'s <c>InspectorTabRow</c>;
    /// the shell supplies the Riff Library and Chord shapes, so all of them are visible.</summary>
    private const int InspectorTabCount = 9;

    private const int AnnotationsTab = 5;

    /// <summary>The section body a short window must still leave the Sound tab, in px (#211): below
    /// it the tab shows no whole row of anything.</summary>
    private const double MinBodyPx = 100;

    /// <summary>
    ///     Whole annotation rows the list must show at 1280x720 (#233). The dock leaves the tab
    ///     panel ~220 px once the strip is one row (measured 218-220 in en-US, de-DE and ja-JP); the
    ///     header takes ~40 and the one-row composer ~70 with its margins, which leaves ~100 px of
    ///     list. A one-line row is 28 px plus 2 between rows, so three whole rows is what fits — and
    ///     three is the floor the issue asks for. Before the fix: 0 (en-US), the one row there was
    ///     71 px tall.
    /// </summary>
    private const int MinAnnotationRowsAt720 = 3;

    /// <summary>At 1920x1080 the 3:4 default split (#232, #81) gives the tab panel ~490 px and the list
    /// ~370, so all six seeded rows. Before the fix the ~50 px rows showed two of them, at the
    /// split the shell had before #232.</summary>
    private const int MinAnnotationRowsAt1080 = 6;

    /// <summary>The composer's note box must be at least this wide: about twenty characters of a
    /// note, the width at which what is typed still reads as a sentence (#233).</summary>
    private const double MinComposerTextPx = 160;

    private static readonly string[] AnnotationTexts =
    [
        "Check the bend release here - the original drops a quarter tone before the slide into the chorus",
        "Re-time the chorus phrase after the tempo map fix",
        "Harmonics at fret 12 sound off, try pinch harmonics instead",
        "Verse 2 riff matches verse 1 except the last bar",
        "Tone change for the solo",
        "Mark"
    ];

    private static void SeedAnnotations(ChartProject project)
    {
        project.Annotations.Clear();
        for (var i = 0; i < AnnotationTexts.Length; i++)
        {
            project.Annotations.Add(new ChartAnnotation
            {
                TimeMs = 4_000 + (i * 9_500),
                Kind = (ChartAnnotationKind)(i % 3),
                Text = AnnotationTexts[i],
                Resolved = i == 3
            });
        }
    }

    private static void AssertTabStripIsOneRow(MainWindow window, int width, int height, int selected)
    {
        var inspector = window.GetVisualDescendants().OfType<InspectorView>().Single();
        var row = NarrowWidthLayout.Named<OverflowTabStrip>(inspector, "InspectorTabRow");
        var tabs = row.Children.OfType<ToggleButton>().Where(t => t.IsVisible && !OverflowTabStrip.GetIsOverflowButton(t)).ToList();
        var shown = row.ShownTabs.OfType<ToggleButton>().ToList();
        var at = $"at {width}x{height} with tab {selected} selected";
        Assert.True(tabs.Count == InspectorTabCount, $"{at} the strip has {tabs.Count} of the {InspectorTabCount} tabs.");

        var tabHeight = shown.Max(t => t.Bounds.Height);
        var rows = shown.Select(t => Math.Round(NarrowWidthLayout.TopIn(t, row))).Distinct().Count();
        NarrowWidthLayout.Report($"[short-window] tab strip {at}: inspector {inspector.Bounds.Width:0.#} px, row " +
                          $"{row.Bounds.Width:0.#}x{row.Bounds.Height:0.#}, {row.Mode}, {shown.Count} shown, {row.OverflowTabs.Count} in More; widths " +
                          string.Join(" ", shown.Select(t => $"{t.Bounds.Width:0.#}")));

        Assert.True(rows == 1 && row.Bounds.Height <= tabHeight + 0.5,
            $"{at} the Inspector's tab strip is {rows} rows, {row.Bounds.Height:0.#} px tall for {tabHeight:0.#} px tabs (#235).");
        var checkedTab = Assert.Single(tabs, t => t.IsChecked == true);
        Assert.Equal(selected.ToString(System.Globalization.CultureInfo.InvariantCulture), checkedTab.Tag);

        // The selected tab always stands in the strip and NAMES itself (#573).
        Assert.Contains(checkedTab, shown);
        var caption = checkedTab.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.True(caption.IsEffectivelyVisible, $"{at} the selected tab shows no caption ({row.Mode}) (#573).");
        Assert.All(shown, tab =>
        {
            var (left, right) = NarrowWidthLayout.Span(tab, row);
            Assert.True(left >= -0.5 && right <= row.Bounds.Width + 0.5,
                $"{at} a tab spans {left:0.#}..{right:0.#} px in a {row.Bounds.Width:0.#} px strip.");
            Assert.True(tab.Focusable && tab.IsEffectivelyEnabled, $"{at} tab {tab.Tag} cannot take keyboard focus.");
        });
        Assert.All(tabs, tab =>
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(tab)), $"{at} tab {tab.Tag} has no accessible name."));
    }

    private static void AssertAnnotations(MainWindow window, int width, int height)
    {
        var at = $"at {width}x{height}";
        var notes = window.GetVisualDescendants().OfType<NotesInspectorView>().Single();
        var list = NarrowWidthLayout.Named<ListBox>(notes, "NotesList");
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible).ToList();
        var whole = items.Count(i =>
        {
            var top = NarrowWidthLayout.TopIn(i, list);
            return top >= -0.5 && top + i.Bounds.Height <= list.Bounds.Height + 0.5;
        });
        var text = NarrowWidthLayout.Named<TextBox>(notes, "NotesComposerText");
        NarrowWidthLayout.Report($"[short-window] annotations {at}: tab {notes.Bounds.Height:0.#} px, list {list.Bounds.Height:0.#} px, " +
                          $"{whole} whole row(s) of {AnnotationTexts.Length} (rows " +
                          string.Join(" ", items.Select(i => $"{i.Bounds.Height:0.#}")) + $"), note box {text.Bounds.Width:0.#} px");

        var minimum = height <= 720 ? MinAnnotationRowsAt720 : MinAnnotationRowsAt1080;
        Assert.True(whole >= minimum,
            $"{at} the Annotations list shows {whole} whole row(s) of {AnnotationTexts.Length} in {list.Bounds.Height:0.#} px - " +
            $"it needs at least {minimum} (#233).");
        Assert.True(text.Bounds.Width >= MinComposerTextPx,
            $"{at} the annotation note box is {text.Bounds.Width:0.#} px wide - under the {MinComposerTextPx} px a note needs (#233).");
    }

    /// <summary>The real shell over the UI sweep's seeded song, in the editor with the Inspector
    /// open. One shell window per case, never closed by the test - see the <c>finally</c>.</summary>
    private static void WithShell(Action<MainWindow, MainWindowViewModel, ProjectState, ISelectionService> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = services.GetRequiredService<ProjectState>();
        var selection = services.GetRequiredService<ISelectionService>();

        // Leaked shell state from other classes in this session (see TitleBarDragUiPerfSmokeTests).
        foreach (var dialog in vm.Dialogs.AllDialogs)
        {
            if (dialog.IsOpen)
                dialog.RequestClose();
        }

        vm.CommandPalette.Close();
        vm.IsGameplayPresentation = false;
        vm.SelectedMainTabIndex = 0;
        vm.IsInspectorVisible = true;
        vm.IsPreviewVisible = true;
        vm.IsSheetMusicVisible = false;
        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        NarrowWidthLayout.SeedSong(state);
        selection.ClearSelections();
        vm.Inspector.SelectedTabIndex = 0;

        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            body(window, vm, state, selection);
        }
        finally
        {
            // No window.Close(): closing the SHELL runs the app's shutdown path and ends the
            // testhost without a session end (no TRX). The dispatch releases the window, as it
            // does for TitleBarDragUiPerfSmokeTests.
            selection.ClearSelections();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
        }
    }

    private static void AssertLayout(MainWindow window, int width, int height, bool tall, bool selected)
    {
        var at = $"at {width}x{height}{(selected ? " with a selection" : "")}";
        var main = NarrowWidthLayout.Named<Grid>(window, "MainSplitGrid");
        var renderer = NarrowWidthLayout.Named<Control>(window, "MainChartRenderer");
        var status = NarrowWidthLayout.Named<Control>(window, "StatusBarHost");
        var inspector = window.GetVisualDescendants().OfType<InspectorView>().Single();
        var sound = inspector.GetVisualDescendants().OfType<SoundInspectorView>().Single();
        var body = NarrowWidthLayout.Named<ScrollViewer>(sound, "SoundSectionScroll");
        var section = NarrowWidthLayout.Named<Control>(sound, selected ? "SoundBulkCard" : "SoundTaskBody");
        var statusTop = NarrowWidthLayout.TopIn(status, window);

        Console.WriteLine($"[short-window] {at}: split {main.Bounds.Height:0.#} = " +
                          string.Join(" / ", main.RowDefinitions.Select(r => $"{r.ActualHeight:0.#} (min {r.MinHeight:0.#})")) +
                          $"; timeline {renderer.Bounds.Height:0.#}; inspector {inspector.Bounds.Height:0.#}, body {body.Viewport.Height:0.#}");

        Assert.True(section.IsEffectivelyVisible, $"{at} the Sound tab's {section.Name} is not the open section — repoint this test.");
        Assert.True(body.Viewport.Height >= MinBodyPx,
            $"{at} the Inspector's section body is {body.Viewport.Height:0.#} px tall (inspector {inspector.Bounds.Height:0.#}, " +
            $"dock row {main.RowDefinitions[2].ActualHeight:0.#}) — under the {MinBodyPx} px it needs to show rows (#211).");

        // The tab ends above the status rail: nothing of it is drawn under the window's chrome.
        var tabBottom = NarrowWidthLayout.TopIn(body, window) + body.Bounds.Height;
        Assert.True(tabBottom <= statusTop + 0.5,
            $"{at} the Sound tab ends at {tabBottom:0.#} px, under the status rail's top at {statusTop:0.#} (#211).");

        Assert.True(renderer.Bounds.Height >= EditorRowFloors.TimelineCompactPx - 0.5,
            $"{at} the timeline is {renderer.Bounds.Height:0.#} px — under its {EditorRowFloors.TimelineCompactPx} px floor.");

        if (!tall) return;

        // A tall window is EXACTLY as before #211: the timeline keeps its comfortable floor and the
        // dock's floor does not bind.
        var shell = (MainWindowViewModel)window.DataContext!;
        Assert.Equal(TimelineAreaMinHeight.For(sheetVisible: false, shell.IsMinimapShown, gameplayPresentation: false),
            main.RowDefinitions[0].MinHeight);
        Assert.True(main.RowDefinitions[2].ActualHeight > EditorRowFloors.DockFloorPx,
            $"{at} the dock row is {main.RowDefinitions[2].ActualHeight:0.#} px — its #211 floor now binds on a tall window.");

        // The split a first launch shows is EditorSplitDefaults' 3 : 4 (#232, #81), so on a tall window
        // the dock is the taller row. The layout controller's own 1* starting height used to win
        // here and give the timeline two thirds.
        Assert.True(main.RowDefinitions[2].ActualHeight > main.RowDefinitions[0].ActualHeight,
            $"{at} the dock row is {main.RowDefinitions[2].ActualHeight:0.#} px under a {main.RowDefinitions[0].ActualHeight:0.#} px " +
            "timeline — the first-launch split is not EditorSplitDefaults' 3 : 4.");
    }
}
