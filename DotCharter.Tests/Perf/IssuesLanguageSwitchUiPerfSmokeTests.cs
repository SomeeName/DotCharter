using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Issues tab's finding rows read their sentence through <c>IssueMessageConverter</c> at convert
///     time, so they follow a language switch only if the rows are realized again. The REAL
///     <see cref="IssuesInspectorView" /> inside the inspector, over the app's own view model: findings
///     shown in English, the language switched at run time, and the realized rows' text, tooltip and
///     accessible name must read German with no reselection and no new validation pass.
/// </summary>
[Collection("HeadlessUI")]
public sealed class IssuesLanguageSwitchUiPerfSmokeTests
{
    [Theory]
    [InlineData(420)]
    [InlineData(530)]
    public async Task TheFindingRows_FollowALanguageSwitch_WithoutAnotherPass(int width) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var selection = services.GetRequiredService<ISelectionService>();
            var vm = services.GetRequiredService<InspectorViewModel>();
            var localization = LocalizationService.Instance;
            var restoreCulture = localization.ConfiguredCulture;
            var previousProject = state.ActiveProject;
            var previousArrangement = state.ActiveArrangement;
            var previousTab = vm.SelectedTabIndex;
            Window? window = null;
            try
            {
                localization.SetConfiguredCulture("en-US");
                NarrowWidthLayout.SeedSong(state);
                selection.ClearSelections();
                var view = new InspectorView { DataContext = vm };
                // Tiny (420 px): the Findings section pages the list card on; Small (530 px): the list keeps the left column.
                window = NarrowWidthLayout.NewWindow(width, 600, view);
                window.Show();
                vm.SelectedTabIndex = InspectorViewModel.IssuesTabIndex;
                NarrowWidthLayout.Settle(window);
                // Seeding the song queued the inspector's 150 ms debounced Issues refresh. Left on its
                // timer it lands whenever the box gets to it, under load after the findings below are
                // seeded, and replaces them with the song's own: run it now and wait for its pass.
                vm.FlushIssueRefresh();
                HeadlessIssuePass.Settle(vm.Issues);

                ArrangementIssue[] findings =
                [
                    Bend(3.5, 4000),
                    Bend(4, 9000)
                ];
                vm.Issues.Items.ResetWith(findings);
                vm.Issues.IssueGroups.ResetWith(IssueGrouping.Build(findings));
                vm.Issues.IssueCount = findings.Length;
                NarrowWidthLayout.Settle(window);

                var issues = view.GetVisualDescendants().OfType<IssuesInspectorView>().Single();
                Assert.True(issues.Bounds is { Width: > 1, Height: > 1 }, $"the Issues tab laid out as {issues.Bounds}");
                Assert.Equal(findings.Select(f => f.Message), RowTexts(issues));

                localization.SetConfiguredCulture("de-DE");
                NarrowWidthLayout.Settle(window);

                var german = findings.Select(f => ArrangementIssueText.Of(f, localization)).ToArray();
                Assert.All(german.Zip(findings), pair => Assert.NotEqual(pair.Second.Message, pair.First));
                Assert.Equal(german, RowTexts(issues));
                var rows = Rows(issues);
                Assert.Equal(german, rows.Select(r => ToolTip.GetTip(r) as string));
                Assert.Equal(german, rows.Select(AutomationProperties.GetName));
            }
            finally
            {
                localization.SetConfiguredCulture(restoreCulture);
                CultureInfo.CurrentUICulture = localization.Culture;
                vm.SelectedTabIndex = previousTab;
                window?.Close();
                state.ActiveProject = previousProject;
                state.ActiveArrangement = previousArrangement;
            }
        }, CancellationToken.None);

    private static ArrangementIssue Bend(double steps, double timeMs) =>
        new(ArrangementIssueType.BendStrengthTooHigh, ArrangementIssueText.English("Validation_BendStrengthTooHigh", [steps]),
            timeMs, MessageKey: "Validation_BendStrengthTooHigh", MessageArgs: [steps]);

    private static List<Button> Rows(IssuesInspectorView issues) =>
        [.. issues.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "IssueRow" && b.IsEffectivelyVisible)];

    private static string?[] RowTexts(IssuesInspectorView issues)
    {
        var texts = Rows(issues).Select(r => r.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "IssueMessage"))
            .ToList();
        Assert.All(texts, t => Assert.True(t.Bounds is { Width: > 0, Height: > 0 }, $"a finding row's text measured {t.Bounds}"));
        return [.. texts.Select(t => t.Text)];
    }
}
