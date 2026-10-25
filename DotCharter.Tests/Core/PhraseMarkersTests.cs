namespace DotCharter.Tests.Core;

/// <summary>
///     An arrangement can carry its phrases in EITHER store, and an imported
///     chart that has since been edited carries both. Reading one is a silent
///     whole-feature failure: measured on the 89 hand-finished RS2 exports in
///     <c>TestData/EOF</c>, <c>EventPoint.Phrase</c> yields <b>0</b> markers and
///     <c>PhraseIterations</c> yields <b>2,596</b> — so a rule keyed on the
///     former finds nothing to check and reports a clean chart.
/// </summary>
public sealed class PhraseMarkersTests
{
    private static Arrangement WithEventPoints(params (int Beat, string Phrase)[] phrases)
    {
        var arr = new Arrangement();
        foreach (var (beat, phrase) in phrases)
            arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = phrase });
        return arr;
    }

    private static Arrangement WithIterations(string[] names, params (int Beat, int PhraseId)[] iterations)
    {
        var arr = new Arrangement();
        arr.Phrases.AddRange(names);
        foreach (var (beat, id) in iterations)
            arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(beat), id));
        return arr;
    }

    [Fact]
    public void TheEditorsOwnPhraseMarkersAreFound()
    {
        var markers = PhraseMarkers.All(WithEventPoints((0, "intro"), (8, "verse")));

        Assert.Equal(["intro", "verse"], markers.Select(m => m.Name).ToArray());
    }

    /// <summary>The store every imported chart actually uses.</summary>
    [Fact]
    public void AnImportedChartsPhraseIterationsAreFound()
    {
        var markers = PhraseMarkers.All(WithIterations(["intro", "verse"], (0, 0), (8, 1)));

        Assert.Equal(["intro", "verse"], markers.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void BothStoresAreUnionedAndOrderedByPosition()
    {
        var arr = WithIterations(["intro", "verse"], (0, 0), (16, 1));
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "bridge" });

        var markers = PhraseMarkers.All(arr);

        Assert.Equal(["intro", "bridge", "verse"], markers.Select(m => m.Name).ToArray());
    }

    /// <summary>
    ///     An imported phrase the editor has since re-authored is ONE marker.
    ///     Counting it twice would make a phrase look like a zero-length span
    ///     and hide the phrase that really follows it.
    /// </summary>
    [Fact]
    public void APositionCarriedByBothStoresYieldsASingleMarker()
    {
        var arr = WithIterations(["imported"], (8, 0));
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "edited" });

        var marker = Assert.Single(PhraseMarkers.All(arr));

        // The editor's own text wins — it is the later authority.
        Assert.Equal("edited", marker.Name);
    }

    [Fact]
    public void AnIterationPointingOutsideThePhraseTableStillYieldsAMarker()
    {
        var marker = Assert.Single(PhraseMarkers.All(WithIterations(["intro"], (4, 77))));

        Assert.Equal("", marker.Name);
        Assert.Equal(new FractionalPosition(4), marker.Position);
    }

    [Theory]
    [InlineData("COUNT")]
    [InlineData("count")]
    [InlineData("END")]
    [InlineData("noguitar")]
    [InlineData("noguitar1")]
    [InlineData("ng")]
    public void StructuralPhrasesAreExcludedFromPlayable(string phrase)
    {
        var arr = WithEventPoints((0, "riff"), (8, phrase));

        Assert.Equal(2, PhraseMarkers.All(arr).Count);
        Assert.Equal(["riff"], PhraseMarkers.Playable(arr).Select(m => m.Name).ToArray());
    }

    [Fact]
    public void AnArrangementWithNoPhrasesAtAllYieldsNothing() => Assert.Empty(PhraseMarkers.All(new Arrangement()));

    // ── Iterations: the per-iteration view of both stores ───────────────────

    private static string[] Named(PhraseMarkers.IterationSet set) =>
        set.Iterations.Select(i => set.Phrases[i.PhraseId]).ToArray();

    [Fact]
    public void Iterations_FromMarkersOnly_MintPhrasesByFirstAppearance()
    {
        var set = PhraseMarkers.Iterations(WithEventPoints((8, "chorus"), (0, "verse"), (16, "verse")));

        Assert.Equal(["verse", "chorus"], set.Phrases);
        Assert.Equal(["verse", "chorus", "verse"], Named(set));
        Assert.Equal([0, 8, 16], set.Iterations.Select(i => i.Position.BeatId).ToArray());
    }

    [Fact]
    public void Iterations_FromExplicitOnly_AreTheSameObjects()
    {
        var arr = WithIterations(["intro", "riff"], (0, 0), (4, 1));

        var set = PhraseMarkers.Iterations(arr);

        Assert.Equal(arr.PhraseIterations, set.Iterations);
        Assert.Same(arr.PhraseIterations[1], set.Iterations[1]);
    }

    /// <summary>An imported chart whose markers mirror its iterations is unchanged…</summary>
    [Fact]
    public void Iterations_WithOverlappingStores_AddNothing()
    {
        var arr = WithIterations(["intro", "riff"], (0, 0), (4, 1));
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "riff" });

        var set = PhraseMarkers.Iterations(arr);

        Assert.Equal(["intro", "riff"], Named(set));
    }

    /// <summary>…and a marker the user added joins in time order, minting its name once.</summary>
    [Fact]
    public void Iterations_WithDisjointStores_MergeInTimeOrder()
    {
        var arr = WithIterations(["intro", "riff"], (0, 0), (8, 1));
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "bridge" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(12), Phrase = "riff" });

        var set = PhraseMarkers.Iterations(arr);

        Assert.Equal(["intro", "riff", "bridge"], set.Phrases);
        Assert.Equal(["intro", "bridge", "riff", "riff"], Named(set));
    }

    /// <summary>An export pads a markers-only chart with COUNT and END; nothing else does.</summary>
    [Fact]
    public void Iterations_PadCountAndEnd_OnlyWhenAskedAndMissing()
    {
        var arr = WithEventPoints((2, "verse"));

        Assert.Equal(["verse"], Named(PhraseMarkers.Iterations(arr)));
        var padded = PhraseMarkers.Iterations(arr, () => new FractionalPosition(20));
        Assert.Equal(["COUNT", "verse", "END"], Named(padded));
        Assert.Equal(20, padded.Iterations[^1].Position.BeatId);
    }
}
