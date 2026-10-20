using DotCharter.Services.Ai.Generate;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The window a placed tone change is generated FROM: one measure of MUSICAL
///     lead, clamped so windows never overlap and never start before the song.
/// </summary>
public sealed class ToneWindowPlannerTests
{
    /// <summary>A beat map from explicit measure lengths, four beats a bar. Bar
    /// <c>i</c> is <paramref name="barMs" />[i] long, so a tempo change is a
    /// different number in the array rather than a BPM to convert.</summary>
    private static List<BeatMarker> Beats(params double[] barMs)
    {
        var beats = new List<BeatMarker>();
        var t = 0.0;
        foreach (var bar in barMs)
        {
            for (var b = 0; b < 4; b++)
            {
                beats.Add(new BeatMarker
                {
                    Time = t + b * bar / 4.0,
                    IsMeasure = b == 0,
                    BeatsInMeasure = 4,
                    Bpm = 60_000.0 / (bar / 4.0)
                });
            }

            t += bar;
        }

        // One closing bar line so the LAST written bar has a span of its own.
        beats.Add(new BeatMarker { Time = t, IsMeasure = true, BeatsInMeasure = 4 });
        return beats;
    }

    private static ToneChange At(double ms, List<BeatMarker> beats, string name = "") =>
        new() { Position = TimelineMath.PositionFromMs(ms, beats), ToneName = name };

    [Fact]
    public void TheWindowStartsOneMeasureBeforeTheChange()
    {
        // Four 2,000 ms bars. A change on the third bar line gets 2,000 ms of lead.
        var beats = Beats(2000, 2000, 2000, 2000);
        var windows = ToneWindowPlanner.Plan([At(4000, beats)], beats, 8000);

        var w = Assert.Single(windows);
        Assert.Equal(4000, w.PlacedMs, 1);
        Assert.Equal(2000, w.FromMs, 1);
        Assert.Equal(2000, w.MeasureMs, 1);
        Assert.Equal(2000, w.LeadMs, 1);
        Assert.Equal(8000, w.ToMs, 1);
        Assert.Equal(6000, w.DurationMs, 1);
        Assert.Equal(ToneWindowClamp.None, w.ClampedBy);
    }

    [Fact]
    public void TheLeadFollowsTheTEMPOMAP_NotAFixedMillisecondValue()
    {
        // Bars 1–2 are 4,000 ms (slow); bars 3–4 are 1,000 ms (fast). Two changes,
        // one in each half, must get DIFFERENT leads — that is the whole reason the
        // measure is resolved through the beat map instead of subtracted.
        var beats = Beats(4000, 4000, 1000, 1000);
        var slow = At(8000, beats);   // the third bar line, which sits in a 1,000 ms bar
        var inSlow = At(6000, beats);  // mid-bar 2, a 4,000 ms bar

        var windows = ToneWindowPlanner.Plan([inSlow, slow], beats, 10_000);

        Assert.Equal(4000, windows[0].MeasureMs, 1);
        Assert.Equal(1000, windows[1].MeasureMs, 1);
        Assert.NotEqual(windows[0].MeasureMs, windows[1].MeasureMs);
    }

    [Fact]
    public void AChangeInTheFirstMeasure_IsClampedToTheStart_NotNegative()
    {
        var beats = Beats(2000, 2000);
        var windows = ToneWindowPlanner.Plan([At(500, beats)], beats, 4000);

        var w = Assert.Single(windows);
        Assert.Equal(0, w.FromMs, 1);
        Assert.True(w.FromMs >= 0, $"the window starts at {w.FromMs} ms");
        Assert.Equal(500, w.LeadMs, 1);
        Assert.Equal(ToneWindowClamp.ArrangementStart, w.ClampedBy);
        // The measure is still REPORTED in full, so the arithmetic stays inspectable.
        Assert.Equal(2000, w.MeasureMs, 1);
    }

    [Fact]
    public void TwoChangesLessThanAMeasureApart_DoNotOverlap()
    {
        // Bars are 2,000 ms; the changes are 500 ms apart.
        var beats = Beats(2000, 2000, 2000);
        var windows = ToneWindowPlanner.Plan([At(3000, beats), At(3500, beats)], beats, 6000);

        Assert.Equal(2, windows.Count);
        Assert.Equal(ToneWindowClamp.PreviousChange, windows[1].ClampedBy);
        Assert.Equal(3000, windows[1].FromMs, 1);
        Assert.Equal(500, windows[1].LeadMs, 1);

        // The claim, stated directly: a window ends exactly where the next one
        // starts, so the two never describe the same audio.
        Assert.Equal(windows[1].FromMs, windows[0].ToMs, 1);
        // …and the consequence: the earlier change's window is entirely its own
        // lead, the bar before its marker, because the later change's lead took the
        // 500 ms after it.
        Assert.Equal(1000, windows[0].FromMs, 1);
        Assert.Equal(3000, windows[0].ToMs, 1);
    }

    [Fact]
    public void ChangesOutOfOrder_AreSortedBeforeAnythingIsPlanned()
    {
        // The collection's order is not guaranteed chronological; a window planned
        // from an unsorted list would pair a change with another one's audio.
        var beats = Beats(2000, 2000, 2000);
        var windows = ToneWindowPlanner.Plan([At(4000, beats, "late"), At(2000, beats, "early")], beats, 6000);

        Assert.Equal(["early", "late"], windows.Select(w => w.ToneName).ToArray());
        Assert.True(windows[0].PlacedMs < windows[1].PlacedMs);
    }

    [Fact]
    public void TheLastWindowRunsToTheEndOfTheArrangement()
    {
        var beats = Beats(2000, 2000);
        var windows = ToneWindowPlanner.Plan([At(2000, beats)], beats, 123_456);

        Assert.Equal(123_456, Assert.Single(windows).ToMs, 1);
    }

    [Fact]
    public void WithNoBeatMap_ItSaysWhyRatherThanGuessing()
    {
        // A measure is musical time. Without a tempo map there is no measure, and a
        // fixed millisecond fallback would be a different window on every song.
        var ex = Assert.Throws<ArgumentException>(() =>
            ToneWindowPlanner.Plan([new ToneChange()], [], 1000));

        Assert.Contains("beat map", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDescriptionCarriesTheMeasureArithmetic()
    {
        // Provenance: a reader must be able to check the window rather than trust it.
        var beats = Beats(2000, 2000, 2000);
        var w = Assert.Single(ToneWindowPlanner.Plan([At(4000, beats)], beats, 6000));

        Assert.Contains("one measure (2000 ms at this tempo)", w.Describe(), StringComparison.Ordinal);
        Assert.Contains("placed at 4000 ms", w.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasureLengthIsTheBARSpan_NotAComputedOne()
    {
        // The guard against the guard: if MeasureLengthAt derived the span from BPM
        // it would agree here by coincidence, so the map is built with a bar line
        // NUDGED off the beat grid and the answer must follow the LINE.
        var beats = new List<BeatMarker>
        {
            new() { Time = 0, IsMeasure = true, BeatsInMeasure = 4, Bpm = 120 },
            new() { Time = 500, BeatsInMeasure = 4, Bpm = 120 },
            new() { Time = 1000, BeatsInMeasure = 4, Bpm = 120 },
            new() { Time = 1500, BeatsInMeasure = 4, Bpm = 120 },
            // 120 BPM / 4-4 says the next bar line is at 2000; it is at 2600.
            new() { Time = 2600, IsMeasure = true, BeatsInMeasure = 4, Bpm = 120 },
            new() { Time = 4600, IsMeasure = true, BeatsInMeasure = 4, Bpm = 120 }
        };

        Assert.Equal(2600, ToneWindowPlanner.MeasureLengthAt(1000, beats), 1);
        Assert.Equal(2000, ToneWindowPlanner.MeasureLengthAt(3000, beats), 1);
    }
}
