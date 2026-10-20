namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     <c>ArrangementValidationService</c>'s final ordering pass, whose comment
///     has always read "Stable order: non-positional first, then ascending time".
///     <para>
///         It sorted with <c>List&lt;T&gt;.Sort</c>, which is INTROSORT and
///         therefore not stable, over a comparator that returns 0 for every pair
///         sharing a time (and for every pair of non-positional findings at
///         all). Below 17 elements introsort falls through to insertion sort and
///         looks stable, so small charts hid it; past that the equal-time run is
///         partitioned and comes out in an order nobody chose.
///     </para>
///     <para>
///         The user-visible symptom is in the FEHLER tab: the rows are grouped by
///         severity with "the original issue order preserved inside each group"
///         (<c>IssueGrouping.Build</c>), so a charter part-way through triaging a
///         stack of findings on one beat had them reshuffle under the cursor the
///         moment an edit ANYWHERE else added or removed a finding — the list
///         length changes, introsort picks different pivots, and unrelated rows
///         move.
///     </para>
/// </summary>
public sealed class IssueOrderStabilityTests
{
    private static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>
    ///     <paramref name="stacked" /> notes on one beat, each on its own string
    ///     and each over fret 24, so every one produces an I38 at the same time
    ///     in a known order. <paramref name="extraElsewhere" /> adds unrelated
    ///     findings at a LATER beat — the "edit somewhere else" of the symptom.
    /// </summary>
    private static Arrangement Chart(int stacked, int extraElsewhere = 0)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();

        for (var i = 0; i < stacked; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(4), StringIndex = i, Fret = 25 + i
            });
        }

        for (var i = 0; i < extraElsewhere; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(8 + i), StringIndex = 0, Fret = 30
            });
        }

        arr.Levels.Add(level);
        return arr;
    }

    /// <summary>Beat 4 on a 120 bpm grid. The stacked notes all sit here; the
    /// unrelated ones are at beat 8 and later.</summary>
    private const double StackedBeatMs = 2000.0;

    /// <summary>The strings the stacked beat's I38 findings name, in the order
    /// the service returned them. Identified by TIME rather than by fret so the
    /// unrelated findings cannot leak into the sequence under test.</summary>
    private static List<int> StackedStringOrder(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(arr, Beats(32))
            .Where(i => i.Type == ArrangementIssueType.FretNumberMoreThan24
                        && i.TimeMs == StackedBeatMs)
            .Select(i => i.Note!.StringIndex)
            .ToList();

    /// <summary>
    ///     Findings that share a time come out in the order the rules produced
    ///     them — which for one rule sweeping one level is chart order. Twenty
    ///     is past introsort's insertion-sort threshold, where the unstable sort
    ///     is actually reached.
    /// </summary>
    [Fact]
    public void FindingsOnOneBeatKeepTheOrderTheRulesProducedThem()
    {
        var order = StackedStringOrder(Chart(stacked: 20));

        Assert.Equal(Enumerable.Range(0, 20).ToList(), order);
    }

    /// <summary>
    ///     The symptom itself: adding findings at an unrelated LATER time must
    ///     not move the rows on the earlier beat. This is what makes the FEHLER
    ///     list reshuffle mid-triage.
    /// </summary>
    [Fact]
    public void AFindingAddedElsewhereDoesNotReorderTheOnesBeforeIt()
    {
        var before = StackedStringOrder(Chart(stacked: 20));
        Assert.Equal(20, before.Count);

        for (var extra = 1; extra <= 6; extra++)
            Assert.Equal(before, StackedStringOrder(Chart(stacked: 20, extraElsewhere: extra)));
    }

    /// <summary>
    ///     The ordering contract the comment states, unchanged: non-positional
    ///     findings first, then ascending time.
    /// </summary>
    [Fact]
    public void NonPositionalFindingsStillComeFirstAndTimesStillAscend()
    {
        var issues = new ArrangementValidationService(new FakeAppLogger())
            .Validate(Chart(stacked: 20, extraElsewhere: 4), Beats(32));

        var firstTimed = issues.ToList().FindIndex(i => i.HasTime);
        Assert.True(firstTimed > 0, "the arrangement produces at least one non-positional finding");
        Assert.All(issues.Take(firstTimed), i => Assert.False(i.HasTime));

        var times = issues.Skip(firstTimed).Select(i => i.TimeMs!.Value).ToList();
        Assert.Equal(times.OrderBy(t => t).ToList(), times);
    }

    /// <summary>Two runs over the same chart agree — the weaker property, but the
    /// one a reader assumes when the list is called stable.</summary>
    [Fact]
    public void TwoRunsOverTheSameChartAgree() => Assert.Equal(StackedStringOrder(Chart(20, 3)), StackedStringOrder(Chart(20, 3)));
}
