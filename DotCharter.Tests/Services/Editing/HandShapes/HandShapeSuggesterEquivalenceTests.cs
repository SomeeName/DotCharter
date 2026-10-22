using System.Text.Json;
using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     The pooled-merge suggester gives the answer the keyed-map one gave: the same bars, the same
///     frets in the same order, the same reused template, the same counts. The oracle is the old path
///     kept verbatim (<see cref="LegacyHandShapeSuggester" />); the charts are 24 windows of the
///     owner's finished charts, charts shaped like a charter's (<see cref="SuggesterCharts" />), the
///     stress fixture, lists that are NOT position-sorted, and odd options.
/// </summary>
public sealed class HandShapeSuggesterEquivalenceTests
{
    private static void AssertSame(List<SuggestedHandShape> expected, List<SuggestedHandShape> actual, string what)
    {
        Assert.True(expected.Count == actual.Count, $"{what}: {expected.Count} suggestions expected, {actual.Count} found");
        for (var i = 0; i < expected.Count; i++)
        {
            var (e, a) = (expected[i], actual[i]);
            var at = $"{what}, suggestion {i}";
            Assert.True(e.Position == a.Position, $"{at}: position");
            Assert.True(e.EndPosition == a.EndPosition, $"{at}: end");
            Assert.True(ReferenceEquals(e.Existing, a.Existing), $"{at}: existing template");
            Assert.True(e.SoundCount == a.SoundCount && e.NoteCount == a.NoteCount, $"{at}: counts");
            Assert.True(e.Frets.SequenceEqual(a.Frets), $"{at}: frets (in order)");
        }
    }

    private static int Compare(Arrangement arrangement, string what, HandShapeSuggestOptions? options = null)
    {
        var level = arrangement.Levels[0];
        var expected = LegacyHandShapeSuggester.Suggest(arrangement, level, options);
        var actual = HandShapeSuggester.Suggest(arrangement, level, options);
        AssertSame(expected, actual, what);
        return expected.Count;
    }

    [Fact]
    public void TheOwnersFinishedChartWindows_GiveTheSameBars()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(HandShapeSuggesterCorpusFloorTests.FixturePath));
        var found = 0;
        var windows = 0;
        foreach (var window in doc.RootElement.GetProperty("windows").EnumerateArray())
        {
            var (arrangement, _, _) = HandShapeSuggesterCorpusFloorTests.Load(window);
            found += Compare(arrangement, $"window {windows++}");
        }

        Assert.Equal(24, windows);
        Assert.True(found > 20, $"only {found} suggestions across the windows; the comparison is thin");
    }

    [Fact]
    public void ChartsShapedLikeACharters_GiveTheSameBars_AcrossStringCountsCaposAndSizes()
    {
        var found = 0;
        var withTemplate = 0;
        var seeds = 0;
        int[] sizes = [12, 40, 150, 400, 900];
        foreach (var (stringCount, capo) in new[] { (6, 0), (6, 2), (7, 0), (4, 0), (8, 3) })
        {
            for (var seed = 0; seed < 14; seed++)
            {
                var arrangement = SuggesterCharts.Build(seed, sizes[seed % sizes.Length], stringCount, capo);
                found += Compare(arrangement, $"seed {seed}, {stringCount} strings, capo {capo}");
                withTemplate += LegacyHandShapeSuggester.Suggest(arrangement, arrangement.Levels[0]).Count(s => s.Existing != null);
                seeds++;
            }
        }

        Assert.Equal(70, seeds);
        // Non-vacuity: the sweep meets proposals, reused templates and plenty of grips.
        Assert.True(found > 300, $"only {found} suggestions across the sweep");
        Assert.True(withTemplate > 30, $"only {withTemplate} suggestions reused a declared template");
    }

    [Fact]
    public void ABassChart_StillGetsNone()
    {
        var arrangement = SuggesterCharts.Build(3, 300, 4, type: ArrangementType.Bass);
        Assert.Empty(HandShapeSuggester.Suggest(arrangement, arrangement.Levels[0]));
    }

    [Fact]
    public void TheStressChart_GivesTheSameBars()
    {
        var chart = StressChartFactory.Chart(5000);
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        foreach (var note in chart.Notes) level.Notes.Add(note);
        foreach (var chord in chart.Chords) level.Chords.Add(chord);
        foreach (var anchor in chart.Anchors) level.Anchors.Add(anchor);
        arrangement.Levels.Add(level);

        Compare(arrangement, "stress chart");
    }

    [Fact]
    public void NotesAndChordsThatAreNotSortedByPosition_GiveTheSameBars()
    {
        var checkedCharts = 0;
        for (var seed = 0; seed < 20; seed++)
        {
            var arrangement = SuggesterCharts.Build(seed, 200);
            var level = arrangement.Levels[0];
            var rng = new Random(seed);
            var notes = level.Notes.OrderBy(_ => rng.Next()).ToList();
            var chords = level.Chords.OrderBy(_ => rng.Next()).ToList();
            level.Notes.Clear();
            level.Chords.Clear();
            foreach (var note in notes) level.Notes.Add(note);
            foreach (var chord in chords) level.Chords.Add(chord);

            Compare(arrangement, $"shuffled seed {seed}");
            checkedCharts++;
        }

        Assert.Equal(20, checkedCharts);
    }

    [Fact]
    public void OtherOptions_GiveTheSameBars()
    {
        HandShapeSuggestOptions[] variants =
        [
            new(MinSounds: 2, MinStrings: 2, MinStringsAlone: 3),
            new(OneChordShape: false),
            new(MaxFretSpread: 4, MaxGap: 1.0),
            new(MaxFretSpread: 2, MaxGap: 4.0, MinSounds: 4)
        ];
        var found = 0;
        foreach (var (options, index) in variants.Select((o, i) => (o, i)))
        {
            for (var seed = 0; seed < 10; seed++)
                found += Compare(SuggesterCharts.Build(seed, 250), $"options {index}, seed {seed}", options);
        }

        Assert.True(found > 100, $"only {found} suggestions across the option sweep");
    }

    /// <summary>The per-call memo of which grips one hand can hold keys on the SHAPE, not the order its
    /// strings were first played — which holds only while the fingering is a pure function of the shape.</summary>
    [Fact]
    public void CanHold_DoesNotDependOnTheOrderTheGripWasPlayedIn()
    {
        var rng = new Random(11);
        var held = 0;
        var refused = 0;
        for (var round = 0; round < 1500; round++)
        {
            var strings = Enumerable.Range(0, 6).OrderBy(_ => rng.Next()).Take(rng.Next(2, 7)).ToArray();
            var low = rng.Next(0, 9);
            var frets = strings.Select(_ => rng.Next(5) == 0 ? 0 : low + rng.Next(0, 5)).ToArray();
            var grip = strings.Zip(frets).ToList();
            var capo = rng.Next(3) == 0 ? 2 : 0;
            var inOrder = HandShapeSuggester.CanHold(grip.ToDictionary(p => p.First, p => p.Second), capo, 6, 3);
            for (var shuffle = 0; shuffle < 3; shuffle++)
            {
                var other = grip.OrderBy(_ => rng.Next()).ToDictionary(p => p.First, p => p.Second);
                Assert.True(inOrder == HandShapeSuggester.CanHold(other, capo, 6, 3),
                    $"round {round}: [{string.Join(",", grip.Select(p => $"{p.First}:{p.Second}"))}] capo {capo}");
            }

            if (inOrder) held++;
            else refused++;
        }

        Assert.True(held > 100 && refused > 100, $"held {held}, refused {refused}: the sample is one-sided");
    }
}
