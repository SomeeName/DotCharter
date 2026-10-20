using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The ported Editor-on-Fire time-signature and tempo import toggles, applied
///     by the shared <see cref="GpBeatMapBuilder"/> (used by both the GP and
///     Songsterr import dialogs).
/// </summary>
public sealed class GpBeatMapBuilderTests
{
    private static GpBarInfo Bar(int num, int den) => new() { BeatsInMeasure = num, NoteDenominator = den };

    [Fact]
    public void TimeSignaturesOn_UsesTheFileMeters()
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(4, 4));
        meta.Bars.Add(Bar(3, 4));

        var beats = GpBeatMapBuilder.Build(meta, 120, 0, importTimeSignatures: true);

        Assert.Equal(7, beats.Count);            // 4 + 3
        Assert.Equal(4, beats[0].BeatsInMeasure);
        Assert.Equal(3, beats[4].BeatsInMeasure); // first beat of the 3/4 bar
        Assert.True(beats[4].IsMeasure);
    }

    /// <summary>
    ///     <c>GpMetadataParser</c> was the THIRD reader of gpif's <c>&lt;Time&gt;</c>
    ///     element and the one that did not clamp it, while its sibling
    ///     <c>GpArrangementParser</c> — reading the same element in the same file —
    ///     has clamped it all along.
    ///     <para>
    ///         <see cref="GpBeatMapBuilder" /> allocates one <c>BeatMarker</c> per
    ///         iteration of <c>beatInBar &lt; beatsInMeasure</c>, so
    ///         <c>&lt;Time&gt;2000000000/4&lt;/Time&gt;</c> hung the import and then
    ///         ran out of memory. Gated on <c>AppConfig.GpImportTimeSignatures</c>,
    ///         which is ON by default.
    ///     </para>
    ///     <para>
    ///         Read through the real parser rather than by writing the bar by hand:
    ///         the clamp has to be AT THE READ, or a fourth reader inherits the
    ///         hole.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("2000000000/4", 32, 4)]
    [InlineData("4/2000000000", 4, 64)]
    [InlineData("0/0", 1, 1)]
    [InlineData("-4/-4", 1, 1)]
    public void AnAbsurdTimeSignature_IsClampedAtTheRead(string time, int expectedBeats, int expectedDenominator)
    {
        var meta = new GpImportMetadata();
        GpMetadataParser.Populate(GpifWithOneBar(time), meta);

        var bar = Assert.Single(meta.Bars);
        Assert.Equal(expectedBeats, bar.BeatsInMeasure);
        Assert.Equal(expectedDenominator, bar.NoteDenominator);

        // …and the emit loop it feeds therefore stays bounded.
        var beats = GpBeatMapBuilder.Build(meta, 120, 0, importTimeSignatures: true);
        Assert.Equal(expectedBeats, beats.Count);
    }

    private static System.Xml.Linq.XDocument GpifWithOneBar(string time) =>
        System.Xml.Linq.XDocument.Parse($"""
                                         <GPIF>
                                           <MasterBars>
                                             <MasterBar>
                                               <Time>{time}</Time>
                                               <Bars>0</Bars>
                                             </MasterBar>
                                           </MasterBars>
                                         </GPIF>
                                         """);

    [Fact]
    public void TimeSignaturesOff_ForcesFourFour()
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(4, 4));
        meta.Bars.Add(Bar(3, 4));

        var beats = GpBeatMapBuilder.Build(meta, 120, 0, importTimeSignatures: false);

        Assert.Equal(8, beats.Count);            // 4 + 4
        Assert.All(beats, b => Assert.Equal(4, b.BeatsInMeasure));
        Assert.All(beats, b => Assert.Equal(4, b.NoteDenominator));
    }

    [Fact]
    public void MidBeatTempoChange_IsApplied_WhenNotDropped()
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(4, 4));
        // 0.3 of the bar is between beat 1 (0.25) and beat 2 (0.5) — mid-beat.
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.3, Bpm = 200 });

        var beats = GpBeatMapBuilder.Build(meta, 120, 0, dropMidBeatTempoChanges: false);

        Assert.Equal(120, beats[0].Bpm);
        Assert.Equal(200, beats[1].Bpm); // the mid-beat change takes effect here
    }

    [Fact]
    public void MidBeatTempoChange_IsDropped_ButBarStartSurvives()
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(4, 4));
        meta.Bars.Add(Bar(4, 4));
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.3, Bpm = 200 }); // mid-beat
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 1, BarPosition = 0.0, Bpm = 150 }); // on a beat

        var beats = GpBeatMapBuilder.Build(meta, 120, 0, dropMidBeatTempoChanges: true);

        Assert.All(beats.Take(4), b => Assert.Equal(120, b.Bpm)); // mid-beat change discarded
        Assert.All(beats.Skip(4), b => Assert.Equal(150, b.Bpm)); // bar-start change kept
    }

    /// <summary>
    ///     A tempo change landing EXACTLY on a beat inside a bar (not on the bar
    ///     line) fell through both windows: the bar-start pass only takes
    ///     <c>BarPosition &lt;= 0.0001</c>, and the per-beat pass asked for
    ///     <c>&gt; posFrac &amp;&amp; &lt; nextFrac</c> — so a point at exactly
    ///     <c>k/beatsInMeasure</c> is equal to one window's lower bound and to the
    ///     previous window's upper bound and is admitted by neither. The change
    ///     was silently dropped and every later beat kept the old tempo, so the
    ///     whole rest of the import drifted in wall-clock time.
    ///     <para>
    ///         It is the COMMON spelling, not an edge case: an authored tempo
    ///         change is normally written on a beat. <c>IsOnBeat</c> — the
    ///         predicate behind <c>dropMidBeatTempoChanges</c> — calls exactly
    ///         these points on-beat and keeps them, which is what made the toggle
    ///         self-contradictory: with it ON, every surviving point except a
    ///         bar-start one was then discarded by the applier.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(0.25, 1)]
    [InlineData(0.5, 2)]
    [InlineData(0.75, 3)]
    public void AnOnBeatTempoChangeInsideABar_IsApplied(double barPosition, int firstChangedBeat)
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(4, 4));
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = barPosition, Bpm = 200 });

        var beats = GpBeatMapBuilder.Build(meta, 120, 0);

        for (var i = 0; i < firstChangedBeat; i++) Assert.Equal(120, beats[i].Bpm);
        for (var i = firstChangedBeat; i < 4; i++) Assert.Equal(200, beats[i].Bpm);
    }

    /// <summary>The same point survives the <c>dropMidBeatTempoChanges</c>
    /// filter — <c>IsOnBeat</c> keeps it — so it must be applied there too.</summary>
    [Fact]
    public void AnOnBeatTempoChangeInsideABar_SurvivesTheMidBeatDrop()
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(4, 4));
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.5, Bpm = 200 });

        var beats = GpBeatMapBuilder.Build(meta, 120, 0, dropMidBeatTempoChanges: true);

        Assert.Equal([120.0, 120.0, 200.0, 200.0], beats.Select(b => b.Bpm));
    }

    /// <summary>A meter whose beat positions are not exactly representable in
    /// binary (1/3, 2/3 of a 3/4 bar): the point still has to land on its own
    /// beat rather than being dropped by an exact comparison.</summary>
    [Fact]
    public void AnOnBeatTempoChangeInAThreeFourBar_IsApplied()
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(3, 4));
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 2.0 / 3.0, Bpm = 200 });

        var beats = GpBeatMapBuilder.Build(meta, 120, 0);

        Assert.Equal([120.0, 120.0, 200.0], beats.Select(b => b.Bpm));
    }

    /// <summary>A point at the very END of a bar belongs to the NEXT bar, not to
    /// the bar's last beat — the upper bound stays exclusive.</summary>
    [Fact]
    public void ATempoChangeAtTheEndOfABar_DoesNotLeakOntoItsLastBeat()
    {
        var meta = new GpImportMetadata();
        meta.Bars.Add(Bar(4, 4));
        meta.Bars.Add(Bar(4, 4));
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 1.0, Bpm = 200 });

        var beats = GpBeatMapBuilder.Build(meta, 120, 0);

        Assert.All(beats, b => Assert.Equal(120, b.Bpm));
    }

    // ── the OPENING tempo ────────────────────────────────────────────────────
    //
    // `Build` sorts its tempo points by (BarIndex, BarPosition), but the BPM it
    // is HANDED for every bar before the first point came from four call sites
    // that each spelled it `TempoPoints.FirstOrDefault()?.Bpm` — DOCUMENT order.
    // GpMetadataParser walks <Automations> in document order and fans a repeated
    // section's automation over every unfolded pass; SongsterrConverter appends
    // its `automations` block AFTER the beat-level tempos it read bar by bar. So
    // a score whose bar-0 tempo lives in `automations` opened at a LATER bar's
    // tempo — and that value also became project.Bpm and the transport's BPM.

    [Fact]
    public void InitialBpm_TakesTheEarliestBarNotTheFirstAppended()
    {
        var meta = new GpImportMetadata();
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 12, BarPosition = 0.0, Bpm = 200 });
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.0, Bpm = 90 });

        Assert.Equal(90, GpBeatMapBuilder.InitialBpm(meta, 120));
    }

    /// <summary>Within one bar the earliest POSITION wins too.</summary>
    [Fact]
    public void InitialBpm_TakesTheEarliestPositionWithinABar()
    {
        var meta = new GpImportMetadata();
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.75, Bpm = 200 });
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.0, Bpm = 90 });

        Assert.Equal(90, GpBeatMapBuilder.InitialBpm(meta, 120));
    }

    /// <summary>The unusable-point filter is <see cref="GpBeatMapBuilder.Build"/>'s
    /// own, so the two cannot disagree about which points count.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-60.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InitialBpm_SkipsAnUnusablePoint(double bad)
    {
        var meta = new GpImportMetadata();
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.0, Bpm = bad });
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 4, BarPosition = 0.0, Bpm = 90 });

        Assert.Equal(90, GpBeatMapBuilder.InitialBpm(meta, 120));
    }

    [Fact]
    public void InitialBpm_NoPoints_IsTheFallback()
    {
        Assert.Equal(137, GpBeatMapBuilder.InitialBpm(new GpImportMetadata(), 137));
        Assert.Equal(137, GpBeatMapBuilder.InitialBpm(null, 137));
    }

    /// <summary>End to end: the bars BEFORE the first tempo point carry the
    /// opening tempo, not a later bar's.</summary>
    [Fact]
    public void Build_WithAnUnorderedTempoList_OpensAtTheEarliestBarsTempo()
    {
        var meta = new GpImportMetadata();
        for (var i = 0; i < 3; i++) meta.Bars.Add(Bar(4, 4));
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 2, BarPosition = 0.0, Bpm = 200 });
        meta.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0.0, Bpm = 90 });

        var beats = GpBeatMapBuilder.Build(meta, GpBeatMapBuilder.InitialBpm(meta, 120), 0);

        Assert.All(beats.Take(8), b => Assert.Equal(90, b.Bpm));
        Assert.All(beats.Skip(8), b => Assert.Equal(200, b.Bpm));
    }
}
