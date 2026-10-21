using Avalonia.Media;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A vocal phrase in the 2D lyrics lane is ONE region — a rounded band behind its syllables —
///     since 2026-09-23. It replaced "[" / "]" bracket ticks: the owner found them dated, and the
///     closing tick sat at the phrase's end TIME, inside or short of a last pill widened to fit its
///     text. Same view-model-free harness as <see cref="Timeline2DBudgetGateTests" />: a real
///     <see cref="FrameSnapshot" /> through the real layer into a plain <see cref="TimelineBatcher" />,
///     whose solid stream is 6 floats per vertex (x, y, r, g, b, a), so the region is found by colour.
///     No GPU and no font atlas (text measures 0).
/// </summary>
public sealed class EventLayerLyricPhraseMarkerTests
{
    private const double Zoom = 0.2;
    private const double ViewWidth = 1920;

    /// <summary>120 BPM grid: one beat every 500 ms.</summary>
    private static List<BeatMarker> Beats(int count = 32) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static LyricPhrase PhraseAt(int startBeat, int endBeat, params string[] words)
    {
        var phrase = new LyricPhrase
        {
            Position = new FractionalPosition(startBeat),
            EndPosition = new FractionalPosition(endBeat)
        };
        for (var i = 0; i < words.Length; i++)
        {
            phrase.Lyrics.Add(new LyricEvent
            {
                Position = new FractionalPosition(startBeat + i),
                EndPosition = new FractionalPosition(startBeat + i + 1),
                Text = words[i]
            });
        }

        return phrase;
    }

    private static TimelineBatcher Render(IList<LyricPhrase> phrases, bool showLyrics = true)
    {
        var beats = Beats();
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = [],
            ActiveChords = [],
            ActiveAnchors = [],
            ActiveHandShapes = [],
            EventPoints = [],
            Tones = [],
            LyricPhrases = phrases,
            Showlights = []
        };

        var snapshot = new FrameSnapshot { StringCount = 6 };
        FrameSnapshot.BuildSnapshot(0, ViewWidth / Zoom, 0, snapshot, ctx, []);

        var rc = new ChartRenderContext
        {
            Width = ViewWidth,
            Height = 500,
            CurrentZoom = Zoom,
            ViewStartMs = 0,
            ViewEndMs = ViewWidth / Zoom,
            CurrentTimeMs = 0,
            StringCount = 6,
            DrawLabels = true,
            RendererPixelsPerMs = Zoom,
            Ribbon = new RibbonLayout(showLyrics ? RibbonLanes.All : RibbonLanes.All & ~RibbonLanes.Lyrics),
            Snapshot = snapshot
        };

        var batcher = new TimelineBatcher();
        new EventLayer(new PinnedLabelHitRegistry(), new object()).Render(batcher, rc);
        batcher.FinalizeFrame();
        return batcher;
    }

    /// <summary>Every x of the solid vertices painted in <paramref name="colour" />, sorted.</summary>
    private static List<float> XsOf(TimelineBatcher batcher, Color colour, out List<float> ys)
    {
        var xs = new List<float>();
        ys = [];
        for (var v = 0; v < batcher.SolidVertexCount; v++)
        {
            var o = v * 6;
            if (Math.Abs(batcher.SolidBuffer[o + 2] - colour.R / 255f) >= 0.004f ||
                Math.Abs(batcher.SolidBuffer[o + 3] - colour.G / 255f) >= 0.004f ||
                Math.Abs(batcher.SolidBuffer[o + 4] - colour.B / 255f) >= 0.004f ||
                Math.Abs(batcher.SolidBuffer[o + 5] - colour.A / 255f) >= 0.004f)
            {
                continue;
            }

            xs.Add(batcher.SolidBuffer[o]);
            ys.Add(batcher.SolidBuffer[o + 1]);
        }

        xs.Sort();
        return xs;
    }

    /// <summary>EventLayer's lyric chip fill (unselected, unhovered).</summary>
    private static readonly Color LyricChipFill = EventLayer.OnLane(EventLayer.LyricFill);

    private static (float Min, float Max) RegionSpan(TimelineBatcher batcher)
    {
        var xs = XsOf(batcher, EventLayer.LyricPhraseRegionFill, out _);
        Assert.True(xs.Count > 0, "no phrase region was drawn at all");
        return (xs[0], xs[^1]);
    }

    [Fact]
    public void APhrase_IsOneRegionFromItsFirstSyllableToItsLastSyllablesEnd()
    {
        // Two one-beat syllables from beat 2: 1000..2000 ms = 200..400 px.
        var (min, max) = RegionSpan(Render([PhraseAt(2, 6, "la", "la")]));

        Assert.Equal(200f - LyricPillLayout.RegionPadPx, min, 1);
        Assert.Equal(400f + LyricPillLayout.RegionPadPx, max, 1);
    }

    /// <summary>The fault the brackets had: a last pill wider than its syllable (here the
    /// minimum width) ran past the closing tick. The region ends past the pill.</summary>
    [Fact]
    public void ALastPillWiderThanItsSyllable_StaysInsideTheRegion()
    {
        var phrase = new LyricPhrase { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(2) };
        phrase.Lyrics.Add(new LyricEvent
        {
            // A 50 ms syllable is 10 px; its pill is the 18 px minimum.
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(2, new Fraction(1, 10)), Text = "a"
        });

        var (_, max) = RegionSpan(Render([phrase]));
        Assert.True(max >= 200f + LyricPillMetrics.MinPillWidthPx + LyricPillLayout.RegionPadPx - 0.5f,
            $"the region ends at {max}, before the pill that ends at {200f + LyricPillMetrics.MinPillWidthPx}");
    }

    [Fact]
    public void TheRegionStaysInsideTheLyricRow_NeverFullHeight()
    {
        // Full-height lines mean sections / guitar phrases; a VOCAL phrase stays in its row.
        var batcher = Render([PhraseAt(2, 6, "la", "la")]);
        var ribbon = new RibbonLayout(RibbonLanes.All);
        XsOf(batcher, EventLayer.LyricPhraseRegionFill, out var ys);

        Assert.NotEmpty(ys);
        foreach (var y in ys)
            Assert.InRange(y, ribbon.LyricY, ribbon.LyricY + RibbonLayout.RowHeight);
    }

    /// <summary>The region follows the SYLLABLES, not the phrase container's stored bounds — nothing
    /// updates those when auto-sync or a drag moves the pills (owner screenshot, 2026-08-11).</summary>
    [Fact]
    public void TheRegion_FollowsTheSyllables_NotTheStaleContainerBounds()
    {
        var phrase = new LyricPhrase { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(12) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(5), Text = "shou"
        });
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(5), EndPosition = new FractionalPosition(6), Text = "wa"
        });

        // Syllables span beats 4..6 = 2000..3000 ms = 400..600 px.
        var (min, max) = RegionSpan(Render([phrase]));
        Assert.Equal(400f - LyricPillLayout.RegionPadPx, min, 1);
        Assert.Equal(600f + LyricPillLayout.RegionPadPx, max, 1);
    }

    [Fact]
    public void TwoPhrasesBackToBack_KeepTheirRegionsApart()
    {
        var batcher = Render([PhraseAt(2, 4, "la", "la"), PhraseAt(4, 6, "da", "da")]);
        var xs = XsOf(batcher, EventLayer.LyricPhraseRegionFill, out _);

        // The first region ends before the second begins: nothing is painted in the gap.
        var secondStart = 400f - LyricPillLayout.RegionPadPx;
        Assert.DoesNotContain(xs, x => x > secondStart - LyricPillLayout.GapPx + 0.01f && x < secondStart - 0.01f);
        Assert.Contains(xs, x => Math.Abs(x - secondStart) < 0.5f);

        // …and the first phrase's last pill ends inside the first region, not under the second.
        var firstEnd = xs.Where(x => x < secondStart - 0.01f).Max();
        var pills = XsOf(batcher, LyricChipFill, out _);
        Assert.True(pills.Where(x => x < secondStart).Max() <= firstEnd + 0.01f,
            $"the first phrase's last pill runs past its region, which ends at {firstEnd}");
    }

    [Fact]
    public void HidingTheLyricsRow_HidesTheRegionsToo()
    {
        var batcher = Render([PhraseAt(2, 6, "la", "la")], showLyrics: false);
        Assert.Empty(XsOf(batcher, EventLayer.LyricPhraseRegionFill, out _));
    }
}
