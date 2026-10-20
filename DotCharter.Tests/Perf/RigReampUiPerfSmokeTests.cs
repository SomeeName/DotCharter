using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Reamp;
using DotCharter.Tests.Audio.Reamp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using JuceSharp.AudioIo;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The DI · re-amp panel and the spillover switch (ABI v37), through the REAL
///     views: each constructs, attaches, lays out and MEASURES non-zero; the
///     panel's fixed cells do not move when recording or re-amping starts; the
///     rig header grows a DI button without growing; and the Tones mode carries
///     the spillover switch, enabled only when an engine can honour it. With
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set,
///     each state is written as a PNG for review.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigReampUiPerfSmokeTests
{
    private static RigReampViewModel NewReamp(string dir, FakeReampEngine? engine)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "DI 2026-09-27 14-03-12", "Verse riff", "Chorus (clean) take with a long name" })
            WavIo.WriteFloat32(Path.Combine(dir, name + ".wav"), new AudioBuffer(new float[480], 48000, 1));
        return new RigReampViewModel(engine, new DiTakeLibrary(dir), LocalizationService.Instance,
            loader: _ => (new float[48000 * 83], 48000),
            probe: _ => TimeSpan.FromSeconds(83))
        {
            ManualTick = true
        };
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static Rect Painted(Control c, Visual root)
        => c.TranslatePoint(new Point(0, 0), root) is { } p ? new Rect(p, c.Bounds.Size) : default;

    [Fact]
    public Task TheReampPanel_LaysOut_AndItsCellsHoldStillThroughEveryState() =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "dc-reamp-view-" + Guid.NewGuid().ToString("N"));
            var engine = new FakeReampEngine();
            using var vm = NewReamp(dir, engine);
            var panel = new RigReampPanel { DataContext = vm, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 460, Height = 720, Content = panel };
            window.Show();
            try
            {
                Settle(window);
                var popover = panel.FindControl<Control>("RigReampPopover")!;
                Assert.True(popover.Bounds.Width >= 370 && popover.Bounds.Height > 200, $"panel measured {popover.Bounds}");

                string[] watched = ["RigReampRecord", "RigReampRecordMeter", "RigReampRecordTime",
                                    "RigReampPlayPause", "RigReampStop", "RigReampPosition", "RigReampTime", "RigReampLoop"];
                Dictionary<string, Rect> Snapshot()
                {
                    Settle(window);
                    return watched.ToDictionary(n => n, n => Painted(panel.FindControl<Control>(n)!, panel));
                }
                foreach (var name in watched)
                {
                    var c = panel.FindControl<Control>(name)!;
                    Assert.True(c.Bounds.Width > 0 && c.Bounds.Height > 0, $"{name} measured {c.Bounds}");
                }
                // The readouts' fixed cells hold the widest value each can print:
                // a 20-minute take (DiTakeLibrary.MaxLoadSeconds).
                foreach (var (name, widest) in new[] { ("RigReampTime", "20:00 / 20:00"), ("RigReampRecordTime", "20:00") })
                {
                    var shown = panel.FindControl<TextBlock>(name)!;
                    var needed = new TextBlock
                    {
                        Text = widest, FontFamily = shown.FontFamily, FontSize = shown.FontSize,
                        FontWeight = shown.FontWeight, LetterSpacing = shown.LetterSpacing
                    };
                    needed.Measure(Size.Infinity);
                    // The CELL, not the text's own box: the value is right-aligned in it.
                    var cell = ((Grid)shown.Parent!).ColumnDefinitions[Grid.GetColumn(shown)].ActualWidth;
                    Assert.True(needed.DesiredSize.Width <= cell + 0.5,
                        $"'{widest}' needs {needed.DesiredSize.Width:F1} px and {name}'s cell is {cell:F1}");
                }
                var takes = panel.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("FirePopoverOption"));
                Assert.Equal(3, takes);
                var idle = Snapshot();
                RigCapture.Save(window, "reamp-panel-idle.png");

                // Re-amping: the take is named, the transport comes alive.
                await vm.ReampTakeCommand.ExecuteAsync(vm.Takes[1]);
                engine.Position = 37.5;
                vm.Tick();
                var reamping = Snapshot();
                Assert.True(panel.FindControl<Control>("RigReampTakeName")!.IsEffectivelyVisible);
                Assert.True(vm.Takes[1].IsLive, "the playing take is not lit in the list");
                Assert.Single(vm.Takes, t => t.IsLive);
                RigCapture.Save(window, "reamp-panel-reamping.png");
                vm.StopCommand.Execute(null);

                // Recording: the button's label swaps inside ONE fixed cell.
                vm.ToggleRecordCommand.Execute(null);
                engine.Capture(48000 * 3);
                vm.Tick();
                var recording = Snapshot();
                RigCapture.Save(window, "reamp-panel-recording.png");
                vm.ToggleRecordCommand.Execute(null);

                foreach (var (label, state) in new[] { ("re-amping", reamping), ("recording", recording) })
                {
                    foreach (var name in watched)
                    {
                        Assert.True(Math.Abs(idle[name].X - state[name].X) < 0.5 && Math.Abs(idle[name].Width - state[name].Width) < 0.5,
                            $"{name} moved or resized while {label}: {idle[name]} -> {state[name]}");
                    }
                }
            }
            finally
            {
                window.Close();
                BestEffortDelete.Directory(dir);
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task WithoutAnEngine_ThePanelPrintsWhy() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "dc-reamp-none-" + Guid.NewGuid().ToString("N"));
            using var vm = NewReamp(dir, engine: null);
            var panel = new RigReampPanel { DataContext = vm, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 460, Height = 720, Content = panel };
            window.Show();
            try
            {
                Settle(window);
                var reason = panel.FindControl<TextBlock>("RigReampUnavailable")!;
                Assert.True(reason.IsEffectivelyVisible && reason.Bounds.Height > 0, "the no-engine reason is not drawn");
                Assert.False(panel.FindControl<Button>("RigReampRecord")!.IsEffectivelyEnabled);
                RigCapture.Save(window, "reamp-panel-unavailable.png");
            }
            finally
            {
                window.Close();
                BestEffortDelete.Directory(dir);
            }
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task TheRigHeader_GrowsADiButton_AndTheTonesModeASpilloverSwitch(bool engineUp) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

            var dir = Path.Combine(Path.GetTempPath(), "dc-reamp-rig-" + Guid.NewGuid().ToString("N"));
            var chain = new FakeDspChainService(catalogue) { SupportsSpillover = engineUp };
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "amp"));
            rig.AddGear(catalogue.Single(m => m.Id == "delay"));
            using var reamp = NewReamp(dir, engineUp ? new FakeReampEngine() : null);
            var model = new AmpFxRigViewModel(rig) { Reamp = reamp };
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                Settle(window);
                var header = view.FindControl<Border>("RigHeader")!;
                var button = view.FindControl<Button>("RigReampButton")!;
                Assert.True(button.IsEffectivelyVisible && button.Bounds.Width > 30 && button.Bounds.Height > 20,
                    $"the DI button measured {button.Bounds}");
                Assert.Equal(64, header.Bounds.Height, 1);

                rig.ToggleBrowserCommand.Execute(null); // the Tones mode
                Settle(window);
                Assert.True(model.IsTonesMode);
                // In the Tones VIEW's head (its own name scope since 2026-09-27).
                var spill = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "RigSpilloverToggle");
                Assert.True(spill.IsEffectivelyVisible && spill.Bounds.Width > 40 && spill.Bounds.Height > 10,
                    $"the spillover switch measured {spill.Bounds}");
                Assert.Equal(engineUp, spill.IsEffectivelyEnabled);
                Assert.True(spill.IsChecked, "spillover is on by default");
                Assert.Equal(rig.SpilloverTip, ToolTip.GetTip(spill));
                RigCapture.Save(window, engineUp ? "tones-spillover-on.png" : "tones-spillover-no-engine.png");

                if (engineUp)
                {
                    spill.IsChecked = false;
                    Assert.False(rig.Spillover);
                    Assert.False(chain.Spillover);
                }
            }
            finally
            {
                window.Close();
                BestEffortDelete.Directory(dir);
            }
        }, TestContext.Current.CancellationToken);
}

/// <summary>Writes a window's rendered frame into DOTCHARTER_RIG_CAPTURE_DIR when
/// capture is on (DOTCHARTER_UI_SWEEP_DUMP=1). A no-op otherwise.</summary>
internal static class RigCapture
{
    public static void Save(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
