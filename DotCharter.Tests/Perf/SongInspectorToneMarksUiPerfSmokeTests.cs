using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #181, the Inspector's Song tab: its Tones list is the project's tones, a placed key with
///     no definition carries a "no definition" mark, and a refused rename prints its reason
///     under the row. Both marks are constructed, attached and laid out inside a real window
///     and must MEASURE — a mark whose style failed still arranges, with no size.
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongInspectorToneMarksUiPerfSmokeTests
{
    [Fact]
    public Task TheNoDefinitionMark_AndARefusedRename_LayOutUnderTheirRows() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var vm = new SongOverviewViewModel(state, new HistoryService(logger), logger, FakeLocalization.English(),
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy);
            var tab = new SongInspectorView { DataContext = vm };
            // #601: the tones are a section of the Song tab's rail; open it, as its entry's click does.
            tab.Rail.Choose("SongTonesBody");
            var window = new Window
            {
                Width = 360, Height = 900, Content = InspectorScope(tab),
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x07, 0x0E))
            };
            window.Show();
            try
            {
                state.ActiveProject = Project();
                Settle(window);
                Assert.Equal(["lead_clean", "lead_dist", "solo"], vm.TonesPanel.Groups.Select(t => t.Name));

                var marks = Named(tab, "MarkerWarning");
                var shown = marks.Where(m => m.IsEffectivelyVisible).ToList();
                var solo = Assert.Single(shown);
                Assert.Equal("solo", ((MarkerGroupRow)solo.DataContext!).Name);
                AssertMeasured(solo, "the \"no definition\" mark of 'solo'");
                Assert.All(Named(tab, "MarkerRenameRefusal"), r => Assert.False(r.IsEffectivelyVisible));
                Capture(window, "song-tab-tones-listed.png");

                var dist = vm.TonesPanel.Groups.Single(t => t.Name == "lead_dist");
                dist.EditName = "lead_clean";
                vm.TonesPanel.RenameCommand.Execute(dist);
                Settle(window);

                var refusal = Assert.Single(Named(tab, "MarkerRenameRefusal"), r => r.IsEffectivelyVisible);
                Assert.Same(dist, refusal.DataContext);
                AssertMeasured(refusal, "the refused rename's reason");
                var text = refusal.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.Equal(dist.Error, text.Text);
                Capture(window, "song-tab-rename-refused.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>Two designed tones (one placed, one only declared) and a placed key nobody designed.</summary>
    private static ChartProject Project()
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120 });
        foreach (var key in new[] { "lead_dist", "lead_clean" })
        {
            project.ToneDefinitions.Add(new ToneDefinition
            {
                Key = key, Name = key, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared))
            });
        }

        var lead = new Arrangement { Name = "Lead", ToneBase = "lead_clean", ToneA = "lead_dist", StartingTone = "lead_clean" };
        lead.ToneChanges.Add(new ToneChange { Position = TimelineMath.PositionFromMs(2000, project.Beats), ToneName = "lead_dist" });
        lead.ToneChanges.Add(new ToneChange { Position = TimelineMath.PositionFromMs(4000, project.Beats), ToneName = "solo" });
        project.Arrangements.Add(lead);
        return project;
    }

    private static List<Control> Named(Control root, string name) =>
        [.. root.GetVisualDescendants().OfType<Control>().Where(c => c.Name == name)];

    /// <summary>The Song tab inside the Inspector's style scope, as the Inspector pane hosts it.</summary>
    private static UserControl InspectorScope(Control tab)
    {
        var scope = new UserControl { Content = tab };
        scope.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
        {
            Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
        });
        return scope;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static void AssertMeasured(Control control, string what) =>
        Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
            $"{what} laid out as {control.Bounds} — it rendered nothing");

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
