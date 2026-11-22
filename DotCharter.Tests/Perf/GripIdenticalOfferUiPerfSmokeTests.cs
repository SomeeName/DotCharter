using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #839: the Shape page's "Also apply to N identical hand shapes" offer in the REAL <see cref="InspectorView" /> at
///     the primary 530 x 370. Hidden before a save; after a grip save over two repeated arpeggios it shows, measures
///     non-zero, wraps rather than cuts its caption, and its click gives the twin the grip. Run it under
///     <c>DOTCHARTER_HEADLESS_CULTURE=de-DE</c> and <c>ja-JP</c> too; with <c>DOTCHARTER_INSPECTOR_CAPTURE=1</c> and
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> it writes <c>839-identical-offer.png</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class GripIdenticalOfferUiPerfSmokeTests
{
    private static readonly Dictionary<int, int> Frets = new() { [1] = 5, [2] = 5, [3] = 7 };

    [Fact]
    public async Task TheOffer_ShowsAfterAGripSave_LaysOut_AndAppliesOnItsClick() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var (project, arrangement, level, tab) = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, vm.SelectedTabIndex);
            Window? window = null;
            try
            {
                var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
                var chart = new ChartLevel();
                lead.Levels.Add(chart);
                lead.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "", Frets = new(Frets) });
                var source = Arpeggio(chart, 0);
                var twin = Arpeggio(chart, 8);
                var song = new ChartProject();
                for (var i = 0; i < 16; i++) song.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
                song.Arrangements.Add(lead);
                state.ActiveProject = song;
                state.ActiveArrangement = lead;
                state.ActiveLevel = chart;
                vm.SelectedTabIndex = 0;

                var view = new InspectorView { DataContext = vm };
                window = new Window { Width = 530, Height = 370, Content = view };
                window.Show();
                Settle(window);
                selection.ClearSelections();
                selection.SelectedHandShapes.Add(source);
                Settle(window);
                vm.Sections.ChooseCommand.Execute(SoundSection.Shape);
                Settle(window);
                var offer = Named(view, "SoundShapeApplyIdentical");
                Assert.False(offer.IsEffectivelyVisible, "the offer is hidden before a save");

                vm.GripInspector.SetFinger(3, 2);
                vm.GripInspector.SaveFingeringCommand.Execute(null);
                offer.BringIntoView();
                Settle(window);

                if (InspectorCapture.InspectorCaptureFixture.Enabled)
                    InspectorCapture.InspectorCaptureFixture.Save(window, "", $"839-identical-offer-{HeadlessSessionFixture.SweptCulture}");

                var at = $"530x370 ({HeadlessSessionFixture.SweptCulture})";
                Assert.True(offer.IsEffectivelyVisible, $"{at}: the offer shows after the save");
                Assert.True(offer.Bounds is { Width: > 1, Height: > 1 }, $"{at}: the offer laid out as {offer.Bounds}");
                var text = (TextBlock)Named(view, "SoundShapeApplyIdenticalText");
                Assert.True(text.Bounds is { Width: > 1, Height: > 1 }, $"{at}: the caption laid out as {text.Bounds}");
                Assert.Contains("1", text.Text);
                Assert.Equal(Avalonia.Media.TextWrapping.Wrap, text.TextWrapping);

                ((Button)offer).Command!.Execute(null);
                Settle(window);
                Assert.Equal(source.TemplateId, twin.TemplateId);
                Assert.False(offer.IsEffectivelyVisible, "the offer closes after its click");
            }
            finally
            {
                selection.ClearSelections();
                vm.SelectedTabIndex = tab;
                window?.Close();
                state.ActiveProject = project;
                state.ActiveArrangement = arrangement;
                state.ActiveLevel = level;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);

    private static HandShape Arpeggio(ChartLevel chart, int start)
    {
        foreach (var (beat, stringIndex) in new[] { (0, 1), (1, 2), (2, 3), (3, 2) })
            chart.Notes.Add(new GuitarNote { Position = new(start + beat), StringIndex = stringIndex, Fret = Frets[stringIndex] });
        var shape = new HandShape { TemplateId = 0, Position = new(start), EndPosition = new(start + 3) };
        chart.HandShapes.Add(shape);
        return shape;
    }

    private static Control Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no control named '{name}' — repoint this test.");

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
