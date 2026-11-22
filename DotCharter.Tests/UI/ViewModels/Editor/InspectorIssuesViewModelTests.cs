using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab on its own view model (#659), driven directly over a real chart editor
///     and a faked rule set: a validation pass regroups by severity (Important first), the
///     "show easier levels" toggle moves the count, the groups wait while the tab is hidden,
///     and an issue jump switches level first and selects through the host. Before the move
///     this was reachable only through source lints — the inspector it lived on could not be
///     built outside a headless session.
/// </summary>
public sealed class InspectorIssuesViewModelTests
{
    [Fact]
    public void ALanguageSwitch_RebuildsTheVisibleGroups_WithoutRerunningValidation()
    {
        var localization = FakeLocalization.English();
        var (_, rules, vm) = Create(localization);
        vm.RecomputeIssues();
        var groups = vm.IssueGroups.ToArray();

        localization.SwitchTo("de-DE");

        Assert.Equal(1, rules.Calls);
        Assert.Equal(groups.Length, vm.IssueGroups.Count);
        Assert.NotSame(groups[0], vm.IssueGroups[0]);
    }

    /// <summary>A released app's view model still hears the process-wide language service through its weak
    /// subscription until it is collected; once the container disposed it, a language change moves no list.</summary>
    [Fact]
    public void ALanguageSwitch_AfterDispose_MovesNoList()
    {
        var localization = FakeLocalization.English();
        var (_, _, vm) = Create(localization);
        vm.RecomputeIssues();
        Assert.NotEmpty(vm.IssueGroups);
        var moves = 0;
        vm.IssueGroups.CollectionChanged += (_, _) => moves++;

        vm.Dispose();
        localization.SwitchTo("de-DE");

        Assert.Equal(0, moves);
    }

    /// <summary>The Issues tab's badge is hidden on zero (it drew as an empty red pill) and shown with a count.</summary>
    [Fact]
    public void TheTabBadge_ShowsOnlyWithACount()
    {
        var (_, _, vm) = Create();
        Assert.False(vm.HasIssues);

        vm.RecomputeIssues();

        Assert.True(vm.HasIssues);
    }

    [Fact]
    public void AValidationPass_GroupsBySeverity_ImportantFirst()
    {
        var (_, rules, vm) = Create();

        vm.RecomputeIssues();

        Assert.Equal(1, rules.Calls);
        Assert.Equal(3, vm.IssueCount);
        Assert.Equal(ArrangementIssueSeverity.Important, vm.TopIssueSeverity);
        Assert.Equal([ArrangementIssueSeverity.Important, ArrangementIssueSeverity.Warning],
            vm.IssueGroups.Select(g => g.Severity));
        Assert.Equal([2, 1], vm.IssueGroups.Select(g => g.Count));
        Assert.Equal(vm.Items.Count, vm.IssueGroups.Sum(g => g.Count));
        Assert.False(vm.IssueGroupsAreStale);
    }

    [Fact]
    public void ShowEasierLevels_TogglesTheCount()
    {
        var (_, rules, vm) = Create();
        vm.RecomputeIssues();

        Assert.Equal(1, vm.EasierLevelIssueCount);
        Assert.True(vm.HasEasierLevelIssues);
        Assert.DoesNotContain(vm.Items, i => i.Message == EasyFinding);

        vm.ShowEasierLevelIssues = true;

        Assert.Equal(2, rules.Calls);
        Assert.Equal(4, vm.IssueCount);
        Assert.Contains(vm.Items, i => i.Message == EasyFinding);
        Assert.Equal(4, vm.IssueGroups.Sum(g => g.Count));

        vm.ShowEasierLevelIssues = false;

        Assert.Equal(3, vm.IssueCount);
        Assert.DoesNotContain(vm.Items, i => i.Message == EasyFinding);
    }

    [Fact]
    public void WhileTheTabIsHidden_TheGroupsGoStale_AndTheHostHearsEveryPass()
    {
        var (_, _, vm) = Create();
        var host = new FakeHost { IsIssuesTabSelected = false };
        vm.AttachHost(host);

        vm.RecomputeIssues();

        Assert.Equal(1, host.Recomputed);
        Assert.Equal(3, vm.IssueCount);
        Assert.Empty(vm.IssueGroups);
        Assert.True(vm.IssueGroupsAreStale);

        vm.EnsureIssueGroupsCurrent();

        Assert.False(vm.IssueGroupsAreStale);
        Assert.Equal(3, vm.IssueGroups.Sum(g => g.Count));

        vm.DetachHost(host);
        vm.RecomputeIssues();
        Assert.Equal(1, host.Recomputed);
    }

    [Fact]
    public void AJump_SwitchesToTheOwningLevel_AndSelectsThroughTheHost()
    {
        var (harness, _, vm) = Create();
        var host = new FakeHost();
        vm.AttachHost(host);
        vm.ShowEasierLevelIssues = true;
        var easy = vm.Items.Single(i => i.Message == EasyFinding);
        var arrangement = harness.Project.Arrangements[0];

        vm.JumpToIssue(easy);

        Assert.Same(arrangement.Levels[0], harness.State.ActiveLevel);
        Assert.Equal(1, host.ProgrammaticSelections);
        Assert.Same(easy.Note, Assert.Single(harness.Editor.Selection.SelectedNotes));
    }

    private const string EasyFinding = "on the easy level";

    [Fact]
    public void AShowlightWarning_AppearsInIssuesAndJumpsToTheShowlight()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        harness.Project.Showlights.Add(new Showlight { Note = 42, Position = new FractionalPosition(3) });

        vm.RecomputeIssues();
        var warnings = Assert.Single(vm.IssueGroups, group => group.Severity == ArrangementIssueSeverity.Warning);
        var finding = Assert.Single(warnings.Issues, issue => issue.MessageKey == "Validation_ShowlightsMissingFog");

        vm.JumpToIssue(finding);

        Assert.Equal(1500, harness.State.CurrentTimeMs);
    }

    [Fact]
    public void ADifficultyLevelWarning_AppearsInIssuesAndJumpsToThePhrase()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        var arrangement = harness.Project.Arrangements[0];
        // "intro" holds the beat-1 note, the same on both levels: one level only.
        arrangement.EventPoints.Add(new EventPoint { Phrase = "intro", Position = new FractionalPosition(1) });
        arrangement.EventPoints.Add(new EventPoint { Phrase = "riff", Position = new FractionalPosition(2) });
        arrangement.PhraseInfos["intro"] = new PhraseInfo(0, false); // authored at max 0: content decides

        vm.RecomputeIssues();
        var warnings = Assert.Single(vm.IssueGroups, group => group.Severity == ArrangementIssueSeverity.Warning);
        var finding = Assert.Single(warnings.Issues, issue => issue.MessageKey == "Validation_PhraseLevelsTooFew");

        vm.JumpToIssue(finding);

        Assert.Equal(500, harness.State.CurrentTimeMs);
    }

    [Fact]
    public void AHandShapeGapWarning_JumpsToTheShapesEndAndSelectsTheShape()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        var hard = harness.Project.Arrangements[0].Levels[1];
        var shape = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(1) };
        hard.HandShapes.Add(shape);
        hard.HandShapes.Add(new HandShape { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3) });

        vm.RecomputeIssues();
        var warnings = Assert.Single(vm.IssueGroups, group => group.Severity == ArrangementIssueSeverity.Warning);
        var finding = Assert.Single(warnings.Issues, issue => issue.MessageKey == "Validation_HandShapeGap");

        vm.JumpToIssue(finding);

        Assert.Equal(500, harness.State.CurrentTimeMs);
        Assert.Same(shape, Assert.Single(harness.Editor.Selection.SelectedHandShapes));
    }

    /// <summary>An anchor finding also names the shape around the anchor, as context. Selecting
    /// that shape would let Delete remove it, so the jump leaves it unselected.</summary>
    [Fact]
    public void AnAnchorInsideAShapeWarning_DoesNotSelectTheSurroundingShape()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        var hard = harness.Project.Arrangements[0].Levels[1];
        hard.HandShapes.Add(new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2) });
        hard.Anchors.Add(new FretAnchor(new FractionalPosition(1), 3));

        vm.RecomputeIssues();
        var finding = Assert.Single(vm.Items, issue => issue.MessageKey == "Validation_AnchorInsideHandShape");
        Assert.NotNull(finding.HandShape);

        vm.JumpToIssue(finding);

        Assert.Empty(harness.Editor.Selection.SelectedHandShapes);
    }

    [Fact]
    public void ASectionNameWarning_AppearsInIssuesAndJumpsToTheFirstDominantSection()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        var arrangement = harness.Project.Arrangements[0];
        arrangement.EventPoints.Clear();
        string[] names = ["verse", "intro", "intro", "intro", "intro", "intro"];
        for (var i = 0; i < names.Length; i++)
            arrangement.EventPoints.Add(new EventPoint { Phrase = "riff", Section = names[i], Position = new FractionalPosition(i + 1) });

        vm.RecomputeIssues();
        var warnings = Assert.Single(vm.IssueGroups, group => group.Severity == ArrangementIssueSeverity.Warning);
        var finding = Assert.Single(warnings.Issues, issue => issue.MessageKey == "Validation_SectionNamesNotVaried");

        vm.JumpToIssue(finding);

        Assert.Equal(new FractionalPosition(2).GetPositionMs(harness.Project.Beats), harness.State.CurrentTimeMs);
    }

    [Fact]
    public void ALyricStyleWarning_ActivatesItsVocalTrackAndSelectsTheSyllable()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        var (first, _) = AddVocals(harness, "Hold", "on");
        var (second, syllable) = AddVocals(harness, "Hold", "on to");
        harness.Editor.ActiveVocalTrack = first;

        vm.RecomputeIssues();
        var warnings = Assert.Single(vm.IssueGroups, group => group.Severity == ArrangementIssueSeverity.Warning);
        var finding = Assert.Single(warnings.Issues, issue => issue.MessageKey == "Validation_LyricUnsplitSyllable");

        vm.JumpToIssue(finding);

        Assert.Equal(new FractionalPosition(3).GetPositionMs(harness.Project.Beats), harness.State.CurrentTimeMs);
        Assert.Same(second, harness.Editor.ActiveVocalTrack);
        Assert.Same(syllable, harness.Editor.Selection.SelectedLyric);
    }

    [Fact]
    public void ALyricStyleWarning_IsListedWithNoInstrumentArrangementActive()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        AddVocals(harness, "Hold", "on to");
        harness.State.ActiveArrangement = null;

        vm.RecomputeIssues();

        var finding = Assert.Single(vm.IssueGroups.SelectMany(group => group.Issues));
        Assert.Equal("Validation_LyricUnsplitSyllable", finding.MessageKey);
    }

    [Fact]
    public void AToneChangeOnANote_OffersTheSixteenthEarlierFix_WhichClearsTheFinding()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        var arrangement = harness.Project.Arrangements[0];
        arrangement.ToneBase = "clean";
        arrangement.ToneA = "lead";
        arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "lead" });

        vm.RecomputeIssues();
        var finding = Assert.Single(vm.IssueGroups.SelectMany(group => group.Issues),
            issue => issue.Type == ArrangementIssueType.ToneChangeOnNote);
        vm.JumpToIssue(finding);

        Assert.True(vm.IssueFix.HasFix);
        Assert.False(vm.IssueFix.IsBlocked);
        Assert.Equal("Inspector_IssueFix_ToneEarlier", vm.IssueFix.DefaultLabel);
        Assert.True(vm.IssueFix.ApplyDefaultCommand.CanExecute(null));

        vm.IssueFix.ApplyDefaultCommand.Execute(null);
        vm.RecomputeIssues();

        Assert.Equal(new FractionalPosition(1, new Fraction(3, 4)), arrangement.ToneChanges[0].Position);
        Assert.DoesNotContain(vm.IssueGroups.SelectMany(group => group.Issues),
            issue => issue.Type == ArrangementIssueType.ToneChangeOnNote);
        Assert.False(vm.IssueFix.HasFix);
    }

    /// <summary>
    ///     Inspector design D puts a Fix on every finding row a rule can repair: one click jumps to the finding and
    ///     applies its default fix (one undo step), here the tone change "a 16th note earlier".
    /// </summary>
    [Fact]
    public void ARowsFix_JumpsToTheFinding_AndAppliesItsDefaultFix()
    {
        var (harness, _, vm) = Create(validation: new ArrangementValidationService(new FakeAppLogger()));
        var arrangement = harness.Project.Arrangements[0];
        arrangement.ToneBase = "clean";
        arrangement.ToneA = "lead";
        arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "lead" });

        vm.RecomputeIssues();
        var finding = Assert.Single(vm.IssueGroups.SelectMany(group => group.Issues),
            issue => issue.Type == ArrangementIssueType.ToneChangeOnNote);
        Assert.True(vm.Rows.Items.Single(row => ReferenceEquals(row.Issue, finding)).HasFix);

        vm.FixIssueCommand.Execute(finding);

        Assert.Same(finding, vm.IssueAi.Issue);
        Assert.Equal(new FractionalPosition(1, new Fraction(3, 4)), arrangement.ToneChanges[0].Position);
        vm.FixIssueCommand.Execute(null);
    }

    /// <summary>
    ///     A row's Fix asks the fix menu's own rule check, not a copy of its rule list: a grip finding whose anchor has
    ///     nothing to repair (no chord template, so <c>IndexAnchorRepair.For</c> finds none) draws no Fix.
    /// </summary>
    [Fact]
    public void ARowsFix_ShowsOnlyWhenTheFixMenuHasAFix()
    {
        var harness = new ChartEditHarness();
        var arrangement = harness.Project.Arrangements[0];
        var level = arrangement.Levels[0];
        var anchor = new FretAnchor { Position = new FractionalPosition(1), Fret = 5 };
        level.Anchors.Add(anchor);

        Assert.False(IssueFixViewModel.CanOffer(null, arrangement, level, harness.State.Beats));
        Assert.False(IssueFixViewModel.CanOffer(new ArrangementIssue(ArrangementIssueType.ToneChangeOnNote, "m"), arrangement, level, harness.State.Beats));
        Assert.False(IssueFixViewModel.CanOffer(new ArrangementIssue(ArrangementIssueType.IndexFingerNotOnAnchorFret, "m"), arrangement, level, harness.State.Beats));
        Assert.False(IssueFixViewModel.CanOffer(new ArrangementIssue(ArrangementIssueType.IndexFingerNotOnAnchorFret, "m", 1000, Anchor: anchor),
            arrangement, level, harness.State.Beats));
    }

    /// <summary>Design D's Difficulty filter lists one level's findings (plus the arrangement-wide ones) and each row names its level.</summary>
    [Fact]
    public void TheDifficultyFilter_ListsOneLevel_AndEachRowNamesItsLevel()
    {
        var (_, _, vm) = Create();
        vm.ShowEasierLevelIssues = true;
        Assert.Equal(4, vm.Rows.Items.Count);
        Assert.True(vm.Rows.HasLevelChoices);
        Assert.Equal([-1, 0, 1], vm.Rows.LevelChoices.Select(c => c.Index));
        Assert.Equal(3, vm.Rows.Items.Count(row => row.HasLevel));

        vm.Rows.SelectedLevel = vm.Rows.LevelChoices[1];

        Assert.Equal([EasyFinding, "the whole arrangement"], vm.Rows.Items.Select(row => row.Issue.Message).Order());
        vm.Rows.SelectedLevel = vm.Rows.LevelChoices[0];
        Assert.Equal(4, vm.Rows.Items.Count);
    }

    /// <summary>The row of the finding on the Fixes card lights its spine; no other row does.</summary>
    [Fact]
    public void AJump_LightsOnlyThatRowsSpine()
    {
        var (_, _, vm) = Create();
        vm.RecomputeIssues();
        Assert.DoesNotContain(vm.Rows.Items, row => row.IsOnFixesCard);

        var target = vm.Rows.Items[1];
        vm.JumpToIssueCommand.Execute(target.Issue);

        Assert.Same(target, Assert.Single(vm.Rows.Items, row => row.IsOnFixesCard));
    }

    /// <summary>Design D's Auto-fix asks "Auto-fix N visible arrangement findings?" first; a history step withdraws the question.</summary>
    [Fact]
    public void AutoFix_AsksFirst_AndRunsOnlyOnConfirm()
    {
        var (harness, _, vm) = Create();
        vm.RecomputeIssues();

        vm.AutoFixIssuesCommand.Execute(null);

        Assert.True(vm.AutoFixConfirm.IsPending);
        Assert.Contains("Inspector_Issues_AutoFixAsk", vm.AutoFixConfirm.Sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoFixChart()", _lastMaintenance!.Calls);
        vm.AutoFixConfirm.ConfirmCommand.Execute(null);
        Assert.Single(_lastMaintenance.Calls, "AutoFixChart()");
        Assert.False(vm.AutoFixConfirm.IsPending);

        vm.AutoFixIssuesCommand.Execute(null);
        var notes = harness.Project.Arrangements[0].Levels[1].Notes;
        harness.History.Do(new MutateNotesAction(notes, [notes[0]], n => n.Fret = 7));
        Assert.False(vm.AutoFixConfirm.IsPending);
    }

    private static (DotCharter.Core.Domain.Vocals.VocalTrack Track, DotCharter.Core.Domain.Vocals.LyricEvent Last) AddVocals(
        ChartEditHarness harness, string first, string last)
    {
        var phrase = new DotCharter.Core.Domain.Vocals.LyricPhrase { Position = new FractionalPosition(2) };
        phrase.Lyrics.Add(new DotCharter.Core.Domain.Vocals.LyricEvent { Text = first, Position = new FractionalPosition(2) });
        var syllable = new DotCharter.Core.Domain.Vocals.LyricEvent
        {
            Text = last, Position = new FractionalPosition(3), IsPhraseEnd = true
        };
        phrase.Lyrics.Add(syllable);
        var track = new DotCharter.Core.Domain.Vocals.VocalTrack();
        track.Phrases.Add(phrase);
        harness.Project.VocalTracks.Add(track);
        return (track, syllable);
    }

    /// <summary>Two levels, the hardest on screen. The rules answer four findings: two Important
    /// and one Warning on the hardest level's notes or the whole arrangement, and one Warning on
    /// the easy level, which the list sets aside until asked.</summary>
    private static (ChartEditHarness Harness, FakeRules Rules, InspectorIssuesViewModel Vm) Create(
        DotCharter.Localization.ILocalizationService? localization = null,
        IArrangementValidationService? validation = null)
    {
        var harness = new ChartEditHarness();
        var arrangement = harness.Project.Arrangements[0];
        var easy = arrangement.Levels[0];
        easy.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        var hard = new ChartLevel { DifficultyIndex = 1 };
        hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 5 });
        arrangement.Levels.Add(hard);
        harness.State.ActiveLevel = hard;

        var rules = new FakeRules();
        var (maintenance, recorder) = RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>();
        _lastMaintenance = recorder;
        var vm = new InspectorIssuesViewModel(harness.State, harness.Editor, validation ?? rules, maintenance,
            new ExportReadinessViewModel(harness.State, new CheckpointsViewModelTests.FakeDocumentService(),
                harness.Transport, harness.Editor, FakeLocalization.KeysOnly(),
                RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger(),
                new DotCharter.Services.Editing.Quality.ChartQualityReporter(new DotCharter.Services.Editing.Validation.ArrangementValidationService(new FakeAppLogger())),
                new DotCharter.Tests.Infrastructure.TestUiDispatcher()),
            harness.History, localization ?? FakeLocalization.KeysOnly(), () => null, new GatedInlineDispatcher(), new FakeAppLogger())
        {
            // inline: the pass runs and lands before RecomputeIssues returns
            OffThread = work => Task.FromResult(work())
        };
        return (harness, rules, vm);
    }

    /// <summary>The maintenance service of the last <see cref="Create" />: what Auto-fix ran.</summary>
    [ThreadStatic] private static RecordingProxy? _lastMaintenance;

    /// <summary>The rules run over a COPY of the chart, so the findings name the copy's notes.</summary>
    private sealed class FakeRules : IArrangementValidationService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ArrangementIssue> Validate(Arrangement? arrangement, IList<BeatMarker> beats,
            double? totalAudioLengthMs = null, ChartProject? project = null)
        {
            Calls++;
            var onEasy = arrangement!.Levels[0].Notes[0];
            var onHard = arrangement.Levels[1].Notes[0];
            var alsoOnHard = arrangement.Levels[1].Notes[1];
            return
            [
                // Severity is derived from the type: NoEndPhrase is a Warning, FirstPhraseNotEmpty Important.
                new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "warning on the hard level", 500, Note: onHard),
                new ArrangementIssue(ArrangementIssueType.NoEndPhrase, EasyFinding, 500, Note: onEasy),
                new ArrangementIssue(ArrangementIssueType.FirstPhraseNotEmpty, "important on the hard level", 1000,
                    Note: alsoOnHard),
                new ArrangementIssue(ArrangementIssueType.FirstPhraseNotEmpty, "the whole arrangement")
            ];
        }
    }

    private sealed class FakeHost : IInspectorIssuesHost
    {
        public bool IsIssuesTabSelected { get; init; } = true;

        public int Recomputed { get; private set; }

        public int ProgrammaticSelections { get; private set; }

        public void OnIssuesRecomputed() => Recomputed++;

        public void SelectProgrammatically(Action select)
        {
            ProgrammaticSelections++;
            select();
        }
    }
}
