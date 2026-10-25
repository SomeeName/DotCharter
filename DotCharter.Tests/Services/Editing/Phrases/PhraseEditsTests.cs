using DotCharter.Services.Editing.Phrases;

namespace DotCharter.Tests.Services.Editing.Phrases;

/// <summary>
///     Rename and delete of one phrase reach BOTH phrase stores — the markers the editor authors
///     and the explicit list + iterations an import carries — and the name-keyed metadata, as one
///     undo node that puts every id and marker back exactly.
/// </summary>
public sealed class PhraseEditsTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());

    private static FractionalPosition Beat(int beat) => new(beat);

    /// <summary>Markers only, as an editor-placed or EOF-imported chart keeps them.</summary>
    private static Arrangement MarkersOnly()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.EventPoints.Add(new EventPoint { Position = Beat(0), Phrase = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = Beat(8), Phrase = "chorus", Section = "chorus" });
        arrangement.EventPoints.Add(new EventPoint { Position = Beat(16), Phrase = " Verse " });
        return arrangement;
    }

    /// <summary>The explicit store, as an RS-XML or psarc import fills it.</summary>
    private static Arrangement Imported()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Phrases.AddRange(["intro", "verse", "chorus"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(Beat(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Beat(4), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Beat(12), 2));
        arrangement.PhraseIterations.Add(new PhraseIteration(Beat(20), 1));
        arrangement.PhraseInfos["verse"] = new PhraseInfo { MaxDifficulty = 7 };
        return arrangement;
    }

    private static List<(FractionalPosition Position, string Name)> Effective(Arrangement arrangement)
    {
        var (phrases, iterations) = PhraseMarkers.Iterations(arrangement);
        return [.. iterations.Select(i => (i.Position, phrases[i.PhraseId]))];
    }

    [Fact]
    public void RenamingAMarkerPhrase_RenamesEveryMarker_MatchedAsTheReaderMatches()
    {
        var arrangement = MarkersOnly();

        Assert.True(_history.Do(PhraseEdits.PlanRename(arrangement, "verse", "A")!));

        Assert.Equal(["A", "chorus", "A"], arrangement.EventPoints.Select(e => e.Phrase));
        _history.Undo();
        Assert.Equal(["verse", "chorus", " Verse "], arrangement.EventPoints.Select(e => e.Phrase));
    }

    [Fact]
    public void RenamingAnImportedPhrase_RenamesTheListEntry_AndMovesItsMetadata()
    {
        var arrangement = Imported();

        _history.Do(PhraseEdits.PlanRename(arrangement, "verse", "A")!);

        Assert.Equal(["intro", "A", "chorus"], arrangement.Phrases);
        Assert.Equal(7, arrangement.PhraseInfos["A"].MaxDifficulty);
        Assert.False(arrangement.PhraseInfos.ContainsKey("verse"));
    }

    /// <summary>Renaming onto a phrase that exists merges: the iterations join it, the ids close up.</summary>
    [Fact]
    public void RenamingOntoAnExistingPhrase_MergesIt()
    {
        var arrangement = Imported();
        var before = Effective(arrangement);

        _history.Do(PhraseEdits.PlanRename(arrangement, "intro", "verse")!);

        Assert.Equal(["verse", "chorus"], arrangement.Phrases);
        Assert.Equal([0, 0, 1, 0], arrangement.PhraseIterations.Select(i => i.PhraseId));
        _history.Undo();
        Assert.Equal(before, Effective(arrangement));
        Assert.Equal([0, 1, 2, 1], arrangement.PhraseIterations.Select(i => i.PhraseId));
    }

    [Fact]
    public void DeletingAnImportedPhrase_RemovesItsIterations_AndRenumbersTheRest()
    {
        var arrangement = Imported();
        var iterations = arrangement.PhraseIterations.ToList();

        _history.Do(PhraseEdits.PlanDelete(arrangement, "verse")!);

        Assert.Equal(["intro", "chorus"], arrangement.Phrases);
        Assert.Equal([0, 1], arrangement.PhraseIterations.Select(i => i.PhraseId));
        Assert.Empty(arrangement.PhraseInfos);
        _history.Undo();
        Assert.Equal(iterations, arrangement.PhraseIterations);
        Assert.Equal([0, 1, 2, 1], arrangement.PhraseIterations.Select(i => i.PhraseId));
        Assert.Equal(7, arrangement.PhraseInfos["verse"].MaxDifficulty);
    }

    /// <summary>A marker that also carries a section keeps the section; a phrase-only marker goes.</summary>
    [Fact]
    public void DeletingAMarkerPhrase_KeepsWhatElseAMarkerCarries()
    {
        var arrangement = MarkersOnly();
        var chorusMarker = arrangement.EventPoints[1];

        _history.Do(PhraseEdits.PlanDelete(arrangement, "chorus")!);

        Assert.Contains(chorusMarker, arrangement.EventPoints);
        Assert.Equal("", chorusMarker.Phrase);
        Assert.Equal("chorus", chorusMarker.Section);

        _history.Do(PhraseEdits.PlanDelete(arrangement, "verse")!);
        Assert.Equal([chorusMarker], arrangement.EventPoints);

        _history.Undo();
        _history.Undo();
        Assert.Equal(["verse", "chorus", " Verse "], arrangement.EventPoints.Select(e => e.Phrase));
    }

    /// <summary>An imported chart the editor has added a marker to: both stores lose the phrase.</summary>
    [Fact]
    public void DeletingFromAMixedArrangement_ClearsBothStores()
    {
        var arrangement = Imported();
        arrangement.EventPoints.Add(new EventPoint { Position = Beat(28), Phrase = "verse" });

        _history.Do(PhraseEdits.PlanDelete(arrangement, "verse")!);

        Assert.DoesNotContain(PhraseMarkers.Iterations(arrangement).Phrases, p => p == "verse");
    }

    [Fact]
    public void EachEdit_IsOneUndoNode()
    {
        var arrangement = Imported();

        _history.Do(PhraseEdits.PlanRename(arrangement, "verse", "A")!);
        _history.Do(PhraseEdits.PlanDelete(arrangement, "chorus")!);

        Assert.Equal(3, _history.GetTree().Count);
        Assert.Contains(_history.GetTree(), n => n.Description.Contains(PhraseEdits.RenameLabelKey, StringComparison.Ordinal));
        Assert.Contains(_history.GetTree(), n => n.Description.Contains(PhraseEdits.DeleteLabelKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("verse", "verse")]
    [InlineData("verse", "   ")]
    [InlineData("nothing", "A")]
    public void ARenameThatChangesNothing_PlansNothing(string from, string to) =>
        Assert.Null(PhraseEdits.PlanRename(Imported(), from, to));

    [Fact]
    public void DeletingAPhraseThatIsNotThere_PlansNothing() =>
        Assert.Null(PhraseEdits.PlanDelete(MarkersOnly(), "bridge"));
}
