using DotCharter.Core.Domain.Vocals;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab's Ignore and grouping (#1032) on the real view model over a real chart editor: identical
///     findings fold into one counted row that expands to its occurrences (each still a jump); Ignore and Ignore
///     all take findings out of the count and the list, Show ignored brings them back with Stop ignoring, every
///     change is one undo step, an edited subject brings its finding back, and an error offers no Ignore at all.
///     The fixture is the issue's chart: "oh baddy" on every line, so the D13 finding repeats once per line.
/// </summary>
public sealed class InspectorIssueIgnoreTests
{
    private const int Lines = 4;

    [Fact]
    public void IdenticalFindings_FoldIntoOneCountedRow_ThatExpandsToJumpableOccurrences()
    {
        var (_, vm) = Create();
        vm.RecomputeIssues();

        var group = Assert.Single(vm.Rows.Items, row => row.IsGroup && IsBaddy(row.Issue));
        Assert.Equal(Lines, group.Count);
        Assert.Contains(Lines.ToString(System.Globalization.CultureInfo.InvariantCulture), group.CountText, StringComparison.Ordinal);
        Assert.False(group.HasFix);
        Assert.DoesNotContain(vm.Rows.Items, row => row.IsMember);
        // The error stays its own row.
        Assert.Single(vm.Rows.Items, row => row is { IsGroup: false } && row.Issue.Severity == ArrangementIssueSeverity.Important);

        group.Activate.Execute(group.Target);

        var members = vm.Rows.Items.Where(row => row.IsMember && IsBaddy(row.Issue)).ToList();
        Assert.Equal(Lines, members.Count);
        Assert.All(members, member => Assert.Same(vm.JumpToIssueCommand, member.Activate));
        Assert.Equal(Lines, members.Select(member => member.Issue.Lyric!.Phrase).Distinct().Count());
        Assert.True(Assert.Single(vm.Rows.Items, row => row.IsGroup && IsBaddy(row.Issue)).IsExpanded);
    }

    [Fact]
    public void Ignore_TakesOneOccurrenceOut_AndUndoBringsItBack()
    {
        var (harness, vm) = Create();
        vm.RecomputeIssues();
        var before = vm.IssueCount;
        var one = vm.Items.First(IsBaddy);

        vm.Ignore.Ignore.Execute(one);

        Assert.Equal(before - 1, vm.IssueCount);
        Assert.Equal(1, vm.IgnoredIssueCount);
        Assert.True(vm.HasIgnoredIssues);
        Assert.Equal(Lines - 1, Assert.Single(vm.Rows.Items, row => row.IsGroup && IsBaddy(row.Issue)).Count);
        Assert.Single(harness.Project.IgnoredFindings);

        harness.History.Undo();
        vm.RecomputeIssues();

        Assert.Equal(before, vm.IssueCount);
        Assert.Equal(0, vm.IgnoredIssueCount);
        Assert.Empty(harness.Project.IgnoredFindings);
    }

    [Fact]
    public void IgnoreAll_HidesTheGroup_ShowIgnoredListsIt_AndStopIgnoringRestoresIt()
    {
        var (harness, vm) = Create();
        vm.RecomputeIssues();
        var before = vm.IssueCount;
        var group = Assert.Single(vm.Rows.Items, row => row.IsGroup && IsBaddy(row.Issue));
        Assert.Same(vm.Ignore.IgnoreAllLike, group.IgnoreCommand);

        group.IgnoreCommand.Execute(group.Issue);

        Assert.Equal(before - Lines, vm.IssueCount);
        Assert.Equal(Lines, vm.IgnoredIssueCount);
        Assert.Equal(Lines, harness.Project.IgnoredFindings.Count);
        Assert.DoesNotContain(vm.Rows.Items, row => IsBaddy(row.Issue));
        Assert.DoesNotContain(vm.Items, IsBaddy);

        vm.ShowIgnoredIssues = true;

        var ignored = Assert.Single(vm.Rows.Items, row => IsBaddy(row.Issue));
        Assert.True(ignored is { IsGroup: true, IsIgnored: true, Count: Lines });
        Assert.Same(vm.Ignore.StopIgnoringAllLike, ignored.IgnoreCommand);
        Assert.Contains("Issues_Row_StopIgnoring", ignored.IgnoreLabel, StringComparison.Ordinal);
        Assert.Equal(before - Lines, vm.IssueCount);

        ignored.IgnoreCommand.Execute(ignored.Issue);

        Assert.Equal(before, vm.IssueCount);
        Assert.Equal(0, vm.IgnoredIssueCount);
        Assert.Empty(harness.Project.IgnoredFindings);
    }

    [Fact]
    public void AnEditedLine_BringsItsIgnoredFindingBack()
    {
        var (harness, vm) = Create();
        vm.RecomputeIssues();
        vm.IgnoreAllLike(vm.Items.First(IsBaddy));
        Assert.Equal(Lines, vm.IgnoredIssueCount);

        var other = harness.Project.VocalTracks[0].Phrases[2].Lyrics[0];
        harness.History.Do(new DotCharter.Services.State.SetLyricTextAction(other, "yeah"));
        vm.RecomputeIssues();

        Assert.Equal(Lines - 1, vm.IgnoredIssueCount);
        Assert.Equal(2, Assert.Single(vm.Items, IsBaddy).Lyric!.Phrase);
    }

    [Fact]
    public void AnError_OffersNoIgnore_AndIgnoringItChangesNothing()
    {
        var (harness, vm) = Create();
        vm.RecomputeIssues();
        var before = vm.IssueCount;
        var error = Assert.Single(vm.Rows.Items, row => row.Issue.Severity == ArrangementIssueSeverity.Important);

        Assert.False(error.HasIgnore);
        vm.IgnoreIssue(error.Issue);
        vm.IgnoreAllLike(error.Issue);

        Assert.Equal(before, vm.IssueCount);
        Assert.Empty(harness.Project.IgnoredFindings);
        Assert.False(harness.History.CanUndo);
    }

    private static bool IsBaddy(ArrangementIssue issue) =>
        issue.MessageKey == "Validation_LyricSeveralSyllablesOnOneNote";

    private static (ChartEditHarness Harness, InspectorIssuesViewModel Vm) Create()
    {
        var harness = new ChartEditHarness();
        var level = harness.Project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        var track = new VocalTrack { Name = "Lead Vocals" };
        for (var line = 0; line < Lines; line++)
        {
            var phrase = new LyricPhrase { Position = new FractionalPosition(line * 4) };
            phrase.Lyrics.Add(new LyricEvent { Text = "oh", Position = new FractionalPosition(line * 4), EndPosition = new FractionalPosition(line * 4 + 1) });
            phrase.Lyrics.Add(new LyricEvent
            {
                Text = "baddy", Position = new FractionalPosition(line * 4 + 1), EndPosition = new FractionalPosition(line * 4 + 2),
                IsPhraseEnd = true
            });
            phrase.EndPosition = new FractionalPosition(line * 4 + 2);
            track.Phrases.Add(phrase);
        }

        harness.Project.VocalTracks.Add(track);
        var maintenance = RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy;
        var vm = new InspectorIssuesViewModel(harness.State, harness.Editor, new OneError(), maintenance,
            new ExportReadinessViewModel(harness.State, new CheckpointsViewModelTests.FakeDocumentService(),
                harness.Transport, harness.Editor, FakeLocalization.KeysOnly(),
                RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger(),
                new DotCharter.Services.Editing.Quality.ChartQualityReporter(new DotCharter.Services.Editing.Validation.ArrangementValidationService(new FakeAppLogger())),
                new DotCharter.Tests.Infrastructure.TestUiDispatcher()),
            harness.History, FakeLocalization.KeysOnly(), () => null, new GatedInlineDispatcher(), new FakeAppLogger())
        {
            OffThread = work => Task.FromResult(work())
        };
        return (harness, vm);
    }

    /// <summary>The arrangement rules answer one error on the note; the lyric checks run for real.</summary>
    private sealed class OneError : IArrangementValidationService
    {
        public IReadOnlyList<ArrangementIssue> Validate(Arrangement? arrangement, IList<BeatMarker> beats,
            double? totalAudioLengthMs = null, ChartProject? project = null) =>
            [new ArrangementIssue(ArrangementIssueType.FirstPhraseNotEmpty, "an error", 500, Note: arrangement!.Levels[0].Notes[0])];
    }
}
