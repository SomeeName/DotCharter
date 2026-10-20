namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     Which audio belongs to which tone change.
///     <para>
///         The owner's rule for generating tones from the song: <b>the placement
///         is not generated</b> — the charter drops a tone change where the tone
///         should change, and the tone is built from the audio between that
///         change and the next. Everything downstream reads the samples this type
///         points at, so a wrong span does not fail: it returns a confident tone
///         built from the wrong part of the song.
///     </para>
/// </summary>
public sealed class ToneChangeSpansTests
{
    /// <summary>Beat i sits at i × 500 ms.</summary>
    private static List<BeatMarker> Beats(int count = 64)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        return beats;
    }

    private static ToneChange At(int beat, string name) =>
        new() { Position = new FractionalPosition(beat), ToneName = name };

    [Fact]
    public void EachChangeRunsUntilTheNextOne()
    {
        var changes = new List<ToneChange> { At(2, "Clean"), At(6, "Overdrive") };

        var spans = ToneChangeSpans.Resolve(changes, Beats(), songLengthMs: 10_000);

        Assert.Equal(2, spans.Count);
        Assert.Equal(1000, spans[0].StartMs, 3);
        Assert.Equal(3000, spans[0].EndMs, 3);
        Assert.Equal(3000, spans[1].StartMs, 3);
    }

    [Fact]
    public void TheLastChangeRunsToTheEndOfTheSong()
    {
        var spans = ToneChangeSpans.Resolve([At(2, "Clean")], Beats(), songLengthMs: 10_000);

        Assert.Equal(10_000, Assert.Single(spans).EndMs, 3);
    }

    /// <summary>
    ///     The collection's order is not guaranteed chronological, and a span
    ///     computed from an unsorted list pairs a change with the WRONG audio —
    ///     silently, and only on charts edited out of order.
    /// </summary>
    [Fact]
    public void ChangesAreSortedBeforePairing()
    {
        var changes = new List<ToneChange> { At(6, "Overdrive"), At(2, "Clean") };

        var spans = ToneChangeSpans.Resolve(changes, Beats(), songLengthMs: 10_000);

        Assert.Equal("Clean", spans[0].ToneName);
        Assert.Equal(1000, spans[0].StartMs, 3);
        Assert.Equal("Overdrive", spans[1].ToneName);
    }

    [Fact]
    public void NoChangesMeansNoSpans() =>
        Assert.Empty(ToneChangeSpans.Resolve([], Beats(), 10_000));

    [Fact]
    public void NoBeatGridMeansNoSpans() =>
        Assert.Empty(ToneChangeSpans.Resolve([At(2, "Clean")], [], 10_000));

    /// <summary>A stab is not evidence of a tone — averaging a spectrum over a
    /// few hundred milliseconds answers confidently from one chord.</summary>
    [Fact]
    public void AVeryShortSpanIsNotUseful()
    {
        var changes = new List<ToneChange> { At(2, "Clean"), At(3, "Overdrive") };

        var spans = ToneChangeSpans.Resolve(changes, Beats(), songLengthMs: 10_000);

        Assert.Equal(500, spans[0].DurationMs, 3);
        Assert.False(spans[0].IsUseful);
        Assert.True(spans[1].IsUseful);
    }

    /// <summary>
    ///     Two changes on the same beat is a real (broken) chart state. The
    ///     degenerate span is KEPT — dropping it would renumber every later
    ///     index, so a caller acting on "span 3" would act on the wrong tone.
    /// </summary>
    [Fact]
    public void TwoChangesAtOnePositionLeaveAZeroLengthSpanInPlace()
    {
        var changes = new List<ToneChange> { At(2, "Clean"), At(2, "Overdrive"), At(8, "Lead") };

        var spans = ToneChangeSpans.Resolve(changes, Beats(), songLengthMs: 10_000);

        Assert.Equal(3, spans.Count);
        Assert.Equal(0, spans[0].DurationMs, 3);
        Assert.False(spans[0].IsUseful);
        Assert.Equal(2, spans[2].Index);
    }

    /// <summary>A change past the end of the audio governs nothing, and must not
    /// produce a NEGATIVE span for every consumer to guard.</summary>
    [Fact]
    public void AChangePastTheEndOfTheSongIsClampedNotNegative()
    {
        var spans = ToneChangeSpans.Resolve([At(2, "Clean"), At(40, "Late")], Beats(), songLengthMs: 5_000);

        Assert.All(spans, s => Assert.True(s.DurationMs >= 0, $"span {s.Index} is {s.DurationMs} ms"));
        Assert.Equal(0, spans[1].DurationMs, 3);
    }

    // ---- SpanFor ---------------------------------------------------------

    /// <summary>
    ///     Matched by REFERENCE. Several changes legitimately share a name — a
    ///     song returns to its clean tone — so a name match would pick the wrong
    ///     span exactly where a chart is most confusing.
    /// </summary>
    [Fact]
    public void SpanForFindsTheRightOneAmongDuplicateNames()
    {
        var first = At(2, "Clean");
        var second = At(10, "Clean");
        var changes = new List<ToneChange> { first, At(6, "Overdrive"), second };

        var span = ToneChangeSpans.SpanFor(second, changes, Beats(), 20_000);

        Assert.NotNull(span);
        Assert.Equal(5000, span!.Value.StartMs, 3);
    }

    [Fact]
    public void SpanForReturnsNullForAChangeThatIsNotInTheList() =>
        Assert.Null(ToneChangeSpans.SpanFor(At(2, "Clean"), [At(4, "Other")], Beats(), 10_000));
}
