using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tests.Ai.Chart;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #803: the Issues tab's AI row (<c>IssueAiRow</c> in <c>IssuesInspectorView.axaml</c>) builds, attaches
///     inside the inspector grammar and MEASURES non-zero over the app's own <see cref="InspectorViewModel" />:
///     Fix with AI, Fix all in this phrase and Explain with AI for a fixable finding, and the "no AI job"
///     line instead of the fix for one no job can fix. A row whose styles or bindings failed still arranges,
///     it just has no size. The behaviour is pinned in the gate by <c>IssueAiViewModelTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class IssueAiRowUiPerfSmokeTests
{
    [Fact]
    public async Task AFixableFinding_ShowsFixFixPhraseAndExplain_AndTheyMeasure() =>
        await Show((vm, f) =>
        {
            var target = new ArrangementIssue(ArrangementIssueType.AnchorInsideHandShape, "Anchor is placed inside a hand shape.", 5000);
            var other = new ArrangementIssue(ArrangementIssueType.FingerSetForOpenString, "Finger set for an open string.", 6000);
            vm.Issues.IssueAi.Offer(target, f.Arrangement, f.Level, [target, other]);
        }, ["IssueAiRow", "IssueAiFixButton", "IssueAiFixPhraseButton", "IssueAiExplainButton"], ["IssueAiNoJobText"]);

    [Fact]
    public async Task AFindingNoJobCanFix_ShowsWhy_AndExplain_ButNoFix() =>
        await Show((vm, f) =>
        {
            var issue = new ArrangementIssue(ArrangementIssueType.OverlappingNotes, "Notes overlap.", 8000);
            vm.Issues.IssueAi.Offer(issue, f.Arrangement, f.Level, [issue]);
        }, ["IssueAiRow", "IssueAiNoJobText", "IssueAiExplainButton"], ["IssueAiFixButton", "IssueAiFixPhraseButton"]);

    private static Task Show(Action<InspectorViewModel, AiChartFixture> offer, string[] shown, string[] hidden) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<InspectorViewModel>();
            var state = services.GetRequiredService<ProjectState>();
            var f = new AiChartFixture();
            state.ActiveProject = f.Project;
            state.ActiveArrangement = f.Arrangement;
            state.ActiveLevel = f.Level;
            offer(vm, f);

            var view = new IssuesInspectorView { DataContext = vm };
            var host = new UserControl { Content = view };
            host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/InspectorStyles.axaml")
            });
            var window = new Window
            {
                Width = 420, Height = 900, Content = host, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x14, 0x15, 0x19))
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                var controls = view.GetVisualDescendants().OfType<Control>().ToList();
                foreach (var name in shown)
                {
                    var control = controls.Single(c => c.Name == name);
                    Assert.True(control.IsEffectivelyVisible, $"{name} is hidden.");
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {control.Bounds} — it rendered nothing.");
                }

                foreach (var name in hidden)
                    Assert.False(controls.Single(c => c.Name == name).IsEffectivelyVisible, $"{name} shows and should not.");
            }
            finally
            {
                vm.Issues.IssueAi.Offer(null, null, null, []);
                state.ActiveProject = null;
                window.Close();
            }
        }, CancellationToken.None);
}
