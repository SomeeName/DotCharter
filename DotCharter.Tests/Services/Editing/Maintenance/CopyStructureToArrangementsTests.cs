using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     Copying a song's STRUCTURE — its section and phrase markers — from the
///     arrangement being charted onto the others.
///     <para>
///         Structure is a property of the SONG, not of one arrangement: the
///         chorus starts where the chorus starts whether you are looking at Lead
///         or Bass. The event dialog could already fan a marker out to several
///         arrangements, but only if the box was ticked at the time — and a
///         charter who works the whole song on Lead first had no way back. The
///         others ended up empty, and RS-XML needs phrases on all of them.
///     </para>
/// </summary>
public sealed class CopyStructureToArrangementsTests
{
    [Fact]
    public void MarkersTheTargetLacks_AreAdded()
    {
        var source = ArrangementWith(("intro", "intro", 0), ("verse", "verse", 8));
        var target = new Arrangement { Name = "Rhythm" };

        var written = ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.Equal(2, written);
        Assert.Equal(2, target.EventPoints.Count);
        Assert.Equal("intro", target.EventPoints[0].Section);
        Assert.Equal(new FractionalPosition(8), target.EventPoints[1].Position);
    }

    [Fact]
    public void AMarkerTheTargetAlreadyHasAtThatPosition_IsOverwritten()
    {
        var source = ArrangementWith(("chorus", "chorus", 4));
        var target = ArrangementWith(("verse", "verse", 4));

        var written = ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.Equal(1, written);
        var only = Assert.Single(target.EventPoints);
        Assert.Equal("chorus", only.Section);
        Assert.Equal("chorus", only.Phrase);
    }

    /// <summary>
    ///     MERGE, not replace: a marker the target has and the source does not is
    ///     the target's own work and survives. That is what makes running this
    ///     after the other arrangements already have structure safe.
    /// </summary>
    [Fact]
    public void AMarkerOnlyTheTargetHas_IsLeftAlone()
    {
        var source = ArrangementWith(("intro", "intro", 0));
        var target = ArrangementWith(("solo", "solo", 16));

        ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.Equal(2, target.EventPoints.Count);
        Assert.Contains(target.EventPoints, e => e.Section == "solo");
    }

    [Fact]
    public void RunningItTwice_ChangesNothingTheSecondTime()
    {
        var source = ArrangementWith(("intro", "intro", 0), ("verse", "verse", 8));
        var target = new Arrangement { Name = "Rhythm" };

        Assert.Equal(2, ChartMaintenanceService.MergeStructureInto(source, target));
        Assert.Equal(0, ChartMaintenanceService.MergeStructureInto(source, target));
        Assert.Equal(2, target.EventPoints.Count);
    }

    [Fact]
    public void TheMarkersLandInPositionOrder()
    {
        var source = ArrangementWith(("verse", "verse", 8), ("intro", "intro", 0));
        var target = ArrangementWith(("bridge", "bridge", 4));

        ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.Equal(
            target.EventPoints.OrderBy(e => e.Position).Select(e => e.Position).ToList(),
            target.EventPoints.Select(e => e.Position).ToList());
    }

    [Fact]
    public void EventCodesAndSectionStatusTravelWithTheMarker()
    {
        var source = ArrangementWith(("chorus", "chorus", 4));
        source.EventPoints[0].Events = "B0,B1";
        source.EventPoints[0].Status = SectionStatus.Done;
        var target = new Arrangement { Name = "Bass" };

        ChartMaintenanceService.MergeStructureInto(source, target);

        var copied = Assert.Single(target.EventPoints);
        Assert.Equal("B0,B1", copied.Events);
        Assert.Equal(SectionStatus.Done, copied.Status);
    }

    /// <summary>
    ///     <c>PhraseInfo.Solo</c> is keyed by phrase NAME, so it has to follow
    ///     the phrases that were copied — otherwise a solo phrase silently stops
    ///     being one on every target.
    /// </summary>
    [Fact]
    public void TheSoloFlagFollowsThePhraseItBelongsTo()
    {
        var source = ArrangementWith(("solo", "solo", 12));
        source.PhraseInfos["solo"] = new PhraseInfo(0, true);
        var target = new Arrangement { Name = "Rhythm" };

        ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.True(target.PhraseInfos.TryGetValue("solo", out var info));
        Assert.True(info!.Solo);
    }

    /// <summary>
    ///     …but ONLY the solo flag. <c>PhraseInfo.MaxDifficulty</c> is a fact
    ///     about the arrangement's own level stack, not about the phrase: a lead
    ///     with eight generated levels copied its structure onto a single-level
    ///     rhythm and left every phrase there claiming a ceiling of 7, which
    ///     <c>LiveNoteMatcher</c>, the squash and the RS export all read back.
    /// </summary>
    [Fact]
    public void TheSourcesDifficultyCeiling_DoesNotFollowThePhraseOntoAThinnerTarget()
    {
        var source = ArrangementWith(("verse", "verse", 4));
        source.PhraseInfos["verse"] = new PhraseInfo(7, false);
        var target = new Arrangement { Name = "Rhythm" };
        target.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

        ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.Equal(0, target.PhraseInfos["verse"].MaxDifficulty);
    }

    /// <summary>A target that already knows its own ceiling keeps it — the copy
    /// carries the solo flag onto the existing record rather than replacing it
    /// with the source's numbers.</summary>
    [Fact]
    public void ATargetsOwnCeiling_SurvivesTheCopy_WhileTheSoloFlagStillArrives()
    {
        var source = ArrangementWith(("solo", "solo", 12));
        source.PhraseInfos["solo"] = new PhraseInfo(7, true);
        var target = new Arrangement { Name = "Rhythm" };
        target.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        target.Levels.Add(new ChartLevel { DifficultyIndex = 1 });
        target.PhraseInfos["solo"] = new PhraseInfo(1, false);

        ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.Equal(1, target.PhraseInfos["solo"].MaxDifficulty);
        Assert.True(target.PhraseInfos["solo"].Solo);
    }

    [Fact]
    public void APhraselessMarker_CarriesNoPhraseInfo()
    {
        var source = ArrangementWith(("chorus", "", 4));
        var target = new Arrangement { Name = "Rhythm" };

        ChartMaintenanceService.MergeStructureInto(source, target);

        Assert.Empty(target.PhraseInfos);
    }

    private static Arrangement ArrangementWith(params (string Section, string Phrase, int Beat)[] markers)
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        foreach (var (section, phrase, beat) in markers)
        {
            arrangement.EventPoints.Add(new EventPoint
            {
                Position = new FractionalPosition(beat), Section = section, Phrase = phrase
            });
        }

        return arrangement;
    }
}
