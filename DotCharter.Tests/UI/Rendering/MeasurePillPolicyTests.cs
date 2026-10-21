using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Rendering;

/// <summary>
/// Pins the measure-pill decision seam: which measures get a pill at any zoom
/// (<see cref="MeasurePillPolicy.Plan" />), in which form, and the BPM-change
/// epsilon that distinguishes a real tempo change from float noise.
/// </summary>
public class MeasurePillPolicyTests
{
    [Fact]
    public void BpmDiffers_RealTempoChange_IsTrue() =>
        // The Matasaburo chart's real alternation — must register as a change.
        Assert.True(MeasurePillPolicy.BpmDiffers(131.87, 132.16));

    [Fact]
    public void BpmDiffers_IdenticalBpm_IsFalse() => Assert.False(MeasurePillPolicy.BpmDiffers(200.0, 200.0));

    [Fact]
    public void BpmDiffers_WithinEpsilon_IsFalse() =>
        // Float noise below the 0.005 threshold is not a change.
        Assert.False(MeasurePillPolicy.BpmDiffers(200.0, 200.003));

    [Fact]
    public void BpmDiffers_JustBeyondEpsilon_IsTrue() => Assert.True(MeasurePillPolicy.BpmDiffers(200.0, 200.01));

    // ── the measure row at any zoom ─────────────────────────────────────

    /// <summary>Text-free widths shaped like the batcher's: the compact pill is the number
    /// and a three-digit tempo, the full one adds the "M" and " BPM", the meter adds 18.</summary>
    private static double FakeWidth(int measureNumber, bool full, bool sig) =>
        6 + (6 * measureNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Length)
          + 18 + (sig ? 18 : 0) + (full ? 40 : 0);

    private static List<MeasurePillPolicy.MeasureSlot> Slots(int first, int count, double measureWidth,
        ISet<int>? bpmChanges = null, ISet<int>? sigChanges = null, double offset = 0) =>
        Enumerable.Range(first, count)
            .Select(n => new MeasurePillPolicy.MeasureSlot(n, offset + ((n - first) * measureWidth),
                bpmChanges?.Contains(n) == true, sigChanges?.Contains(n) == true))
            .ToList();

    private static List<MeasurePillPolicy.PlannedPill> Plan(List<MeasurePillPolicy.MeasureSlot> slots, bool first = true)
    {
        var placed = new List<MeasurePillPolicy.PlannedPill>();
        MeasurePillPolicy.Plan(slots, (i, full, sig) => FakeWidth(slots[i].MeasureNumber, full, sig), first, placed);
        return placed;
    }

    private static readonly double[] Zooms = [0.25, 0.5, 1, 2, 3, 5, 8, 13, 20, 30, 50, 80, 120, 200, 400];

    [Fact]
    public void NoTwoPills_Overlap_AtAnyZoom()
    {
        var bpm = new HashSet<int> { 7, 8, 9, 40, 41, 120, 121, 122, 250 };
        var sig = new HashSet<int> { 9, 100, 101 };
        foreach (var zoom in Zooms)
        {
            var placed = Plan(Slots(1, 300, zoom, bpm, sig));
            Assert.NotEmpty(placed);
            for (var k = 1; k < placed.Count; k++)
            {
                Assert.True(placed[k - 1].Left + placed[k - 1].Width + MeasurePillPolicy.MinPillGapPx <= placed[k].Left + 1e-9,
                    $"at {zoom} px/measure pill {placed[k - 1].Slot} runs into pill {placed[k].Slot}");
            }
        }
    }

    /// <summary>Far zoomed out the row still says where you are — it used to be hidden.</summary>
    [Fact]
    public void FarZoomedOut_TheRow_StillHasPills()
    {
        var placed = Plan(Slots(1, 300, 0.25), first: false);
        Assert.NotEmpty(placed);
        Assert.All(placed, p => Assert.True(MeasurePillPolicy.OnStride(p.Slot + 1, MeasurePillPolicy.Stride(0.25, 42))));
    }

    /// <summary>A tempo or meter change shows at every zoom where it has room — i.e. where no
    /// other change sits closer than one pill — even in a measure the stride skips.</summary>
    [Fact]
    public void EveryChange_IsShown_WhenItHasRoom()
    {
        var bpm = new HashSet<int> { 93, 173, 253 };
        var sig = new HashSet<int> { 133 };
        var checkedZooms = 0;
        foreach (var zoom in Zooms)
        {
            // The closest two changes are 40 measures apart; the widest pill is 66 px plus its gap.
            if (40 * zoom < 72) continue;
            var slots = Slots(1, 300, zoom, bpm, sig);
            var shown = Plan(slots).Select(p => slots[p.Slot].MeasureNumber).ToHashSet();
            foreach (var change in bpm.Concat(sig))
                Assert.True(shown.Contains(change), $"at {zoom} px/measure the change at measure {change} is not shown");
            checkedZooms++;
        }

        Assert.True(checkedZooms >= 8, $"only {checkedZooms} zooms had room for every change");
    }

    [Fact]
    public void AChangeInASkippedMeasure_WinsOverTheStrideLabelItHits()
    {
        // 10 px a measure: a 42 px pill needs a stride of 8 (labels on 1, 9, 17 …).
        Assert.Equal(8, MeasurePillPolicy.Stride(10, 42));
        var slots = Slots(1, 40, 10, bpmChanges: new HashSet<int> { 12 });
        var shown = Plan(slots, first: false).Select(p => slots[p.Slot].MeasureNumber).ToList();
        Assert.Contains(12, shown);
        Assert.DoesNotContain(9, shown);
        Assert.Contains(17, shown);
    }

    [Fact]
    public void TheFirstPill_CarriesNumberTempoAndMeter()
    {
        foreach (var zoom in Zooms)
        {
            var placed = Plan(Slots(37, 200, zoom));
            Assert.Equal(0, placed[0].Slot);
            Assert.True(placed[0].ShowSig, $"at {zoom} px/measure the first pill has no meter");
        }
    }

    /// <summary>Labels sit on measure numbers 1, 9, 17 … — never on "whatever is first on
    /// screen" — so scrolling by a few measures does not move them.</summary>
    [Fact]
    public void StrideLabels_StayOnTheSameMeasures_WhileScrolling()
    {
        static HashSet<int> Labels(int first)
        {
            var slots = Slots(first, 120, 10, offset: -(first % 3));
            return Plan(slots, first: false).Select(p => slots[p.Slot].MeasureNumber).ToHashSet();
        }

        var a = Labels(1);
        var b = Labels(4);
        Assert.All(a.Concat(b), n => Assert.True(MeasurePillPolicy.OnStride(n, 8), $"measure {n} is off the stride"));
        foreach (var n in new[] { 17, 25, 33, 41 })
            Assert.True(a.Contains(n) && b.Contains(n), $"measure {n} lost its label when the view scrolled");
    }

    [Theory]
    [InlineData(200, 50, 1)]
    [InlineData(30, 50, 2)]
    [InlineData(10, 42, 8)]
    [InlineData(1, 42, 64)]
    public void TheStride_IsTheSmallestPowerOfTwoThatFits(double measureWidth, double pill, int expected) =>
        Assert.Equal(expected, MeasurePillPolicy.Stride(measureWidth, pill));

    /// <summary>With room, a pill is the full "M17 | 120 BPM" form; cramped, the compact one.</summary>
    [Fact]
    public void ThePillForm_FollowsTheRoom()
    {
        Assert.All(Plan(Slots(1, 20, 400)), p => Assert.True(p.Full));
        // Cramped: compact, except the first (context) and the last (nothing after it).
        Assert.All(Plan(Slots(1, 300, 1)).Skip(1).SkipLast(1), p => Assert.False(p.Full));
    }

    /// <summary>The layer feeds the planner at EVERY zoom: no zoom threshold gates the row.</summary>
    [Fact]
    public void TheMeasureRow_IsNotGatedOnZoom()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Layers", "BackgroundLayer.cs")));
        Assert.DoesNotContain("showBeatText", src, StringComparison.Ordinal);
        Assert.Contains("MeasurePillPolicy.Plan(", src, StringComparison.Ordinal);
    }
}
