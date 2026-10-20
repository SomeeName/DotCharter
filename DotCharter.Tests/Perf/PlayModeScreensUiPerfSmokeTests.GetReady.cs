// The Get Ready / tuning-check screen: its regions over a real chart, the stem and tone-approximation warnings, the per-string chips, the tuner states, its key hints and its meter polling.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    [Theory]
    [InlineData(1920, 1000)]
    [InlineData(1536, 800)]
    [InlineData(1440, 760)]
    [InlineData(1280, 720)]
    public async Task GetReady_AllSixStringsStayInsideTheirPanelAtSmallerSizes(double width, double height)
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);
            vm.ApplyToneSummary(new RsToneConversionSummary(true, true, 2, 6, 0, [])
            { ToneNames = ["clean", "drive"] });
            view.UpdateLayout();

            var panel = Find(view, "GetReadyTunerPanel");
            var rows = Find(view, "GetReadyTunerStrings").GetVisualDescendants()
                .OfType<Button>().Where(b => b.Classes.Contains("FireListRow")).ToList();
            Assert.Equal(6, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.True(row.Bounds.Height > 0);
                AssertContains(panel, row, "tuning string");
            });
            var input = Find(view, "GetReadyInputRow");
            AssertContains(panel, input, "input meter");
            var lastRowBottom = rows[^1].TranslatePoint(new Point(0, rows[^1].Bounds.Height), panel)!.Value.Y;
            Assert.True(lastRowBottom <= input.TranslatePoint(default, panel)!.Value.Y,
                "The input meter overlaps the high E string.");
            var tones = Find(view, "GetReadyTonesPanel");
            Assert.True(panel.TranslatePoint(new Point(0, panel.Bounds.Height), view)!.Value.Y
                <= tones.TranslatePoint(default, view)!.Value.Y, "The tuner overlaps the tone card.");
            AssertContains(view, Find(view, "GetReadyFooter"), "Start footer");
            var scroll = Find<ScrollViewer>(view, "GetReadyCentreScroll");
            AssertContains(view, scroll, "tuner scroll viewport");
            scroll.ScrollToEnd();
            view.UpdateLayout();
            AssertContains(scroll, tones, "tone card after scrolling");
            AssertContains(scroll, Find(view, "GetReadyBackingPanel"), "backing card after scrolling");
        }, seedProject: SeedThreeArrangementChart, width: width, height: height);
    }
    // ══ Get Ready (§6.6) ═════════════════════════════════════════════════════

    /// <summary>
    ///     The screen's four regions really compose, and the path list really
    ///     binds the loaded chart. This is the NO-STEMS, NO-APPROXIMATION state
    ///     — the one a freshly imported song is in.
    /// </summary>
    [Fact]
    public async Task GetReadyScreen_composesItsRegions_AndBindsTheRealChart()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            foreach (var name in new[]
                     {
                         "TuningCheckScreenRoot", "GetReadyHeader", "GetReadyLeftRail",
                         "GetReadyPathPanel", "GetReadyPathList", "GetReadyFeelPanel",
                         "GetReadyCentre", "GetReadyTunerPanel", "GetReadyTunerStrings",
                         "GetReadyInputRow", "GetReadyInputMeter", "GetReadyTonesPanel",
                         "GetReadyBackingPanel", "GetReadyFooter", "GetReadyStartButton",
                         "GetReadyKeyHints"
                     })
            {
                AssertLaidOut(view, name);
            }

            AssertLetsTheBackdropThrough(view, "TuningCheckScreenRoot");

            // The paths are the chart's, with the chart's own note counts.
            Assert.Equal(3, vm.FlowArrangements.Count);
            Assert.All(vm.FlowArrangements, a => Assert.True(a.NoteCount > 0,
                $"'{a.Name}' reported {a.NoteCount} notes — the count is not reaching the row"));

            var rows = FireRows(Find<ListBox>(view, "GetReadyPathList"));
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.True(r.Bounds.Width > 1 && r.Bounds.Height > 1,
                $"a path row laid out as {r.Bounds}"));

            // No stems yet ⇒ the amber chip, and no stem rows at all.
            Assert.False(vm.HasStems);
            AssertLaidOut(view, "GetReadyNoStemsChip");
            Assert.False(Find(view, "GetReadyStemList").IsVisible);

            // Nothing measured on this machine ⇒ NO estimate rather than a guess.
            Assert.False(vm.HasStemSeparationEstimate);
            Assert.False(Find(view, "GetReadyStemEstimate").IsVisible);

            // No approximations ⇒ the amber card is not drawn at all.
            Assert.False(vm.HasToneApproximations);
            Assert.False(Find(view, "GetReadyToneWarning").IsVisible);
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>
    ///     The same screen in its OTHER two states: stems present (so the
    ///     per-stem rows are real) and an approximation warning up. A layout
    ///     that only ever sees one branch never realizes the other's templates,
    ///     which is exactly where an unresolvable binding hides.
    /// </summary>
    [Fact]
    public async Task GetReadyScreen_keepsItsShape_WithStemsAndAnApproximationWarning()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            var audio = new FakeAudioPlaybackService();
            vm.BackingStems.Add(new StemTrackViewModel(audio, new Stem { Name = "guitar", Volume = 0.8 }, LocalizationService.Instance));
            vm.BackingStems.Add(new StemTrackViewModel(audio, new Stem { Name = "bass", Volume = 1.0, Muted = true }, LocalizationService.Instance));
            vm.BackingStems.Add(new StemTrackViewModel(audio, new Stem { Name = "drums", Volume = 1.0 }, LocalizationService.Instance));
            vm.BackingStems.Add(new StemTrackViewModel(audio, new Stem { Name = "vocals", Volume = 0.6 }, LocalizationService.Instance));
            vm.HasStems = true;

            // A REAL conversion summary, through the real projection.
            vm.ApplyToneSummary(new RsToneConversionSummary(
                    HasTones: true, DspAvailable: true, ToneCount: 2,
                    ApproximatedEffectCount: 6, SkippedEffectCount: 2,
                    Notes: ["clean: phaser folded onto the flanger", "drive: pitch shifter dropped"])
            { ToneNames = ["3doohere_ac", "3doohere_drive"] });
            vm.ReviewToneApproximationsCommand.Execute(null);
            vm.StemSeparationEstimateLabel = "~2 MIN";
            view.UpdateLayout();

            AssertLaidOut(view, "GetReadyStemList");
            AssertLaidOut(view, "GetReadyToneWarning");
            AssertLaidOut(view, "GetReadyToneChips");
            AssertLaidOut(view, "GetReadyToneApproxList");
            AssertLaidOut(view, "GetReadyStemEstimate");
            Assert.False(Find(view, "GetReadyNoStemsChip").IsVisible);

            // The chips and the review list are the summary's own content.
            Assert.Equal(2, vm.ToneChips.Count);
            Assert.Equal(2, vm.ToneApproximationNotes.Count);
            Assert.True(vm.HasToneApproximations);

            var stemRows = Find(view, "GetReadyStemList").GetVisualDescendants()
                .OfType<ProgressBar>().ToList();
            Assert.Equal(4, stemRows.Count);
            Assert.All(stemRows, b => Assert.True(b.Bounds.Width > 1,
                $"a stem level meter laid out as {b.Bounds}"));
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>
    ///     The tone chips are EDIT buttons now: each realizes as a laid-out button,
    ///     an edited tone wears its badge, the line under them lays out when they
    ///     can be edited, and they are disabled when they cannot.
    /// </summary>
    [Fact]
    public async Task GetReadyScreen_toneChipsAreEditButtons_WithTheEditedBadgeAndTheirNote()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            vm.ApplyToneSummary(new RsToneConversionSummary(
                    HasTones: true, DspAvailable: true, ToneCount: 2,
                    ApproximatedEffectCount: 0, SkippedEffectCount: 0, Notes: [])
            {
                ToneNames = ["clean", "drive"],
                EditedToneNames = ["drive"],
                CanSaveToneEdits = true
            });
            view.UpdateLayout();

            var buttons = Find(view, "GetReadyToneChips").GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(2, buttons.Count);
            Assert.All(buttons, b => Assert.True(b.Bounds.Width > 1 && b.Bounds.Height > 1,
                $"a tone chip laid out as {b.Bounds}"));

            var badges = Find(view, "GetReadyToneChips").GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("FireMono") && t.IsEffectivelyVisible).ToList();
            Assert.Single(badges); // only "drive" is edited

            // This host has no tone coordinator, so nothing can take the rig: the
            // chips stay disabled and say nothing.
            Assert.False(vm.CanEditSongTones);
            Assert.False(vm.EditSongToneCommand.CanExecute(vm.ToneChips[0]));
            Assert.False(Find(view, "GetReadyToneEditNote").IsVisible);

            // …and with an editor, the hint under the chips lays out.
            vm.CanEditSongTones = true;
            vm.SongToneEditNote = "Click a tone to edit it.";
            view.UpdateLayout();
            Assert.True(vm.EditSongToneCommand.CanExecute(vm.ToneChips[0]));
            AssertLaidOut(view, "GetReadyToneEditNote");
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>
    ///     THE quiet regression: every string's colour chip must be that
    ///     string's own fixed identity. A swapped pair still renders six
    ///     coloured bars and passes every bounds check — so this reads each
    ///     chip's REAL brush out of the laid-out tree and compares it with the
    ///     brush the token names in the app's own dictionary.
    /// </summary>
    [Fact]
    public async Task GetReadyScreen_eachStringChip_WearsThatStringsFixedIdentity()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            var chips = Find(view, "GetReadyTunerStrings").GetVisualDescendants()
                .OfType<Border>().Where(b => b.Classes.Contains("FireStringChip")).ToList();

            Assert.Equal(6, chips.Count);
            Assert.Equal(6, vm.GuidedTuning.Strings.Count);

            var app = Application.Current;
            Assert.NotNull(app);

            // The expectation is stated INDEPENDENTLY of the production mapping.
            // Deriving it from FireStringIdentity would move both sides together
            // when a pair is swapped, and this test would pass through the exact
            // regression it exists to catch.
            string[] identityInOrder =
                ["FireString6E", "FireString5A", "FireString4D", "FireString3G", "FireString2B", "FireString1e"];

            var seen = new List<Color>();
            for (var i = 0; i < chips.Count; i++)
            {
                var key = identityInOrder[i];
                Assert.True(app.TryFindResource(key, app.ActualThemeVariant, out var token),
                    $"{key} did not resolve out of the real dictionary");

                var expected = Assert.IsAssignableFrom<ISolidColorBrush>(token!).Color;
                var actual = Assert.IsAssignableFrom<ISolidColorBrush>(chips[i].Background).Color;
                Assert.True(expected == actual,
                    $"string {i} wears {actual} but its identity ({key}) is {expected}");

                Assert.True(chips[i].Bounds.Width > 0 && chips[i].Bounds.Height > 0,
                    $"string {i}'s chip laid out as {chips[i].Bounds}");
                seen.Add(actual);
            }

            // Six DISTINCT colours — a mapping that returned one key six times
            // would satisfy every check above.
            Assert.Equal(6, seen.Distinct().Count());
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>
    ///     The three tuner states are driven by the REAL check and look
    ///     different. Driven by pushing real frequencies into the real state
    ///     machine — nothing is set on the rows by hand — and asserted on what
    ///     the laid-out cells actually render.
    /// </summary>
    [Fact]
    public async Task GetReadyScreen_theTunerStates_AreRealAndVisuallyDistinct()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            var check = vm.GuidedTuning.Check;
            Assert.NotNull(check);
            Assert.Equal(6, vm.GuidedTuning.Strings.Count);

            // String 0 dead on; string 1 a quarter-tone flat; string 2 untouched.
            check!.SelectString(0);
            check.Process(check.Targets[0].FrequencyHz, 0);
            check.SelectString(1);
            check.Process(check.Targets[1].FrequencyHz * Math.Pow(2, -50.0 / 1200.0), 100);
            view.UpdateLayout();

            var rows = vm.GuidedTuning.Strings;
            Assert.True(rows[0].IsReadingGood, "the in-tune string is not reading good");
            Assert.True(rows[1].IsReadingBad, "the flat string is not reading bad");
            Assert.True(rows[2].IsAwaitingPluck, "the untouched string is not awaiting a pluck");

            // Three different sentences…
            var texts = new[] { rows[0].StateText, rows[1].StateText, rows[2].StateText };
            Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
            Assert.Equal(3, texts.Distinct(StringComparer.Ordinal).Count());

            // …and three different LOOKS on the laid-out cells.
            var cells = Find(view, "GetReadyTunerStrings").GetVisualDescendants()
                .OfType<TextBlock>().Where(t => t.Classes.Contains("FireValueCell")).ToList();
            Assert.Equal(6, cells.Count);
            var colours = cells.Take(3)
                .Select(c => Assert.IsAssignableFrom<ISolidColorBrush>(c.Foreground).Color).ToList();
            Assert.Equal(3, colours.Distinct().Count());

            // The meters agree with the cells — same pure function, so a needle
            // can never be green beside an amber verdict.
            var meters = Find(view, "GetReadyTunerStrings").GetVisualDescendants()
                .OfType<FireTunerMeter>().ToList();
            Assert.Equal(6, meters.Count);
            Assert.Equal(FireTunerBand.InTune, meters[0].Band);
            Assert.Equal(FireTunerBand.OutOfTune, meters[1].Band);
            Assert.Equal(FireTunerBand.NoReading, meters[2].Band);
            Assert.All(meters, m => Assert.True(m.Bounds.Width > 1 && m.Bounds.Height > 1,
                $"a tuner meter laid out as {m.Bounds}"));
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>ENTER really starts the run and T really asks for the tuner —
    /// the two hints the SCREEN claims, driven as real key presses through the
    /// view's own handler.</summary>
    [Fact]
    public async Task GetReadyScreen_theKeyHintsThatAreClaimedByTheScreen_ReallyFire()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            var tunerAsked = 0;
            vm.TunerRequested += () => tunerAsked++;

            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.T, Source = view
            });
            Assert.Equal(1, tunerAsked);

            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = view
            });
            // ConfirmTuning leaves the screen — that IS "ENTER START".
            Assert.NotEqual(PlayModeScreen.TuningCheck, vm.CurrentScreen);
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>
    ///     A tuner row can be PRESSED, and pressing it really re-selects that
    ///     string. `SkipCurrentCommand`'s own doc says a skipped string is
    ///     revisited "by tapping it", and until 0.58 the row template had no
    ///     click target at all — `SelectStringCommand` existed with no surface
    ///     raising it, so a skipped or confirmed string could only be reached
    ///     again by restarting the whole check.
    ///     <para>
    ///         Presses the real laid-out control through its own `Command`, and
    ///         asserts the SELECTION MOVED — a bound-but-broken binding leaves a
    ///         null command, which is exactly what a bounds check cannot see.
    ///         The geometry half matters just as much: the caption row and the
    ///         row template share one set of column widths, so this also pins
    ///         that wrapping the row in a button did not inset it.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task GetReadyScreen_aTunerRow_IsPressableAndReSelectsThatString()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            var rows = Find(view, "GetReadyTunerStrings").GetVisualDescendants()
                .OfType<Button>().Where(b => b.Classes.Contains("FireListRow")).ToList();
            Assert.Equal(6, rows.Count);

            // Every row carries a live command over its own row view model.
            Assert.All(rows, r =>
            {
                Assert.NotNull(r.Command);
                Assert.IsType<TuningStringRow>(r.CommandParameter);
            });

            // Skip forward, then press an EARLIER row and watch the selection
            // come back to it — the gesture the skip button's own text promises.
            var skipped = vm.GuidedTuning.Strings[vm.GuidedTuning.CurrentStringIndex];
            vm.GuidedTuning.SkipCurrentCommand.Execute(null);
            vm.GuidedTuning.SkipCurrentCommand.Execute(null);
            Assert.NotEqual(skipped.Index, vm.GuidedTuning.CurrentStringIndex);
            Assert.False(skipped.IsCurrent);

            var target = rows.Single(r => ReferenceEquals(r.CommandParameter, skipped));
            Assert.True(target.Command!.CanExecute(target.CommandParameter));
            target.Command.Execute(target.CommandParameter);
            view.UpdateLayout();

            Assert.Equal(skipped.Index, vm.GuidedTuning.CurrentStringIndex);
            Assert.True(skipped.IsCurrent,
                "pressing a tuner row must re-select that string — that is the whole gesture " +
                "`Skip check` tells the player about");

            // The button may not inset the row: the caption row's columns and the
            // row's columns are the one thing keeping the needles on one scale.
            var captions = Find(view, "GetReadyTunerCaptions");
            Assert.All(rows, r => Assert.True(
                Math.Abs(r.Bounds.Width - captions.Bounds.Width) < 1.0,
                $"a tuner row laid out {r.Bounds.Width:0.#} px wide against a " +
                $"{captions.Bounds.Width:0.#} px caption row"));
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>The live meter clock runs on this screen and provably STOPS
    /// when it is left — a hidden Play Mode surface may not keep polling the
    /// audio capture ring.</summary>
    [Fact]
    public async Task GetReadyScreen_pollsItsMetersOnlyWhileItIsUp()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);

            var before = vm.GetReadyMeterPolls;
            vm.PollGetReadyMeters();
            Assert.Equal(before + 1, vm.GetReadyMeterPolls);

            vm.CurrentScreen = PlayModeScreen.SongSelect;
            view.UpdateLayout();
            var off = vm.GetReadyMeterPolls;
            vm.PollGetReadyMeters();
            Assert.Equal(off, vm.GetReadyMeterPolls);
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>
    ///     A run Live Play REFUSED (no input device, no chart) lands back on this
    ///     screen, so this screen has to be able to say why. Before 0.61 it could
    ///     not: the refusal bounced to Song Select, whose browse bar was the only
    ///     surface in Play Mode that rendered <c>FlowError</c> — two screens from
    ///     the input chip and the signal probe that diagnose it, and from the run
    ///     setup the user had just made.
    ///     <para>
    ///         The strip is collapsed with no error, so the ordinary screen is
    ///         unchanged — and a strip that is present but measures zero is the
    ///         exact shape a failed style takes here, which is why the assertion
    ///         is on the BOUNDS and not on <c>IsVisible</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task GetReadyScreen_showsWhyARunWasRefused()
    {
        await WithView((view, vm) =>
        {
            EnterGetReady(vm, view);
            Assert.False(Find(view, "GetReadyFlowError").IsVisible);

            vm.FlowError = "No input device is open.";
            view.UpdateLayout();

            AssertLaidOut(view, "GetReadyFlowError");
            Assert.Equal("No input device is open.",
                Find<TextBlock>(view, "GetReadyFlowError").Text);
        }, seedProject: SeedThreeArrangementChart);
    }

    /// <summary>Puts the view model into the state a real song load leaves it
    /// in, then opens the screen and lays it out.</summary>
    private static void EnterGetReady(PlayModeViewModel vm, PlayModeView view)
    {
        vm.PreparePrePlay();
        vm.CurrentScreen = PlayModeScreen.TuningCheck;
        view.UpdateLayout();
    }

    /// <summary>A three-path chart with real notes, so the path rows have real
    /// counts and the tuner has real targets to derive.</summary>
    private static void SeedThreeArrangementChart(ProjectState state)
    {
        var project = new ChartProject();
        foreach (var (name, type, strings) in new[]
                 {
                     ("Lead", ArrangementType.Lead, 6),
                     ("Rhythm", ArrangementType.Rhythm, 6),
                     ("Bass", ArrangementType.Bass, 4)
                 })
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = type, StringCount = strings };
            var level = new ChartLevel { DifficultyIndex = 0 };
            for (var i = 0; i < 12; i++)
                level.Notes.Add(new GuitarNote { StringIndex = i % strings, Fret = i % 5 });
            arrangement.Levels.Add(level);
            project.Arrangements.Add(arrangement);
        }

        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
    }
}
