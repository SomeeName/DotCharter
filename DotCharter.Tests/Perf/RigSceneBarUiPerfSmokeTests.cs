using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The rig's SCENE BAR, SETLIST PANEL and MIDI card through the REAL view:
///     each lays out (measured non-zero — a view whose styles failed still
///     arranges, it just has no size), nothing in the bar moves or leaves its
///     line when a scene lights, a name arrives, a setlist goes live or a MIDI
///     message prints, the keys pick scenes and step the setlist (and never while
///     a field is typing), and a scene is renamed in place at its button's size.
///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c>
///     set, each state is written as a PNG for review. The rules behind them are
///     <c>UI/RigScenesTests</c> and <c>UI/RigSetlistsTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigSceneBarUiPerfSmokeTests
{
    private static readonly DspModuleType Drive = new("overdrive", "Overdrive", [new("drive", "Drive", 0, 1, 0.5f)]);
    private static readonly DspModuleType Delay = new("delay", "Delay", [new("mix", "Mix", 0, 1, 0.3f)]);

    private sealed class Clock
    {
        public long Now = 1_000_000;
    }

    private sealed record Open(PedalboardViewModel Rig, DspTonePresetService Presets, AmpFxRigView View, Window Window, Clock Clock);

    private static Open OpenRig(double width = 1920, double height = 1080)
    {
        var chain = new FakeDspChainService(new[] { Drive, Delay });
        var config = new FakeConfigurationService();
        var presets = new DspTonePresetService(chain, config);
        var rig = TestRigBuilders.Pedalboard(chain, presets, LocalizationService.Instance, config);
        var clock = new Clock();
        rig.HistoryClock = () => clock.Now;
        rig.AddGear(Drive);
        rig.AddGear(Delay);
        clock.Now += 10_000;
        rig.SelectNodeAt(-1);
        var view = new AmpFxRigView { DataContext = new AmpFxRigViewModel(rig) };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Settle(window);
        return new Open(rig, presets, view, window, clock);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static DspTonePreset Tone(DspTonePresetService presets, string name, float drive) =>
        presets.SaveUser(name, DspToneCategory.Clean, string.Empty, new DcRigPreset(
        [
            new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = drive }),
            new DcRigModule("delay", new Dictionary<string, float>())
        ]));

    private static RigSceneBar BarOf(Control view) => view.GetVisualDescendants().OfType<RigSceneBar>().Single();

    private static List<ToggleButton> SlotsOf(Control bar) =>
        bar.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("SceneSlot")).ToList();

    private static Rect RectIn(Visual v, Visual root) =>
        new(v.TranslatePoint(new Point(0, 0), root)!.Value, v.Bounds.Size);

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1600, 900)]
    public Task TheSceneBar_LaysOut_AndNothingInItMovesWhenItsStateChanges(double width, double height) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var open = OpenRig(width, height);
            using var _ = open.Rig;
            try
            {
                var bar = BarOf(open.View);
                var root = bar.FindControl<Border>("RigSceneBarRoot")!;
                Assert.True(bar.IsEffectivelyVisible);
                Assert.True(root.Bounds.Width > 600 && root.Bounds.Height >= 40, $"the scene bar measured {root.Bounds}");

                var slots = SlotsOf(bar);
                Assert.Equal(8, slots.Count);
                Assert.All(slots, s => Assert.True(s.Bounds.Width > 60 && s.Bounds.Height > 30, $"a scene button measured {s.Bounds}"));
                Assert.Single(slots.Select(s => Math.Round(s.Bounds.Width, 1)).Distinct()); // one width for all eight

                var watched = slots.Cast<Control>()
                    .Concat(new[] { "RigSetlistPrevious", "RigSetlistNext", "RigMidiMonitorText", "RigSetlistNow" }
                        .Select(n => bar.FindControl<Control>(n)!))
                    .ToList();
                Assert.All(watched, c => Assert.True(c.Bounds.Width > 0 && c.Bounds.Height > 0, $"{c.Name ?? c.GetType().Name} measured {c.Bounds}"));
                var before = watched.Select(c => RectIn(c, root)).ToList();

                // everything on the bar's one line, centred on it
                var middle = root.Bounds.Height / 2;
                foreach (var (c, r) in watched.Zip(before))
                {
                    Assert.True(Math.Abs(r.Center.Y - middle) <= 1.0,
                        $"{c.Name ?? c.GetType().Name} is centred at y={r.Center.Y:F1}, the bar at y={middle:F1}");
                }

                Capture(open.Window, $"scenes-bar-idle-{width}.png");

                // the busy state: a setlist live mid-gig, then (on that song's tone,
                // which brings no scenes of its own) a scene lit and named, and a MIDI message
                open.Rig.Setlists.NewSetlistCommand.Execute(null);
                open.Rig.Setlists.SelectedSetlist!.Name = "Friday at the Crown & Anchor";
                foreach (var name in new[] { "Clean verse", "A very long tone name for the chorus" })
                {
                    open.Rig.Browser.ApplyPreset(Tone(open.Presets, name, 0.3f));
                    open.Rig.Setlists.AddCurrentToneCommand.Execute(null);
                }
                open.Rig.Setlists.ToggleActiveCommand.Execute(null);
                open.Rig.Setlists.NextCommand.Execute(null);
                open.Rig.Setlists.NextCommand.Execute(null);
                open.Rig.SelectSceneAt(2);
                open.Rig.RenameScene(2, "Big chorus lead");
                open.Rig.RenameScene(0, "Verse");
                open.Rig.SelectSceneAt(4);
                open.Rig.MidiControls.LastMessage = "CC 127 = 127 · ch 16";
                Settle(open.Window);

                Assert.Equal("CC 127 = 127 · ch 16", open.Rig.MidiMonitorText);
                Assert.True(slots[4].IsChecked);
                var after = watched.Select(c => RectIn(c, root)).ToList();
                foreach (var (c, (was, now)) in watched.Zip(before.Zip(after)))
                {
                    Assert.True(Math.Abs(was.X - now.X) < 0.5 && Math.Abs(was.Width - now.Width) < 0.5,
                        $"{c.Name ?? c.GetType().Name} moved from x={was.X:F1} w={was.Width:F1} to x={now.X:F1} w={now.Width:F1}");
                    Assert.True(Math.Abs(was.Center.Y - now.Center.Y) < 0.5 && Math.Abs(was.Height - now.Height) < 0.5,
                        $"{c.Name ?? c.GetType().Name} left its line (h {was.Height:F1} -> {now.Height:F1}) — a long name wrapped?");
                }

                // the widest message the monitor can print fits its fixed cell, untrimmed
                var monitor = bar.FindControl<TextBlock>("RigMidiMonitorText")!;
                var needed = new TextBlock { Text = "CC 127 = 127 · ch 16", FontFamily = monitor.FontFamily, FontSize = monitor.FontSize, FontWeight = monitor.FontWeight };
                needed.Measure(Size.Infinity);
                Assert.True(needed.DesiredSize.Width <= monitor.Bounds.Width + 0.5,
                    $"the MIDI monitor is {monitor.Bounds.Width:F1} px and its widest message needs {needed.DesiredSize.Width:F1}");

                // nothing runs past the bar or into its neighbour (the 1600 px case
                // overflowed until the setlist line became one trimming text)
                var cells = new[] { "RigSceneSlots", "RigSetlistPrevious", "RigSetlistNow", "RigSetlistNext", "RigMidiMonitor" }
                    .Select(n => (Name: n, Rect: RectIn(bar.FindControl<Control>(n)!, root))).ToList();
                foreach (var (left, right) in cells.Zip(cells.Skip(1)))
                {
                    Assert.True(left.Rect.Right <= right.Rect.X + 0.5,
                        $"{left.Name} (right {left.Rect.Right:F1}) runs into {right.Name} (x {right.Rect.X:F1})");
                }

                Assert.True(cells[^1].Rect.Right <= root.Bounds.Width + 0.5,
                    $"the MIDI monitor ends at {cells[^1].Rect.Right:F1}, past the bar's {root.Bounds.Width:F1}");

                // the lit scene's letter is lit, not ink on the accent tint
                Assert.True(open.View.TryFindResource("FireAccentBright", open.View.ActualThemeVariant, out var accent));
                var letter = slots[4].GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("SceneLetter"));
                Assert.Equal(((Avalonia.Media.ISolidColorBrush)accent!).Color, ((Avalonia.Media.ISolidColorBrush)letter.Foreground!).Color);

                Capture(open.Window, $"scenes-bar-busy-{width}.png");
            }
            finally
            {
                open.Window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheKeys_PickScenes_AndStepTheSetlist_ButNeverWhileAFieldIsTyping() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var open = OpenRig();
            using var _ = open.Rig;
            try
            {
                var card = open.View.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("FireUnitCard") && b.IsEffectivelyVisible && b.DataContext is PedalNodeViewModel);
                var at = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 3), open.Window)!.Value;
                open.Window.MouseDown(at, MouseButton.Left);
                open.Window.MouseUp(at, MouseButton.Left);
                Settle(open.Window);
                Assert.True(open.View.IsKeyboardFocusWithin, "a click on a unit did not put the keys on the rig");

                Press(open, PhysicalKey.Digit3);
                Assert.Equal(2, open.Rig.ActiveSceneIndex);
                Press(open, PhysicalKey.NumPad1);
                Assert.Equal(0, open.Rig.ActiveSceneIndex);

                var first = Tone(open.Presets, "First", 0.1f);
                var second = Tone(open.Presets, "Second", 0.9f);
                open.Rig.Setlists.NewSetlistCommand.Execute(null);
                foreach (var tone in new[] { first, second })
                {
                    open.Rig.Browser.ApplyPreset(tone);
                    open.Rig.Setlists.AddCurrentToneCommand.Execute(null);
                }
                open.Rig.Setlists.ToggleActiveCommand.Execute(null);
                Settle(open.Window);
                open.View.Focus();

                Press(open, PhysicalKey.PageDown);
                Assert.Equal(first.Id, open.Rig.Browser.ActivePreset!.Id);
                Press(open, PhysicalKey.PageDown);
                Assert.Equal(second.Id, open.Rig.Browser.ActivePreset!.Id);
                Press(open, PhysicalKey.PageUp);
                Assert.Equal(first.Id, open.Rig.Browser.ActivePreset!.Id);

                // typing "5" into the gear search is a search, not a scene
                var search = open.View.FindControl<TextBox>("RigGearSearch")!;
                search.Focus();
                Settle(open.Window);
                Press(open, PhysicalKey.Digit5);
                Assert.Equal(0, open.Rig.ActiveSceneIndex);
            }
            finally
            {
                open.Window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task AScene_IsRenamedInPlace_AtItsButtonsSize_AndItsMenuHoldsTheVerbs() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var open = OpenRig();
            using var _ = open.Rig;
            try
            {
                var bar = BarOf(open.View);
                var slot = open.Rig.SceneSlots[3];
                var button = SlotsOf(bar)[3];

                var menu = button.ContextMenu!;
                menu.Open(button);
                Settle(open.Window);
                var items = menu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(3, items.Count);
                Assert.All(items, i => Assert.NotNull(i.Command));
                Assert.Same(slot.BeginRenameCommand, items[0].Command);
                menu.Close();

                slot.BeginRenameCommand.Execute(null);
                Settle(open.Window);
                var field = bar.GetVisualDescendants().OfType<TextBox>()
                    .Single(t => t.Name == "SceneName" && ReferenceEquals(t.DataContext, slot));
                var frame = field.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("FireField"));
                Assert.True(field.IsEffectivelyVisible && field.IsFocused, "the name field did not take the keys");
                var root = bar.FindControl<Border>("RigSceneBarRoot")!;
                var buttonRect = RectIn(button, root);
                var frameRect = RectIn(frame, root);
                Assert.True(Math.Abs(buttonRect.X - frameRect.X) < 0.5 && Math.Abs(buttonRect.Width - frameRect.Width) < 0.5
                            && Math.Abs(buttonRect.Center.Y - frameRect.Center.Y) < 0.5,
                    $"the name field {frameRect} does not sit over its button {buttonRect}");
                Capture(open.Window, "scenes-bar-renaming.png");

                slot.EditName = "Outro";
                open.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                open.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Settle(open.Window);
                Assert.False(slot.IsRenaming);
                Assert.Equal("Outro", slot.Name);
                Assert.Equal("Outro", open.Rig.BuildCurrentRig().Scenes!.Slots[3].Name);
            }
            finally
            {
                open.Window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheSetlistPanel_TakesTheCardColumn_AndLaysOut() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var open = OpenRig();
            using var _ = open.Rig;
            try
            {
                var browser = open.Rig.Browser;
                open.Rig.Setlists.NewSetlistCommand.Execute(null);
                open.Rig.Setlists.SelectedSetlist!.Name = "Friday";
                foreach (var name in new[] { "Clean verse", "Crunch chorus", "Lead" })
                {
                    browser.ApplyPreset(Tone(open.Presets, name, 0.3f));
                    open.Rig.Setlists.AddCurrentToneCommand.Execute(null);
                }
                open.Rig.Setlists.NewSetlistCommand.Execute(null);
                open.Rig.Setlists.SelectedSetlist = open.Rig.Setlists.Setlists[0];
                open.Rig.Setlists.ToggleActiveCommand.Execute(null);
                open.Rig.Setlists.NextCommand.Execute(null);
                open.Rig.Setlists.SelectedSetlist.Entries[1].SelectedScene = open.Rig.Setlists.SceneChoices[2];

                open.Rig.ToggleBrowserCommand.Execute(null);            // the Tones mode
                browser.SelectedRail = browser.Rail.Single(r => r.Kind == PresetFilterKind.Setlists);
                Settle(open.Window);

                Assert.True(browser.IsSetlistsSelected);
                Assert.False(browser.ShowCards || browser.ShowNoCards);
                // In the Tones VIEW (its own name scope since 2026-09-27).
                var panel = open.View.GetVisualDescendants().OfType<RigSetlistPanel>().Single(p => p.Name == "RigSetlists");
                Assert.True(panel.IsEffectivelyVisible && panel.Bounds.Width > 400 && panel.Bounds.Height > 100,
                    $"the setlist panel measured {panel.Bounds}");
                Assert.False(open.View.GetVisualDescendants().OfType<Control>()
                    .Single(c => c.Name == "RigBrowserCardHost").IsEffectivelyVisible);
                // the head's tone count is not a "Tones 0" over the setlists
                var count = open.View.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "RigBrowserCount");
                Assert.False(count.IsEffectivelyVisible, "the Tones count shows over the setlist panel");
                foreach (var name in new[] { "RigSetlistList", "RigSetlistName", "RigSetlistToggleLive", "RigSetlistAdd", "RigSetlistSongs" })
                {
                    var c = panel.FindControl<Control>(name)!;
                    Assert.True(c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0, $"{name} measured {c.Bounds}");
                }

                var rows = panel.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("SetlistSong")).ToList();
                Assert.Equal(3, rows.Count);
                Assert.All(rows, r => Assert.True(r.Bounds.Height > 20, $"a song row measured {r.Bounds}"));
                Assert.True(rows[0].Classes.Contains("current"), "the song playing is not marked");
                var combos = panel.GetVisualDescendants().OfType<ComboBox>().ToList();
                Assert.Equal(3, combos.Count);
                Assert.All(combos, c => Assert.True(c.Bounds.Width > 100, $"a scene picker measured {c.Bounds}"));
                Capture(open.Window, "setlist-panel.png");

                // back on the library, the count is there and counts
                browser.SelectedRail = browser.Rail.Single(r => r.Kind == PresetFilterKind.All);
                Settle(open.Window);
                Assert.True(count.IsEffectivelyVisible, "the Tones count did not come back with the library");
                Assert.NotEqual("0", count.GetVisualDescendants().OfType<TextBlock>().Single().Text);

                browser.SelectedRail = browser.Rail[0];                 // back to the cards
                Settle(open.Window);
                Assert.False(panel.IsEffectivelyVisible);
            }
            finally
            {
                open.Window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheMidiCard_LaysOutItsPerformanceRows() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            // in the rig, where the player finds it: a unit's DETAILS, Controllers & MIDI
            var open = OpenRig();
            using var _ = open.Rig;
            var window = open.Window;
            var model = open.Rig.MidiControls;
            try
            {
                open.Rig.SelectNodeAt(0);
                ((AmpFxRigViewModel)open.View.DataContext!).IsDetailsOpen = true;
                Settle(window);
                var expander = open.View.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "RigEditorControllers" && e.IsEffectivelyVisible);
                expander.IsExpanded = true;
                Settle(window);
                var card = expander.GetVisualDescendants().OfType<RigMidiControlsView>().Single();
                card.FindControl<Control>("RigMidiPerformance")!.BringIntoView();
                Settle(window);
                foreach (var name in new[] { "RigMidiPerformance", "RigMidiProgramChange", "RigMidiSceneCc", "RigMidiChannel", "RigMidiLastMessage" })
                {
                    var c = card.FindControl<Control>(name)!;
                    Assert.True(c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0, $"{name} measured {c.Bounds}");
                }

                Assert.True(card.FindControl<Control>("RigMidiPerformance")!.Bounds.Right <= card.Bounds.Width + 0.5,
                    "the performance rows run past the card");
                var cc = card.FindControl<NumericUpDown>("RigMidiSceneCc")!;
                var text = cc.GetVisualDescendants().OfType<TextBox>().Single();
                var needed = new TextBlock { Text = "127", FontFamily = text.FontFamily, FontSize = text.FontSize };
                needed.Measure(Size.Infinity);
                Assert.True(needed.DesiredSize.Width <= text.Bounds.Width, $"the scene CC box shows {text.Bounds.Width:F1} px, '127' needs {needed.DesiredSize.Width:F1}");
                model.LastMessage = "PC 12 · ch 1";
                Settle(window);
                Capture(window, "midi-card.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    private static void Press(Open open, PhysicalKey key)
    {
        open.Clock.Now += 10_000;
        open.Window.KeyPressQwerty(key, RawInputModifiers.None);
        open.Window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Settle(open.Window);
    }
}
