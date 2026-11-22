using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The timeline's right-click menu (#490), realized the way
///     <c>ChartRenderer.ShowContextMenu</c> does (<c>ChartRenderer.Realize</c>) and OPENED: the
///     popup measured non-zero, the Technique and Chord groups open onto their rows, and every row
///     of the group laid out with a size. The capture half is inert unless
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <see cref="MainMenuDropdownUiPerfSmokeTests.CaptureDirEnvVar" />
///     name a folder; then it writes <c>timeline-menu-note.png</c> / <c>timeline-menu-empty.png</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class TimelineContextMenuUiPerfSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public Task TheHandShapeMenuOpensWithBothHeldNoteCommandsMeasured() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var invoked = new List<string>();
            var model = TimelineContextMenuBuilder.ForHandShape(() => invoked.Add("grips"), () => invoked.Add("shapes"), null);
            var rows = model.Select(item => Assert.IsType<MenuItem>(ChartRenderer.Realize(item))).ToArray();
            Assert.Equal(new[] { "Grips from held notes", "Hand shapes from let ring" }, rows.Select(row => row.Header));
            var menu = new ContextMenu();
            foreach (var row in rows) menu.Items.Add(row);
            var host = new Border { Width = 40, Height = 20, ContextMenu = menu };
            var window = new Window { Width = Slot.Width, Height = Slot.Height, Content = host };
            window.Show();
            try
            {
                menu.Open(host);
                Settle(window);
                foreach (var row in rows)
                {
                    Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 });
                    row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                }
                Assert.Equal(new[] { "grips", "shapes" }, invoked);
            }
            finally
            {
                menu.Close();
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task AnUnavailableRow_IsRealizedDisabled_WithAReasonThatShowsOnHover() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var model = TimelineContextMenuBuilder.ForSound(Actions(1, 0) with { CanRun = id => id != "tools.refret" })
                .SingleOrDefault(i => i.LocKey == "Menu_Refret");
            Assert.NotNull(model);
            var row = Assert.IsType<MenuItem>(ChartRenderer.Realize(model));
            Assert.False(row.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(row));
            var reason = DotCharter.Localization.LocalizationService.Instance["Disabled_NoArrangement"];
            Assert.Equal(reason, ToolTip.GetTip(row));
            Assert.Equal(reason, Avalonia.Automation.AutomationProperties.GetHelpText(row));
        }, CancellationToken.None);

    private static readonly Size Slot = new(1040, 760);

    private static TimelineContextMenuActions Actions(int notes, int chords)
    {
        var k = new KeybindConfig();
        var chordOf = new Dictionary<string, KeyChord>
        {
            ["edit.cut"] = k.Cut, ["edit.copy"] = k.Copy, ["edit.duplicate"] = k.DuplicateSelection,
            ["edit.delete"] = k.Delete, ["note.toggleHopo"] = k.ToggleHopo,
            ["note.toggleHarmonic"] = k.ToggleHarmonic, ["note.toggleMute"] = k.ToggleMute,
            ["note.toggleLinkNext"] = k.ToggleLinkNext, ["note.toggleVibrato"] = k.ToggleVibrato,
            ["note.toggleTremolo"] = k.ToggleTremolo, ["note.togglePicking"] = k.TogglePicking,
            ["note.toggleAccent"] = k.ToggleAccent, ["note.toggleIgnore"] = k.ToggleIgnore,
            ["note.togglePassOtherNotes"] = k.TogglePassOtherNotes,
            ["tools.powerChordSmall"] = k.ConvertToPowerChordSmall,
            ["tools.powerChordBig"] = k.ConvertToPowerChordBig,
            ["tools.mutedStrumsToChord"] = k.MutedStrumsToChord, ["tools.toggleGhostGems"] = k.ToggleGhostGems,
            ["tools.tappedHarmonic"] = k.TappedHarmonic, ["tools.generateHandShape"] = k.GenerateHandShape,
            ["tools.generateArpeggio"] = k.GenerateArpeggio,
            ["edit.snapSelected"] = k.SnapSelected, ["edit.increaseLength"] = k.IncreaseLength,
            ["edit.decreaseLength"] = k.DecreaseLength, ["note.nudgeEarlier"] = k.NudgeEarlier,
            ["note.nudgeLater"] = k.NudgeLater, ["note.nudgeEarlier10Ms"] = k.NudgeEarlier10Ms,
            ["note.nudgeLater10Ms"] = k.NudgeLater10Ms, ["note.nudgeEarlier1Ms"] = k.NudgeEarlier1Ms,
            ["note.nudgeLater1Ms"] = k.NudgeLater1Ms,
        };
        return new TimelineContextMenuActions(
            () => { }, () => { }, () => { }, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
            _ => { }, id => chordOf.GetValueOrDefault(id), _ => true, notes, chords,
            _ => DotCharter.Localization.LocalizationService.Instance["Disabled_NoArrangement"]);
    }

    [Theory]
    [InlineData("note", true)]
    [InlineData("empty", false)]
    [InlineData("unavailable", true)]
    public Task TheMenu_OpensWithEveryRowMeasured(string name, bool onSound) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var actions = Actions(notes: 2, chords: 1);
            if (name == "unavailable") actions = actions with { CanRun = id => id != "tools.refret" };
            var items = onSound
                ? TimelineContextMenuBuilder.ForSound(actions)
                : TimelineContextMenuBuilder.ForEmptyHighway(Actions(0, 0));
            var menu = new ContextMenu();
            foreach (var item in items) menu.Items.Add(ChartRenderer.Realize(item));
            var host = new Border { Width = 40, Height = 20, ContextMenu = menu };
            var window = new Window
            {
                Width = Slot.Width, Height = Slot.Height, Content = host,
                Background = UiSweepRunner.ShellBackground(),
            };
            window.Show();
            try
            {
                Settle(window);
                menu.Open(host);
                Settle(window);
                Assert.True(menu.IsOpen, "the timeline menu did not open");

                var rows = menu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(items.Count(i => !i.IsSeparator), rows.Count);
                foreach (var row in rows)
                {
                    Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 },
                        $"the row '{row.Header}' laid out as {row.Bounds}");
                }

                output.WriteLine($"{name}: {rows.Count} rows");
                Capture(window, $"timeline-menu-{name}.png");

                if (name == "unavailable")
                {
                    var unavailable = rows.Single(r => r.Header?.ToString() == DotCharter.Localization.LocalizationService.Instance["Menu_Refret"]);
                    ToolTip.SetIsOpen(unavailable, true);
                    Settle(window);
                    Capture(window, "timeline-menu-unavailable-reason.png");
                    ToolTip.SetIsOpen(unavailable, false);
                    Settle(window);
                }

                var groupIndex = 0;
                foreach (var group in rows.Where(r => onSound && r.Items.OfType<MenuItem>().Any()))
                {
                    group.Open();
                    Settle(window);
                    var children = group.Items.OfType<MenuItem>().ToList();
                    Assert.True(children.Count >= 3, $"the group '{group.Header}' shows {children.Count} rows");
                    foreach (var child in children)
                    {
                        Assert.True(child.IsEffectivelyVisible && child.Bounds is { Width: > 1, Height: > 1 },
                            $"the row '{child.Header}' of '{group.Header}' laid out as {child.Bounds}");
                    }

                    Capture(window, $"timeline-menu-{name}-group-{groupIndex++}.png");
                    group.Close();
                    Settle(window);
                }
            }
            finally
            {
                menu.Close();
                window.Close();
            }
        }, CancellationToken.None);

    private static void Settle(Window window)
    {
        for (var round = 0; round < 3; round++)
        {
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable(MainMenuDropdownUiPerfSmokeTests.CaptureDirEnvVar);
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.True(frame is not null, "CaptureRenderedFrame returned null; set DOTCHARTER_UI_SWEEP_DUMP=1 before the session starts");
        frame!.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
    }
}
