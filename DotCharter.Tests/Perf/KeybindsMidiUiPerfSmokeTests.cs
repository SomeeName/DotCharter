using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Settings;
using DotCharter.UI.Views.Settings;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Settings ▸ Keybinds in MIDI mode (#310), built for real, attached to a
///     window and laid out: the mode switch, the MIDI pills, the clear buttons and
///     the device notice must all MEASURE — a style that failed still arranges, it
///     just has no size. Also logs what the first switch from the keyboard pills
///     to the MIDI pills costs (the pills are a hidden subtree until then).
/// </summary>
[Collection("HeadlessUI")]
public sealed class KeybindsMidiUiPerfSmokeTests(ITestOutputHelper output)
{
    private const double TabWidth = 960;
    private const double TabHeight = 1400;

    [Fact]
    public async Task MidiMode_WithADeviceListening_LaysOutEveryPill_AndTheLiveLine()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = new KeybindsSettingsView { DataContext = FakeKeybindsSettingsScreen.MidiLive() };
            WithLaidOutTab(tab, () =>
            {
                AssertMeasured(tab, "KeybindModeSwitch");
                AssertMeasured(tab, "MidiInputLive");
                AssertMeasured(tab, "ClearMidiBindings");
                Assert.False(Named(tab, "MidiInputWarning").IsEffectivelyVisible, "a live device shows no warning");

                var pills = Buttons(tab, "MidiPill");
                Assert.True(pills.Count >= 16, $"only {pills.Count} MIDI pills were realized");
                Assert.All(pills, p => Assert.True(p.Bounds.Width > 40 && p.Bounds.Height > 20,
                    $"a MIDI pill measured {p.Bounds}"));
                Assert.Contains(pills, p => p.Classes.Contains("Learning"));
                Assert.Contains(pills, p => !p.IsEnabled);

                var clears = Buttons(tab, "MidiClear").Where(c => c.IsEffectivelyVisible).ToList();
                Assert.NotEmpty(clears);
                Assert.All(clears, c => Assert.True(c.Bounds.Width > 10 && c.Bounds.Height > 10, $"a clear button measured {c.Bounds}"));

                Assert.DoesNotContain(tab.GetVisualDescendants().OfType<DotCharter.UI.Controls.KeybindCaptureBox>(),
                    b => b.IsEffectivelyVisible);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MidiMode_WithNoDevice_ShowsTheWarning_AndKeepsTheRowsEditable()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = new KeybindsSettingsView { DataContext = FakeKeybindsSettingsScreen.MidiNoDevice() };
            WithLaidOutTab(tab, () =>
            {
                var warning = AssertMeasured(tab, "MidiInputWarning");
                Assert.True(warning.Bounds.Width > 200, $"the warning measured {warning.Bounds}");
                Assert.False(Named(tab, "MidiInputLive").IsEffectivelyVisible);
                Assert.Contains(Buttons(tab, "MidiPill"), p => p.IsEnabled);
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SwitchingFromKeyboardToMidi_RealizesThePills()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeKeybindsSettingsScreen.Full();
            var tab = new KeybindsSettingsView { DataContext = screen };
            WithLaidOutTab(tab, () =>
            {
                Assert.False(Named(tab, "MidiInputState").IsEffectivelyVisible);
                Assert.DoesNotContain(Buttons(tab, "MidiPill"), p => p.IsEffectivelyVisible);
                // Cold: a hidden pill has never been templated — the switch pays for it.
                Assert.All(Buttons(tab, "MidiPill"), p => Assert.Empty(p.GetVisualChildren()));

                var first = Switch(tab, screen, true);
                var back = Switch(tab, screen, false);
                var again = Switch(tab, screen, true);
                output.WriteLine($"Keyboard/MIDI switch over 140 rows: first {first:F1} ms, back {back:F1} ms, again {again:F1} ms");

                var pills = Buttons(tab, "MidiPill").Where(p => p.IsEffectivelyVisible).ToList();
                Assert.True(pills.Count >= 100, $"only {pills.Count} MIDI pills showed after the switch");
                Assert.All(pills, p => Assert.True(p.Bounds.Height > 20, $"a MIDI pill measured {p.Bounds}"));
                AssertMeasured(tab, "MidiInputWarning"); // Full's fake has no device listening
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The view offers the pills of the mode not on screen to the startup
    ///     prewarm (<see cref="DotCharter.UI.Views.Shell.IPrewarmableSurfaces" />,
    ///     listed by <c>MainWindowPrewarm.PrewarmableSurfaceOwners</c>); realized
    ///     that way, every MIDI pill is templated BEFORE the first switch.
    /// </summary>
    [Fact]
    public async Task ThePrewarm_TemplatesTheHiddenMidiPills_BeforeTheFirstSwitch()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeKeybindsSettingsScreen.Full();
            var tab = new KeybindsSettingsView { DataContext = screen };
            WithLaidOutTab(tab, () =>
            {
                var hidden = tab.HiddenSurfaces();
                Assert.Equal(140, hidden.Count(c => c.Classes.Contains("MidiCell")));
                Assert.Contains(hidden, c => c.Name == "MidiInputState");
                Assert.All(hidden, c => Assert.False(c.IsVisible, $"{c} is on screen and was listed as hidden"));

                foreach (var part in hidden)
                    Assert.True(DotCharter.UI.Views.Shell.HiddenSubtreePrewarmer.Realize(part, new Size(TabWidth, TabHeight)));
                Assert.All(Buttons(tab, "MidiPill"), p => Assert.NotEmpty(p.GetVisualChildren()));

                var first = Switch(tab, screen, true);
                output.WriteLine($"Keyboard/MIDI switch over 140 rows after the prewarm: first {first:F1} ms");
                Assert.True(tab.HiddenSurfaces().Count(c => c is DotCharter.UI.Controls.KeybindCaptureBox) >= 140,
                    "in MIDI mode the chord pills are the hidden half, and are offered instead");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The real view model over the real resx (in the session's culture), with
    ///     binds seeded and no device / a device listening — laid out, and when the
    ///     session draws (<c>DOTCHARTER_UI_SWEEP_DUMP=1</c> +
    ///     <c>DOTCHARTER_RIG_CAPTURE_DIR</c>) written as PNGs on the settings ground
    ///     for the review.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRealTab_InMidiMode_LaysOut_AndCaptures(bool live)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var cfg = new FakeConfigurationService();
            cfg.Keybinds.SetMidiTrigger(nameof(KeybindConfig.PlayPause), MidiTrigger.Note(9, 36));
            cfg.Keybinds.SetMidiTrigger(nameof(KeybindConfig.ToggleMetronome), MidiTrigger.ControlChange(0, 20));
            cfg.Keybinds.SetMidiTrigger(nameof(KeybindConfig.SetLoopStart), MidiTrigger.Note(9, 37));
            cfg.Keybinds.SetMidiTrigger(nameof(KeybindConfig.SeekLeft), MidiTrigger.ControlChange(15, 116));
            cfg.Keybinds.SetMidiTrigger(nameof(KeybindConfig.SeekRight), MidiTrigger.ControlChange(15, 117));
            cfg.Keybinds.SetMidiTrigger(nameof(KeybindConfig.Undo), MidiTrigger.Note(0, 127));
            var midi = new FakeMidiTriggerInput();
            if (live) midi.Connect("Akai Professional MPK mini 3 (USB MIDI)");
            using var vm = new DotCharter.Presentation.Screens.Settings.KeybindsSettingsViewModel(cfg,
                new ProjectState { Keybinds = cfg.Keybinds }, new NullDispatch(),
                DotCharter.Localization.LocalizationService.Instance, midi);
            vm.IsMidiMode = true;
            var learning = vm.Groups.SelectMany(g => g.Rows)
                .Cast<DotCharter.Presentation.Screens.Settings.KeybindRowViewModel>()
                .Single(r => r.Property == nameof(KeybindConfig.ToggleLoop));
            learning.LearnMidiCommand.Execute(null);

            var tab = new KeybindsSettingsView { DataContext = vm };
            var ground = new Border { Child = tab };
            ground.Bind(Border.BackgroundProperty, ground.GetResourceObservable("FireGroundScreen"));
            var window = new Window { Width = 1600, Height = 2600, Content = ground };
            window.Show();
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs(Avalonia.Threading.DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                AssertMeasured(tab, live ? "MidiInputLive" : "MidiInputWarning");
                var pills = Buttons(tab, "MidiPill").Where(p => p.IsEffectivelyVisible).ToList();
                Assert.True(pills.Count >= 16, $"only {pills.Count} MIDI pills on screen");
                Assert.All(pills, p => Assert.True(p.Bounds.Height > 20, $"a MIDI pill measured {p.Bounds}"));

                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
                Directory.CreateDirectory(dir);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
                frame.Save(Path.Combine(dir, $"keybinds-midi-{(live ? "live" : "no-device")}-{culture}.png"),
                    new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private sealed class NullDispatch : DotCharter.Services.Infrastructure.Input.IKeybindDispatch
    {
        public void RefreshBinds() { }

        public string? DescribeBinding(KeyChord chord) => null;
    }

    private static double Switch(Control tab, FakeKeybindsSettingsScreen screen, bool midi)
    {
        var clock = Stopwatch.StartNew();
        screen.IsMidiMode = midi;
        tab.UpdateLayout();
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        return clock.Elapsed.TotalMilliseconds;
    }

    private static List<Button> Buttons(Control tab, string cls) =>
        [.. tab.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains(cls))];

    private static Control Named(Control tab, string name) =>
        tab.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no control named {name}");

    private static Control AssertMeasured(Control tab, string name)
    {
        var control = Named(tab, name);
        Assert.True(control.IsEffectivelyVisible, $"{name} is hidden");
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1, $"{name} measured {control.Bounds}");
        return control;
    }

    private static void WithLaidOutTab(Control tab, Action assert)
    {
        var window = new Window { Width = TabWidth, Height = 900, Content = tab };
        window.Show();
        try
        {
            tab.Measure(new Size(TabWidth, TabHeight));
            tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
