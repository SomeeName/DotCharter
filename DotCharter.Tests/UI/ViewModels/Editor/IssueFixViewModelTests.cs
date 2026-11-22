using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #618: the Issues tab's fix menu for the finding last jumped to. An E04 whose grip the
///     arrangement plays the other way under the same anchor offers re-fingering FIRST and the
///     anchor move second; each press is one undo step, and the offer is withdrawn once the
///     finding is gone.
/// </summary>
public sealed class IssueFixViewModelTests
{
    /// <summary>FHP 2; a double stop fingered 2-3 under a hand shape of the same grip, and (with
    ///     <paramref name="evidence" />) the same frets fingered 1-3 later under that anchor.</summary>
    private static (Arrangement Arr, ChartLevel Level, Chord Flagged, FretAnchor Anchor) Build(bool evidence = true)
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);
        foreach (var (id, index) in new[] { (0, 2), (1, 1) })
        {
            arr.ChordTemplates.Add(new ChordTemplate
            {
                Id = id,
                Frets = new Dictionary<int, int> { [3] = 2, [4] = 3 },
                Fingers = new Dictionary<int, int> { [3] = index, [4] = 3 }
            });
        }

        var anchor = new FretAnchor(new FractionalPosition(0), 2, 4);
        level.Anchors.Add(anchor);
        var flagged = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        level.Chords.Add(flagged);
        level.HandShapes.Add(new HandShape
        {
            TemplateId = 0, Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3)
        });
        if (evidence) level.Chords.Add(new Chord { Position = new FractionalPosition(6), TemplateId = 1 });
        return (arr, level, flagged, anchor);
    }

    private static ArrangementIssue E04(Chord chord, FretAnchor anchor) =>
        new(ArrangementIssueType.IndexFingerNotOnAnchorFret, "E04", 0, Chord: chord, Anchor: anchor);

    [Fact]
    public void AnAttestedGrip_IsOfferedFirst_AndTheAnchorMoveSecond()
    {
        var (arr, level, flagged, anchor) = Build();
        var vm = new IssueFixViewModel(new HistoryService(new FakeAppLogger()), FakeLocalization.English());

        vm.Offer(E04(flagged, anchor), arr, level);

        var loc = FakeLocalization.English();
        Assert.True(vm.HasFix);
        Assert.True(vm.HasAlternative);
        Assert.Equal(loc.Format("Inspector_IssueFix_Title", "E04"), vm.Title);
        Assert.Equal(loc.Format("Inspector_IssueFix_RefingerOne", 1, "1-3"), vm.DefaultLabel);
        Assert.Equal(loc.Format("Inspector_IssueFix_MoveAnchor", 1), vm.AlternativeLabel);
    }

    [Fact]
    public void WithNoEvidence_MovingTheAnchorIsTheOnlyFix()
    {
        var (arr, level, flagged, anchor) = Build(evidence: false);
        var vm = new IssueFixViewModel(new HistoryService(new FakeAppLogger()), FakeLocalization.English());

        vm.Offer(E04(flagged, anchor), arr, level);

        Assert.True(vm.HasFix);
        Assert.False(vm.HasAlternative);
        Assert.Equal(FakeLocalization.English().Format("Inspector_IssueFix_MoveAnchor", 1), vm.DefaultLabel);
    }

    [Fact]
    public void ApplyingTheDefault_IsOneUndoStep_AndWithdrawsTheOffer()
    {
        var (arr, level, flagged, anchor) = Build();
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());
        vm.Offer(E04(flagged, anchor), arr, level);

        vm.ApplyDefaultCommand.Execute(null);

        // The chord and the hand shape over it are rebound together, in ONE history node.
        Assert.Equal(1, flagged.TemplateId);
        Assert.Equal(1, level.HandShapes[0].TemplateId);
        Assert.Equal(2, anchor.Fret);
        Assert.False(vm.HasFix);
        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(0, flagged.TemplateId);
        Assert.Equal(0, level.HandShapes[0].TemplateId);
    }

    [Fact]
    public void TheAlternative_MovesTheAnchor()
    {
        var (arr, level, flagged, anchor) = Build();
        var vm = new IssueFixViewModel(new HistoryService(new FakeAppLogger()), FakeLocalization.English());
        vm.Offer(E04(flagged, anchor), arr, level);

        vm.ApplyAlternativeCommand.Execute(null);

        Assert.Equal(1, anchor.Fret);
        Assert.Equal(0, flagged.TemplateId);
    }

    [Fact]
    public void ARecomputeWithoutTheFinding_WithdrawsTheOffer_AndOneWithItKeepsIt()
    {
        var (arr, level, flagged, anchor) = Build();
        var vm = new IssueFixViewModel(new HistoryService(new FakeAppLogger()), FakeLocalization.English());
        vm.Offer(E04(flagged, anchor), arr, level);

        vm.Refresh([E04(flagged, anchor)], arr);
        Assert.True(vm.HasFix);

        vm.Refresh([], arr);
        Assert.False(vm.HasFix);
    }

    [Fact]
    public void AnotherRule_HasNoFix()
    {
        var (arr, level, flagged, anchor) = Build();
        var vm = new IssueFixViewModel(new HistoryService(new FakeAppLogger()), FakeLocalization.English());

        vm.Offer(new ArrangementIssue(ArrangementIssueType.ChordOutsideHandShape, "D11", 0, Chord: flagged, Anchor: anchor),
            arr, level);

        Assert.False(vm.HasFix);
    }

    private static (Arrangement Arr, List<BeatMarker> Beats, ArrangementIssue I30) ToneOnNote(FractionalPosition at)
    {
        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
        var arr = new Arrangement();
        arr.Levels.Add(new ChartLevel());
        var note = new GuitarNote { Position = at, Fret = 5 };
        arr.Levels[0].Notes.Add(note);
        arr.ToneChanges.Add(new ToneChange { Position = at, ToneName = "lead" });
        return (arr, beats, new ArrangementIssue(ArrangementIssueType.ToneChangeOnNote, "I30", at.GetPositionMs(beats), Note: note));
    }

    [Fact]
    public void AToneChangeOnANote_OffersOneMoveASixteenthEarlier_AsOneUndoStep()
    {
        var (arr, beats, i30) = ToneOnNote(new FractionalPosition(2));
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());

        vm.Offer(i30, arr, arr.Levels[0], beats);

        var loc = FakeLocalization.English();
        Assert.True(vm.HasFix);
        Assert.False(vm.HasAlternative);
        Assert.False(vm.IsBlocked);
        Assert.Equal(loc["Inspector_IssueFix_ToneEarlier"], vm.DefaultLabel);
        Assert.Equal(loc["Inspector_IssueFix_ToneEarlierTip"], vm.DefaultTip);

        vm.ApplyDefaultCommand.Execute(null);

        Assert.Equal(new FractionalPosition(1, new Fraction(3, 4)), arr.ToneChanges[0].Position);
        Assert.False(vm.HasFix);
        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(new FractionalPosition(2), arr.ToneChanges[0].Position);
    }

    [Fact]
    public void ABlockedToneMove_IsShownDisabled_WithItsReason()
    {
        var (arr, beats, i30) = ToneOnNote(new FractionalPosition(0, new Fraction(1, 8)));
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());

        vm.Offer(i30, arr, arr.Levels[0], beats);

        Assert.True(vm.HasFix);
        Assert.True(vm.IsBlocked);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_ToneBlockedChartStart"], vm.BlockedReason);
        Assert.False(vm.ApplyDefaultCommand.CanExecute(null));
        vm.ApplyDefaultCommand.Execute(null);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void AToneFindingWithoutABeatGrid_HasNoFix()
    {
        var (arr, _, i30) = ToneOnNote(new FractionalPosition(2));
        var vm = new IssueFixViewModel(new HistoryService(new FakeAppLogger()), FakeLocalization.English());

        vm.Offer(i30, arr, arr.Levels[0]);

        Assert.False(vm.HasFix);
    }

    [Fact]
    public void AToneChangeRestoredAtTheLandingSpotAfterTheOffer_IsNotStacked()
    {
        // The offer was computed with the landing spot free; an undo then puts a change there before
        // the debounced re-validation runs. The press re-checks the live arrangement and refuses.
        var (arr, beats, i30) = ToneOnNote(new FractionalPosition(2));
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());
        vm.Offer(i30, arr, arr.Levels[0], beats);
        Assert.True(vm.ApplyDefaultCommand.CanExecute(null));

        arr.ToneChanges.Insert(0, new ToneChange { Position = new FractionalPosition(1, new Fraction(3, 4)), ToneName = "clean" });
        vm.ApplyDefaultCommand.Execute(null);

        Assert.False(history.CanUndo);
        Assert.Equal(new FractionalPosition(2), arr.ToneChanges[1].Position);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_ToneBlockedToneChange"], vm.BlockedReason);
        Assert.False(vm.ApplyDefaultCommand.CanExecute(null));
    }

    [Fact]
    public void TwoToneChangesOnTheNote_ShowTheFixDisabled_WithItsReason()
    {
        var (arr, beats, i30) = ToneOnNote(new FractionalPosition(2));
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "clean" });
        var vm = new IssueFixViewModel(new HistoryService(new FakeAppLogger()), FakeLocalization.English());

        vm.Offer(i30, arr, arr.Levels[0], beats);

        Assert.True(vm.IsBlocked);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_ToneBlockedSeveralChanges"], vm.BlockedReason);
    }
}
