using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Events;
using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Stamping the last section again without opening the dialog.
///     <para>
///         Sections repeat hard: the owner's 2026-07-27 session placed 44 markers
///         of which 18 were 'chorus', and the hand-finished corpus is 31.8%
///         chorus and 19.1% verse. Everything after the first of a repeat is the
///         same five-second dialog round trip for a value the editor already knew.
///     </para>
/// </summary>
public sealed class SectionStampTests
{
    private static (EventEditingService Service, Arrangement Arrangement, IHistoryService History) NewService()
    {
        var state = new ProjectState();
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);

        var project = new ChartProject();
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = arrangement.Levels[0];

        var service = new EventEditingService(state, new SelectionService(), history,
            new EditingScope(state), logger);
        return (service, arrangement, history);
    }

    private static FractionalPosition At(int beat) => new(beat);

    /// <summary>
    ///     The rest of what the marker dialog asked, remembered alongside the
    ///     section: which RS event codes, and which ARRANGEMENTS.
    ///     <para>
    ///         Placing structure is a run, not a set of unrelated decisions — a
    ///         chorus is not four bars long, so the same section goes down over
    ///         and over on the same set of arrangements until the song changes.
    ///         Re-ticking all of it at every marker is the work this removes.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheEventCodesAndArrangementSet_AreRememberedToo()
    {
        var (service, _, _) = NewService();

        service.RememberLastTargets("B0,B1", ["Lead", "Rhythm"]);

        Assert.Equal("B0,B1", service.LastEvents);
        Assert.Equal(["Lead", "Rhythm"], service.LastArrangementNames);
    }

    /// <summary>
    ///     Unlike <c>RememberLastMarker</c>, this has no section-only guard: a
    ///     phrase-only marker (END, COUNT) is still placed on a chosen SET of
    ///     arrangements, and that set is exactly what should not have to be
    ///     re-ticked.
    /// </summary>
    [Fact]
    public void APhraseOnlyMarkersArrangementSet_IsStillRemembered()
    {
        var (service, _, _) = NewService();

        service.RememberLastMarker("", "END");
        service.RememberLastTargets("", ["Lead", "Rhythm", "Bass"]);

        Assert.Equal(string.Empty, service.LastSection); // the section guard still holds
        Assert.Equal(3, service.LastArrangementNames.Count);
    }

    [Fact]
    public void NothingIsRememberedBeforeAMarkerHasBeenSaved()
    {
        var (service, _, _) = NewService();

        Assert.Equal(string.Empty, service.LastEvents);
        Assert.Empty(service.LastArrangementNames);
    }

    [Fact]
    public void ABlankArrangementName_IsNotRemembered()
    {
        var (service, _, _) = NewService();

        service.RememberLastTargets("", ["Lead", "  ", ""]);

        Assert.Equal(["Lead"], service.LastArrangementNames);
    }

    [Fact]
    public void NothingIsStampedBeforeAMarkerHasBeenSaved()
    {
        var (service, arrangement, _) = NewService();

        Assert.False(service.StampLastMarker(At(8)));
        Assert.Empty(arrangement.EventPoints);
    }

    [Fact]
    public void TheStampRepeatsTheLastSectionAndItsPhrase()
    {
        var (service, arrangement, _) = NewService();
        service.RememberLastMarker("chorus", "chorus");

        Assert.True(service.StampLastMarker(At(8)));

        var marker = Assert.Single(arrangement.EventPoints);
        Assert.Equal("chorus", marker.Section);
        Assert.Equal("chorus", marker.Phrase);
        Assert.Equal(At(8), marker.Position);
    }

    /// <summary>
    ///     A phrase-only marker is a ONE-OFF — COUNT, END, a DD move marker —
    ///     and stamping one again would put a second copy of a unique phrase in
    ///     the chart. Only a marker that names a section is worth repeating.
    /// </summary>
    [Fact]
    public void APhraseOnlyMarkerIsNeverRemembered()
    {
        var (service, arrangement, _) = NewService();
        service.RememberLastMarker("chorus", "chorus");
        service.RememberLastMarker("", "END");

        Assert.True(service.StampLastMarker(At(8)));
        Assert.Equal("chorus", Assert.Single(arrangement.EventPoints).Section);
    }

    /// <summary>
    ///     Merged onto a marker already at the position rather than stacked
    ///     beside it — two markers at one instant are two things the exporter then
    ///     has to reconcile.
    /// </summary>
    [Fact]
    public void StampingOverAnExistingMarkerReplacesItRatherThanAddingASecond()
    {
        var (service, arrangement, _) = NewService();
        arrangement.EventPoints.Add(new EventPoint { Position = At(8), Section = "verse", Phrase = "verse" });
        service.RememberLastMarker("chorus", "chorus");

        Assert.True(service.StampLastMarker(At(8)));

        var marker = Assert.Single(arrangement.EventPoints);
        Assert.Equal("chorus", marker.Section);
    }

    [Fact]
    public void StampingWhatIsAlreadyThereChangesNothing()
    {
        var (service, arrangement, history) = NewService();
        arrangement.EventPoints.Add(new EventPoint { Position = At(8), Section = "chorus", Phrase = "chorus" });
        service.RememberLastMarker("chorus", "chorus");

        Assert.False(service.StampLastMarker(At(8)));
        Assert.Single(arrangement.EventPoints);
        Assert.False(history.CanUndo);
    }

    /// <summary>One stamp is one undo step — including the replace, which is two
    ///     actions internally.</summary>
    [Fact]
    public void EachStampIsExactlyOneUndoStep()
    {
        var (service, arrangement, history) = NewService();
        arrangement.EventPoints.Add(new EventPoint { Position = At(8), Section = "verse", Phrase = "verse" });
        service.RememberLastMarker("chorus", "chorus");

        service.StampLastMarker(At(8));
        Assert.Equal("chorus", Assert.Single(arrangement.EventPoints).Section);

        history.Undo();
        Assert.Equal("verse", Assert.Single(arrangement.EventPoints).Section);
    }
}
