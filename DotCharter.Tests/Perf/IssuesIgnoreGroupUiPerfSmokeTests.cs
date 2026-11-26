using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Localization;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Issues tab's grouping and Ignore (#1032) in the real <see cref="InspectorView" /> over the app's own view
///     models: a song whose every line sings "baddy" on one note produces the identical D13 finding per line. The
///     findings list shows ONE counted row for them, which expands to its occurrences; its Ignore all, the
///     occurrences' rows, the Show ignored toggle and Stop ignoring all construct, attach and lay out with a size,
///     in English, German and Japanese.
/// </summary>
[Collection("HeadlessUI")]
public sealed class IssuesIgnoreGroupUiPerfSmokeTests
{
    private const int Lines = 75;

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public async Task RepeatedFindings_FoldIntoOneRow_AndTheIgnoreControlsLayOut(string culture) =>
        await HeadlessSessionFixture.Dispatch(() => InCulture(culture, () => WithRepeatedLyrics(530, 520, (window, vm, issues) =>
        {
            NarrowWidthLayout.Capture(window, $"issues-repeated-lyrics-{culture}");
            var group = vm.Issues.Rows.Items.Single(row => row.IsGroup && row.Count == Lines && row.Issue.MessageKey == "Validation_LyricSeveralSyllablesOnOneNote");
            var groupButton = RowButton(issues, group);
            AssertLaidOut(groupButton, "the group row");
            AssertLaidOut(Inside<TextBlock>(groupButton.GetVisualParent()!, "IssueGroupCount"), "the group's count");
            AssertLaidOut(Inside<PathIcon>(groupButton.GetVisualParent()!, "IssueGroupFolded"), "the group's chevron");
            AssertLaidOut(Inside<Button>(groupButton.GetVisualParent()!, "IssueRowIgnore"), "the group's Ignore all");

            groupButton.Command!.Execute(groupButton.CommandParameter);
            NarrowWidthLayout.Settle(window);
            var member = vm.Issues.Rows.Items.First(row => row.IsMember && row.Issue.MessageKey == group.Issue.MessageKey);
            var memberButton = RowButton(issues, member);
            AssertLaidOut(memberButton, "an occurrence row");
            AssertLaidOut(Inside<Border>(memberButton.GetVisualParent()!, "IssueMemberIndent"), "an occurrence's indent");
            AssertLaidOut(Inside<Button>(memberButton.GetVisualParent()!, "IssueRowIgnore"), "an occurrence's Ignore");

            var countBefore = vm.Issues.IssueCount;
            vm.Issues.IgnoreAllLike(group.Issue);
            vm.FlushIssueRefresh();
            Settle(window, vm);
            Assert.Equal(countBefore - Lines, vm.Issues.IssueCount);
            NarrowWidthLayout.Capture(window, $"issues-repeated-lyrics-ignored-{culture}");
            AssertLaidOut(NarrowWidthLayout.Named<CheckBox>(issues, "ShowIgnoredIssuesBox"), "Show ignored");

            vm.Issues.ShowIgnoredIssues = true;
            NarrowWidthLayout.Settle(window);
            var ignored = vm.Issues.Rows.Items.Single(row => row.IsGroup && row.IsIgnored);
            AssertLaidOut(Inside<Button>(RowButton(issues, ignored).GetVisualParent()!, "IssueRowIgnore"), "Stop ignoring");
        })), CancellationToken.None);

    private static void AssertLaidOut(Control control, string what) =>
        Assert.True(control.IsEffectivelyVisible && control.Bounds is { Width: > 1, Height: > 1 }, $"{what} laid out as {control.Bounds}");

    private static Button RowButton(Visual issues, IssueListRow row)
    {
        var list = NarrowWidthLayout.Named<ItemsControl>(issues, "IssueGroupList");
        list.ScrollIntoView(row);
        if (TopLevel.GetTopLevel(list) is Window window) NarrowWidthLayout.Settle(window);
        return issues.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "IssueRow" && ReferenceEquals(b.DataContext, row));
    }

    private static T Inside<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void InCulture(string culture, Action body)
    {
        var localization = LocalizationService.Instance;
        var restore = localization.ConfiguredCulture;
        try
        {
            localization.SetConfiguredCulture(culture);
            body();
        }
        finally
        {
            localization.SetConfiguredCulture(restore);
            CultureInfo.CurrentUICulture = localization.Culture;
        }
    }

    /// <summary>The sweep's song with <see cref="Lines" /> lines of "oh baddy", the Issues tab's own pass landed.</summary>
    private static void WithRepeatedLyrics(int width, double height, Action<Window, InspectorViewModel, IssuesInspectorView> body)
    {
        var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
        var state = services.GetRequiredService<ProjectState>();
        var vm = services.GetRequiredService<InspectorViewModel>();
        var previousProject = state.ActiveProject;
        var previousArrangement = state.ActiveArrangement;
        var previousTab = vm.SelectedTabIndex;
        Window? window = null;
        try
        {
            NarrowWidthLayout.SeedSong(state);
            services.GetRequiredService<ISelectionService>().ClearSelections();
            var track = new VocalTrack { Name = "Lead Vocals" };
            for (var line = 0; line < Lines; line++)
            {
                var phrase = new LyricPhrase { Position = new FractionalPosition(line * 4) };
                phrase.Lyrics.Add(new LyricEvent { Text = "Oh", Position = new FractionalPosition(line * 4), EndPosition = new FractionalPosition(line * 4 + 1) });
                phrase.Lyrics.Add(new LyricEvent
                {
                    Text = "baddy", Position = new FractionalPosition(line * 4 + 1), EndPosition = new FractionalPosition(line * 4 + 2),
                    IsPhraseEnd = true
                });
                phrase.EndPosition = new FractionalPosition(line * 4 + 2);
                track.Phrases.Add(phrase);
            }

            state.ActiveProject!.VocalTracks.Add(track);
            var view = new InspectorView { DataContext = vm };
            window = NarrowWidthLayout.NewWindow(width, height, view);
            window.Show();
            vm.SelectedTabIndex = InspectorViewModel.IssuesTabIndex;
            vm.IssuesSections.Open(IssuesSection.Findings);
            NarrowWidthLayout.Settle(window);
            vm.FlushIssueRefresh();
            Settle(window, vm);
            vm.Issues.RecomputeIssues();
            Settle(window, vm);
            body(window, vm, view.GetVisualDescendants().OfType<IssuesInspectorView>().Single());
        }
        finally
        {
            vm.IssuesSections.Open(IssuesSection.Findings);
            vm.SelectedTabIndex = previousTab;
            window?.Close();
            state.ActiveProject = previousProject;
            state.ActiveArrangement = previousArrangement;
        }
    }

    /// <summary>The pass runs off the UI thread and lands through the dispatcher: pump until it has.</summary>
    private static void Settle(Window window, InspectorViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!vm.Issues.Pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            NarrowWidthLayout.Settle(window);
            if (!vm.Issues.Pending.IsCompleted) Thread.Sleep(5);
        }

        Assert.True(vm.Issues.Pending.IsCompleted, "the Issues tab's validation did not land in 60 s: the box was starved or the pass never finished");
        NarrowWidthLayout.Settle(window);
    }
}
