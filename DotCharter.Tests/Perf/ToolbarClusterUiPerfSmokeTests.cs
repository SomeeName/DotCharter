using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Views.Shell.Popovers;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #492: the reshaped chart editor toolbar, built against the app's OWN shell view model.
///     Play and Pause are one button that shows the right state while playing and paused; the
///     tone-preview split button's caret opens the target rows and a pick closes it; the Export
///     button's popup holds the File ▸ Export rows and a pick closes it. Every part measures
///     non-zero at 1280, 1600 and 1920 px.
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c>, <c>DOTCHARTER_TOOLBAR_CAPTURE_DIR</c> and
///         <c>DOTCHARTER_TOOLBAR_CAPTURE_TAG</c> set it writes the toolbar at the three widths
///         (<c>toolbar-1280-&lt;tag&gt;.png</c>) and, for the "after" tag, the two open popups.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToolbarClusterUiPerfSmokeTests
{
    private static readonly double[] Widths = [1280, 1600, 1920];

    private static MainWindowViewModel Shell()
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot");
        var shell = services.GetRequiredService<MainWindowViewModel>();
        if (shell.MultiTrackOverlay.AllEntries.Count == 0) UiSweepDataContexts.Build(services);
        return shell;
    }

    private static T Named<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    [Fact]
    public Task PlayPause_isOneButton_andShowsTheRightStateWhilePausedAndPlaying() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var transport = shell.Transport;
            var wasPlaying = transport.IsPlaying;
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 1600, Height = 80, Content = view };
            window.Show();
            try
            {
                transport.IsPlaying = false;
                Dispatch();
                var button = Named<Button>(view, "PlayPauseButton");
                Assert.True(button.Bounds is { Width: > 1, Height: > 1 }, $"the play/pause button laid out as {button.Bounds}");
                var icons = button.GetVisualDescendants().OfType<PathIcon>().ToList();
                var app = Application.Current!;
                app.TryFindResource("IconPlay", out var play);
                app.TryFindResource("IconPause", out var pause);
                var shown = () => icons.Where(i => i.IsVisible).Select(i => i.Data).Single();

                Assert.Same(play, shown());
                Assert.Equal(LocalizationService.Instance["Toolbar_Play"], AutomationProperties.GetName(button));
                var chord = shell.ProjectState.Keybinds.PlayPause;
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Toolbar_Play_Hint"], chord),
                    ToolTip.GetTip(button));

                transport.IsPlaying = true;
                Dispatch();
                Assert.Same(pause, shown());
                Assert.Equal(LocalizationService.Instance["Toolbar_Pause"], AutomationProperties.GetName(button));
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Toolbar_Pause_Hint"], chord),
                    ToolTip.GetTip(button));

                // The Play/Pause and Stop glyphs are no longer both on the rail at once.
                Assert.Single(view.GetVisualDescendants().OfType<PathIcon>(), i => i.IsVisible && ReferenceEquals(i.Data, pause));
            }
            finally
            {
                transport.IsPlaying = wasPlaying;
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task TheSpeedChip_showsTheCurrentSpeed() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 1920, Height = 80, Content = view };
            window.Show();
            var was = shell.Transport.PlaybackSpeed;
            try
            {
                shell.Transport.PlaybackSpeed = 0.75;
                Dispatch();
                var label = Named<TextBlock>(view, "TempoLabel");
                Assert.Equal(0.75.ToString("P0", CultureInfo.CurrentCulture), label.Text);
            }
            finally
            {
                shell.Transport.PlaybackSpeed = was;
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task TheTonePreviewCaret_opensTheTargetRows_andAPickClosesIt() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 1600, Height = 400, Content = view };
            window.Show();
            try
            {
                var main = Named<ToggleButton>(view, "TonePreviewButton");
                var caret = Named<Button>(view, "TonePreviewTargetButton");
                Assert.True(main.Bounds is { Width: > 1 } && caret.Bounds is { Width: > 1 }, $"{main.Bounds} / {caret.Bounds}");
                // One control: the caret sits right against the main half.
                Assert.True(caret.Bounds.X - (main.Bounds.X + main.Bounds.Width) < 4, "the caret is not joined to the toggle");
                // #593: greyed out, the toggle still shows WHY on hover.
                Assert.True(ToolTip.GetShowOnDisabled(main), "the disabled tone preview hides its reason");
                Assert.Equal(shell.ToneRig.TonePreviewTip, ToolTip.GetTip(main));

                var flyout = Assert.IsType<PrewarmableFlyout>(caret.Flyout);
                flyout.ShowAt(caret);
                Dispatch();
                Assert.True(flyout.IsOpen);
                var content = Assert.IsType<TonePreviewTargetPopoverView>(flyout.Content);
                var rows = content.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Equal(2, rows.Count);
                Assert.All(rows, r => Assert.True(r.Bounds is { Width: > 1, Height: > 1 }, $"a target row laid out as {r.Bounds}"));
                Assert.Equal(LocalizationService.Instance["Toolbar_TonePreviewTarget_Rig"], AutomationProperties.GetName(rows[0]));
                Assert.Same(shell.ToneRig.SelectTonePreviewRigCommand, rows[0].Command);
                Assert.Same(shell.ToneRig.ToggleTonePreviewRocksmithCommand, rows[1].Command);
                Capture(window, "tone-menu");
                Assert.Contains("Picked", rows[0].Classes);
                Assert.DoesNotContain("Picked", rows[1].Classes);

                rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatch();
                Assert.False(flyout.IsOpen, "picking a target left the popup open");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task TheExportButton_opensTheFileExportRows_andAPickClosesIt() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 1920, Height = 400, Content = view };
            window.Show();
            try
            {
                var button = Named<Button>(view, "ExportFlyoutButton");
                Assert.True(button.Bounds is { Width: > 1, Height: > 1 }, $"{button.Bounds}");
                Assert.Equal(LocalizationService.Instance["Toolbar_Export"], AutomationProperties.GetName(button));

                var flyout = Assert.IsType<PrewarmableFlyout>(button.Flyout);
                flyout.ShowAt(button);
                Dispatch();
                Assert.True(flyout.IsOpen);
                var rows = Assert.IsType<ExportPopoverView>(flyout.Content).GetVisualDescendants().OfType<Button>().ToList();
                Assert.Equal(6, rows.Count);
                Assert.Equal(LocalizationService.Instance["Menu_ExportRocksmith"], AutomationProperties.GetName(rows[0]));
                Assert.Same(shell.ShellDialogs.OpenRocksmithExportCommand, rows[0].Command);
                Assert.Same(shell.Export.ExportSheetPdfCommand, rows[4].Command);
                Capture(window, "export-menu");

                rows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatch();
                Assert.False(flyout.IsOpen, "picking an export left the popup open");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task APickInTheTargetPopover_runsItsCommand_beforeTheFlyoutCloses() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var session = new FakeTonelabSession { State = TonelabSessionState.Disconnected, CanLaunch = true };
            typeof(ToneRigViewModel).GetField("_tonelab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(shell.ToneRig, (Func<ITonelabSession?>)(() => session));
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 1600, Height = 400, Content = view };
            window.Show();
            try
            {
                var caret = Named<Button>(view, "TonePreviewTargetButton");
                var flyout = Assert.IsType<PrewarmableFlyout>(caret.Flyout);
                flyout.ShowAt(caret);
                Dispatch();
                var rows = Assert.IsType<TonePreviewTargetPopoverView>(flyout.Content).GetVisualDescendants().OfType<Button>().ToList();
                Assert.True(rows[1].IsEffectivelyEnabled, "the Rocksmith row is disabled although a session can launch");

                ClickWithMouse(window, rows[1]);
                Dispatch();
                Assert.False(flyout.IsOpen, "picking a target left the popup open");
                // The launch runs inside Task.Run: wait for it to be entered (a deadlock detector, not a budget).
                Assert.True(session.LaunchEntered.Wait(TimeSpan.FromSeconds(30)), "the pick never launched the Rocksmith session (the row's command did not run, or the box was starved)");
                Assert.Equal(1, session.Launches);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task APickInTheExportPopover_runsItsCommand_beforeTheFlyoutCloses() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = 1920, Height = 400, Content = view };
            window.Show();
            var project = shell.ProjectState.ActiveProject;
            shell.ProjectState.ActiveProject = null;
            try
            {
                var button = Named<Button>(view, "ExportFlyoutButton");
                var flyout = Assert.IsType<PrewarmableFlyout>(button.Flyout);
                flyout.ShowAt(button);
                Dispatch();
                var rows = Assert.IsType<ExportPopoverView>(flyout.Content).GetVisualDescendants().OfType<Button>().ToList();

                ClickWithMouse(window, rows[0]);
                Dispatch();
                Assert.False(flyout.IsOpen, "picking an export left the popup open");
                Assert.Equal(LocalizationService.Instance["Status_Export_NoProject"], shell.ProjectState.MainDisplayMessage);
            }
            finally
            {
                shell.ProjectState.ActiveProject = project;
                window.Close();
            }
        }, CancellationToken.None);

    private static void ClickWithMouse(Window window, Control target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(1920)]
    public Task TheToolbarGroups_areSpacedEvenlyAroundTheirDividers_andEveryIconIsCentred(double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = width, Height = 52, Content = view };
            window.Show();
            try
            {
                Dispatch();
                window.UpdateLayout();
                var offenders = new List<string>();
                var groups = view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("TbGroup")).ToList();
                Assert.True(groups.Count >= 4, $"only {groups.Count} toolbar groups found");
                foreach (var group in groups)
                {
                    var label = group.Children.OfType<TextBlock>().FirstOrDefault(t => t.IsVisible);
                    var combo = group.Children.OfType<ComboBox>().FirstOrDefault(c => c.IsVisible);
                    if (label is not null && combo is not null && Math.Abs(CenterY(label, window) - CenterY(combo, window)) > 1)
                    {
                        offenders.Add($"label '{label.Text}' centre {CenterY(label, window):0.##} vs combo {CenterY(combo, window):0.##}");
                    }
                    if (label is not null && combo is not null
                        && combo.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)) is { } value
                        && (Math.Abs(CenterY(label, window) - CenterY(value, window)) > 1
                            || label.FontSize != value.FontSize || !Equals(label.FontFamily, value.FontFamily)))
                    {
                        offenders.Add($"label '{label.Text}' text centre {CenterY(label, window):0.##} / {label.FontSize} / {label.FontFamily.Name} vs the combo's value text {CenterY(value, window):0.##} / {value.FontSize} / {value.FontFamily.Name}");
                    }
                }

                var gaps = new List<double>();
                foreach (var divider in view.GetVisualDescendants().OfType<Border>().Where(b => b.IsVisible && (b.Classes.Contains("GroupDivider") || b.Classes.Contains("GroupSep"))))
                {
                    if (divider.Parent is not Panel parent) continue;
                    var shown = parent.Children.Where(c => c.IsVisible && c.Bounds.Width > 0).ToList();
                    var at = shown.IndexOf(divider);
                    if (at <= 0 || at + 1 >= shown.Count) continue;
                    var left = Left(divider, window) - (Left(shown[at - 1], window) + shown[at - 1].Bounds.Width);
                    var right = Left(shown[at + 1], window) - (Left(divider, window) + divider.Bounds.Width);
                    gaps.Add(left);
                    gaps.Add(right);
                    if (Math.Abs(left - right) > 1) offenders.Add($"divider before '{Describe(shown[at + 1])}': gap before {left:0.##}, after {right:0.##}");
                }

                if (gaps.Count > 0 && gaps.Max() - gaps.Min() > 1) offenders.Add($"divider gaps differ across the rail: {string.Join(", ", gaps.Select(g => g.ToString("0.##")))}");

                foreach (var button in groups.SelectMany(g => g.Children).OfType<ContentControl>().Where(b => b is Button or ToggleButton))
                {
                    if (button.Content is not PathIcon icon || !button.IsVisible) continue;
                    var dx = Math.Abs(CenterX(icon, window) - CenterX(button, window));
                    var dy = Math.Abs(CenterY(icon, window) - CenterY(button, window));
                    if (dx > 0.5 || dy > 0.5) offenders.Add($"icon of '{AutomationProperties.GetName(button)}' is off centre by {dx:0.##}, {dy:0.##}");
                }

                Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static double Left(Visual v, Visual root) => v.TranslatePoint(default, root)!.Value.X;

    private static double CenterX(Control c, Visual root) => c.TranslatePoint(new Point(c.Bounds.Width / 2, 0), root)!.Value.X;

    private static double CenterY(Control c, Visual root) => c.TranslatePoint(new Point(0, c.Bounds.Height / 2), root)!.Value.Y;

    private static string Describe(Control c) => c is TextBlock t ? t.Text ?? "" : AutomationProperties.GetName(c) ?? c.GetType().Name;

    [Theory]
    [InlineData(1280)]
    [InlineData(1600)]
    [InlineData(1920)]
    public Task TheToolbar_laysOutWithoutClippingTheRightEnd_andIsCaptured(double width) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            var view = new ToolbarView { DataContext = shell, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new Window { Width = width, Height = 52, Content = view };
            window.Show();
            try
            {
                Dispatch();
                window.UpdateLayout();
                var quick = Named<StackPanel>(view, "ToolbarQuickGroup");
                Assert.True(quick.Bounds is { Width: > 1, Height: > 1 }, $"{quick.Bounds}");
                // The pinned right end is never clipped by the window.
                var right = quick.TranslatePoint(new Point(quick.Bounds.Width, 0), window)!.Value.X;
                Assert.True(right <= width + 0.5, $"the right end runs to {right:0.#} in a {width} px window");
                foreach (var name in new[] { "PlayPauseButton", "TempoFlyoutButton", "TracksFlyoutButton", "ExportFlyoutButton", "TonePreviewButton" })
                    Assert.True(AnyNamed(view, name) is { Bounds: { Width: > 1, Height: > 1 } }, $"{name} did not lay out");

                Capture(window, $"toolbar-{(int)width}");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static Control AnyNamed(Visual root, string name)
        => root.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

    private static string? CaptureDir()
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_TOOLBAR_CAPTURE_DIR");
        return UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir) ? dir : null;
    }

    private static void Capture(Window window, string file)
    {
        if (CaptureDir() is not { } dir) return;
        var tag = Environment.GetEnvironmentVariable("DOTCHARTER_TOOLBAR_CAPTURE_TAG") ?? "after";
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"{file}-{tag}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
