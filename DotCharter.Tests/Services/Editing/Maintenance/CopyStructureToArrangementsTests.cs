using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Parsing.Xml.Song;

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
    public void GeneratingStructureKeepsMainXmlBytesWhenAnEditorMarkerHidesImportedEnd()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var arrangement = h.State.ActiveArrangement!;
        arrangement.Levels[0].Notes.Clear();
        arrangement.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(8), Sustain = new FractionalPosition(1), Fret = 5
        });
        arrangement.Phrases.Add("COUNT");
        arrangement.Phrases.Add("END");
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(12), 1));
        var before = ChartProjectCloner.Clone(project, retainIdentities: true);
        var marker = new EventPoint { Position = new FractionalPosition(12), Phrase = "outro" };
        Assert.True(h.History.Do(new AddEventPointsAction(arrangement.EventPoints, [marker])));
        var edited = ChartProjectCloner.Clone(project, retainIdentities: true);

        // Main's name-table check makes generation a no-op for this chart, so its
        // unchanged export is the reference. Normalize only the conversion stamp.
        var mainBytes = StructureXmlBytes(project);
        var generator = new DotCharter.Services.Editing.Structure.StructureGenerationService(h.State, h.History, new FakeAppLogger());
        var result = generator.Generate();
        var exported = StableStructureXml(project);
        var song = exported.Root!;
        var names = song.Element("phrases")!.Elements("phrase")
            .Select(phrase => (string)phrase.Attribute("name")!).ToList();
        var end = Assert.Single(song.Element("phraseIterations")!.Elements("phraseIteration"),
            iteration => names[(int)iteration.Attribute("phraseId")!] == "END");

        Assert.Equal("6.000", (string)end.Attribute("time")!);
        Assert.Equal(mainBytes, StructureXmlBytes(project));
        Assert.True(result.IsEmpty);
        Assert.Single(h.History.GetUndoSteps());
        Assert.Empty(DeepCompare.Diff("generation", edited, project, new HashSet<string>()));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            h.History.Undo();
            Assert.Empty(DeepCompare.Diff("undo authoring", before, project, new HashSet<string>()));
            h.History.Redo();
            Assert.Empty(DeepCompare.Diff("redo authoring", edited, project, new HashSet<string>()));
            Assert.Equal(mainBytes, StructureXmlBytes(project));
        }
    }

    private static XDocument StableStructureXml(ChartProject project)
    {
        var document = new RsXmlSongWriter().Write(project, 15000);
        document.Root!.Element("lastConversionDateTime")!.Value = "2020-01-02 03:04";
        return document;
    }

    private static byte[] StructureXmlBytes(ChartProject project) =>
        Encoding.UTF8.GetBytes(StableStructureXml(project).ToString(SaveOptions.DisableFormatting));

    [Fact]
    public void CopyThenGenerateRepairsAnOrphanedEndAndBothStepsUndoExactly()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var source = h.State.ActiveArrangement!;
        source.Phrases.Add("verse");
        source.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));
        var target = new Arrangement { Name = "Rhythm" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(8), Sustain = new FractionalPosition(1), Fret = 5 });
        target.Levels.Add(level);
        target.Phrases.Add("END");
        target.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));
        project.Arrangements.Add(target);
        var before = ChartProjectCloner.Clone(project, retainIdentities: true);

        Assert.True(h.Maintenance.CopyStructureToOtherArrangements() > 0);
        Assert.Contains("END", target.Phrases);
        Assert.DoesNotContain(PhraseMarkers.All(target), marker => marker.Name == "END");
        var copied = ChartProjectCloner.Clone(project, retainIdentities: true);
        h.State.ActiveArrangement = target;
        h.State.ActiveLevel = level;
        var generator = new DotCharter.Services.Editing.Structure.StructureGenerationService(h.State, h.History, new FakeAppLogger());

        Assert.Equal(1, generator.Generate().EndPhrases);
        Assert.Single(PhraseMarkers.All(target), marker => marker.Name == "END");
        var generated = ChartProjectCloner.Clone(project, retainIdentities: true);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            h.History.Undo();
            Assert.Empty(DeepCompare.Diff("undo generation", copied, project, new HashSet<string>()));
            h.History.Undo();
            Assert.Empty(DeepCompare.Diff("undo copy", before, project, new HashSet<string>()));
            h.History.Redo();
            Assert.Empty(DeepCompare.Diff("redo copy", copied, project, new HashSet<string>()));
            h.History.Redo();
            Assert.Empty(DeepCompare.Diff("redo generation", generated, project, new HashSet<string>()));
        }
    }

    [Fact]
    public void ImportedPhrasesAreCopiedWithSoloMetadataAndOverrideTargetIterations()
    {
        var source = ArrangementWith();
        source.Phrases.Add("solo");
        source.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));
        source.PhraseInfos["solo"] = new PhraseInfo(7, true);
        var target = ArrangementWith();
        target.Phrases.Add("verse");
        target.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));

        Assert.True(ChartMaintenanceService.MergeStructureInto(source, target) > 0);

        Assert.Equal("solo", Assert.Single(PhraseMarkers.All(target)).Name);
        var iterations = PhraseMarkers.Iterations(target);
        Assert.Equal("solo", iterations.Phrases[Assert.Single(iterations.Iterations).PhraseId]);
        Assert.True(target.PhraseInfos["solo"].Solo);
        Assert.Equal(0, target.PhraseInfos["solo"].MaxDifficulty);
        Assert.Equal(0, ChartMaintenanceService.MergeStructureInto(source, target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyCommitsAndUndoesImportedOrSoloOnlyChanges(bool imported)
    {
        var h = new ChartPowerToolsTests.Harness();
        var source = h.State.ActiveArrangement!;
        if (imported)
        {
            source.Phrases.Add("solo");
            source.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));
        }
        else
        {
            source.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "solo" });
        }
        source.PhraseInfos["solo"] = new PhraseInfo(7, true);
        var target = ArrangementWith(("", "solo", 4));
        target.PhraseInfos["solo"] = new PhraseInfo(0, false);
        h.State.ActiveProject!.Arrangements.Add(target);

        var before = ChartProjectCloner.Clone(h.State.ActiveProject!, retainIdentities: true);
        Assert.True(h.Maintenance.CopyStructureToOtherArrangements() > 0);
        var after = ChartProjectCloner.Clone(h.State.ActiveProject!, retainIdentities: true);
        Assert.True(target.PhraseInfos["solo"].Solo);
        Assert.Single(h.History.GetUndoSteps());
        h.History.Undo();
        Assert.False(target.PhraseInfos["solo"].Solo);
        Assert.Empty(DeepCompare.Diff("undo", before, h.State.ActiveProject!, new HashSet<string>()));
        h.History.Redo();
        Assert.True(target.PhraseInfos["solo"].Solo);
        Assert.Empty(DeepCompare.Diff("redo", after, h.State.ActiveProject!, new HashSet<string>()));
        Assert.Equal(0, h.Maintenance.CopyStructureToOtherArrangements());
    }

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
