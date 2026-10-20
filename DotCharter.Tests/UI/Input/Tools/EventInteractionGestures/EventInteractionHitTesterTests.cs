using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Input.Tools;
using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.Tests.UI.Input.Tools.EventInteractionGestures;

public class EventInteractionHitTesterTests
{
    private static List<BeatMarker> Beats(int n, double spacingMs = 500)
    {
        var b = new List<BeatMarker>();
        for (var i = 0; i < n; i++) b.Add(new BeatMarker { Time = i * spacingMs, Bpm = 120 });
        return b;
    }

    private static EventPoint EpAt(IList<BeatMarker> beats, double ms, string section = "S", string phrase = "")
    {
        return new EventPoint
        {
            Position = TimelineMath.PositionFromMs(ms, beats),
            Section = section,
            Phrase = phrase
        };
    }

    private static ToneChange ToneAt(IList<BeatMarker> beats, double ms, string name)
    {
        return new ToneChange
        {
            Position = TimelineMath.PositionFromMs(ms, beats),
            ToneName = name
        };
    }

    private static LyricEvent LyAt(IList<BeatMarker> beats, double startMs, double endMs, string text = "la")
    {
        return new LyricEvent
        {
            Position = TimelineMath.PositionFromMs(startMs, beats),
            EndPosition = TimelineMath.PositionFromMs(endMs, beats),
            Text = text
        };
    }

    [Fact]
    public void HitTestEvent_OnlyMatchesSectionRowOrPhraseRow()
    {
        var beats = Beats(10);
        var eps = new[] { EpAt(beats, 1000) };

        Assert.Null(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Tone, 1000, 1.0));
        Assert.Null(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Lyric, 1000, 1.0));
        Assert.Null(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Other, 1000, 1.0));
        Assert.Same(eps[0],
            EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 1000, 1.0));
    }

    [Fact]
    public void HitTestEvent_RequiresMatchingFieldForRowType()
    {
        var beats = Beats(10);
        // Pure section EP: section set, phrase empty
        var sectionOnly = new[] { EpAt(beats, 1000, section: "S", phrase: "") };
        Assert.NotNull(EventInteractionHitTester.HitTestEvent(sectionOnly, beats, EventRowType.Section, 1000, 1.0));
        Assert.Null(EventInteractionHitTester.HitTestEvent(sectionOnly, beats, EventRowType.Phrase, 1000, 1.0));

        var phraseOnly = new[] { EpAt(beats, 1000, section: "", phrase: "P") };
        Assert.NotNull(EventInteractionHitTester.HitTestEvent(phraseOnly, beats, EventRowType.Phrase, 1000, 1.0));
        Assert.Null(EventInteractionHitTester.HitTestEvent(phraseOnly, beats, EventRowType.Section, 1000, 1.0));
    }

    [Fact]
    public void HitTestEvent_BackTolerance10Px_ForwardTolerance25Px()
    {
        var beats = Beats(10);
        var eps = new[] { EpAt(beats, 1000) };
        const double pxPerMs = 1.0;

        // 1000ms, back 10px == 10ms back; forward 25px == 25ms forward
        Assert.Null(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 989, pxPerMs));
        Assert.NotNull(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 990, pxPerMs));
        Assert.NotNull(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 1025, pxPerMs));
        Assert.Null(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 1026, pxPerMs));
    }

    [Fact]
    public void HitTestEvent_PicksLastMatching_WhenMultipleOverlap()
    {
        var beats = Beats(10);
        var first = EpAt(beats, 1000, "A");
        var second = EpAt(beats, 1010, "B");
        var eps = new[] { first, second };

        var hit = EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 1010, 1.0);
        Assert.Same(second, hit);
    }

    [Fact]
    public void HitTestEvent_NonPositivePixelsPerMs_ReturnsNull()
    {
        var beats = Beats(10);
        var eps = new[] { EpAt(beats, 1000) };
        Assert.Null(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 1000, 0));
        Assert.Null(EventInteractionHitTester.HitTestEvent(eps, beats, EventRowType.Section, 1000, -1));
    }

    [Fact]
    public void HitTestTone_OnlyMatchesToneRow()
    {
        var beats = Beats(10);
        var tones = new[] { ToneAt(beats, 1000, "lead") };

        Assert.Same(tones[0], EventInteractionHitTester.HitTestTone(tones, beats, EventRowType.Tone, 1000, 1.0));
        Assert.Null(EventInteractionHitTester.HitTestTone(tones, beats, EventRowType.Section, 1000, 1.0));
        Assert.Null(EventInteractionHitTester.HitTestTone(tones, beats, EventRowType.Phrase, 1000, 1.0));
        Assert.Null(EventInteractionHitTester.HitTestTone(tones, beats, EventRowType.Lyric, 1000, 1.0));
    }

    [Fact]
    public void HitTestTone_PicksLastMatching_WhenMultipleOverlap()
    {
        var beats = Beats(10);
        var t1 = ToneAt(beats, 1000, "rhythm");
        var t2 = ToneAt(beats, 1010, "lead");
        var tones = new[] { t1, t2 };
        Assert.Same(t2, EventInteractionHitTester.HitTestTone(tones, beats, EventRowType.Tone, 1010, 1.0));
    }

    [Fact]
    public void HitTestLyric_NullPhrasesOrEmptyBeats_ReturnsNull()
    {
        Assert.Equal((null, null),
            EventInteractionHitTester.HitTestLyric(null, Beats(0), 100, 1.0));
        LyricPhrase[] phrases = [];
        Assert.Equal((null, null),
            EventInteractionHitTester.HitTestLyric(phrases, Beats(0), 100, 1.0));
    }

    [Fact]
    public void HitTestLyric_PillSpan_HitWithSlack()
    {
        var beats = Beats(10);
        var lyric = LyAt(beats, 1000, 1500, "hi");
        var phrase = new LyricPhrase { Lyrics = { lyric } };
        var phrases = new List<LyricPhrase> { phrase };
        const double pxPerMs = 1.0; // 8px slack each side

        var (hit, ph) = EventInteractionHitTester.HitTestLyric(phrases, beats, 1000, pxPerMs);
        Assert.Same(lyric, hit);
        Assert.Same(phrase, ph);

        // Within slack
        Assert.Same(lyric, EventInteractionHitTester.HitTestLyric(phrases, beats, 992, pxPerMs).Lyric);
        Assert.Same(lyric, EventInteractionHitTester.HitTestLyric(phrases, beats, 1508, pxPerMs).Lyric);

        // Just outside slack
        Assert.Null(EventInteractionHitTester.HitTestLyric(phrases, beats, 991, pxPerMs).Lyric);
        Assert.Null(EventInteractionHitTester.HitTestLyric(phrases, beats, 1509, pxPerMs).Lyric);
    }

    [Fact]
    public void HitTestLyric_DegenerateSpan_FallbackTo200ms()
    {
        var beats = Beats(10);
        // EndPosition <= Position → fallback to 200ms width
        var lyric = LyAt(beats, 1000, 1000);
        var phrase = new LyricPhrase { Lyrics = { lyric } };
        var phrases = new List<LyricPhrase> { phrase };

        Assert.Same(lyric,
            EventInteractionHitTester.HitTestLyric(phrases, beats, 1199, 1.0).Lyric);
        Assert.Null(
            EventInteractionHitTester.HitTestLyric(phrases, beats, 1209, 1.0).Lyric);
    }

    [Fact]
    public void HitTestLyric_PicksTopmostWhenOverlapping()
    {
        var beats = Beats(10);
        var bottom = LyAt(beats, 1000, 1500, "bottom");
        var top = LyAt(beats, 1000, 1500, "top");
        var phrase = new LyricPhrase { Lyrics = { bottom, top } };
        var phrases = new List<LyricPhrase> { phrase };

        // Newest-first walk hits the lyric drawn on top
        Assert.Same(top, EventInteractionHitTester.HitTestLyric(phrases, beats, 1100, 1.0).Lyric);
    }

    /// <summary>
    ///     A lyric pill is DRAWN at least
    ///     <see cref="LyricPillMetrics.MinPillWidthPx" /> wide
    ///     (<c>EventLayer.DrawLyrics</c>) so a short syllable is still a target.
    ///     The hit test read the syllable's DURATION alone, so at any real
    ///     editing zoom most of a visible pill was not clickable: at 0.2 px/ms a
    ///     25 ms syllable draws an 18 px chip over a 5 px span, and clicking its
    ///     right half fell through to the row handler — which CREATES a lyric.
    /// </summary>
    [Fact]
    public void HitTestLyric_ShortSyllable_IsClickableAcrossTheWholeDrawnPill()
    {
        var beats = Beats(10);
        const double pxPerMs = 0.2; // 8 px slack = 40 ms
        const double durationMs = 25; // 5 px drawn -> clamped to the 18 px floor
        var lyric = LyAt(beats, 1000, 1000 + durationMs, "the");
        var phrase = new LyricPhrase { Lyrics = { lyric } };
        var phrases = new List<LyricPhrase> { phrase };

        var drawnWidthMs = LyricPillMetrics.MinPillWidthPx / pxPerMs; // 90 ms
        Assert.True(durationMs * pxPerMs < LyricPillMetrics.MinPillWidthPx,
            "the syllable is no longer short enough for the clamp to apply — this case would be vacuous");

        // The pill's own right edge, 1 ms inside it: drawn, and now clickable.
        Assert.Same(lyric,
            EventInteractionHitTester.HitTestLyric(phrases, beats, 1000 + drawnWidthMs - 1, pxPerMs).Lyric);

        // …and the slack still ends where it always did, one slack past the
        // pill — a widened target must not swallow the next syllable's column.
        Assert.Null(
            EventInteractionHitTester.HitTestLyric(phrases, beats, 1000 + drawnWidthMs + 41, pxPerMs).Lyric);
    }

    /// <summary>
    ///     The layer stops a pill <see cref="LyricPillLayout.GapPx" /> before the next syllable
    ///     (<c>LyricPillLayout.Layout</c>, since 2026-09-23), and the click target has to stop there
    ///     too. An RS-XML import keeps each syllable's own length, so an overlong syllable followed
    ///     by a short one is ordinary input; before this the blank row past the drawn pill still
    ///     selected, dragged or right-click-DELETED the first syllable.
    /// </summary>
    [Fact]
    public void HitTestLyric_AnOverlongSyllable_StopsWhereItsPillIsDrawn()
    {
        var beats = Beats(10);
        var first = LyAt(beats, 1000, 2000, "A");
        var next = LyAt(beats, 1100, 1200, "B");
        var phrases = new List<LyricPhrase> { new() { Lyrics = { first, next } } };
        const double pxPerMs = 1.0;

        Assert.Same(first, EventInteractionHitTester.HitTestLyric(phrases, beats, 1050, pxPerMs).Lyric);
        Assert.Same(next, EventInteractionHitTester.HitTestLyric(phrases, beats, 1150, pxPerMs).Lyric);
        // Well past both drawn pills (A ends at 1098 px, B's slack at 1208): blank row.
        Assert.Null(EventInteractionHitTester.HitTestLyric(phrases, beats, 1500, pxPerMs).Lyric);
    }

    /// <summary>…and a phrase's LAST syllable stops at the next phrase's region, as drawn
    /// (<see cref="LyricPillLayout.LastPillLimit" />).</summary>
    [Fact]
    public void HitTestLyric_APhrasesLastSyllable_StopsBeforeTheNextPhrase()
    {
        var beats = Beats(10);
        var last = LyAt(beats, 1000, 2000, "A");
        var nextPhraseFirst = LyAt(beats, 1300, 1400, "B");
        var phrases = new List<LyricPhrase>
        {
            new() { Lyrics = { last } },
            new() { Lyrics = { nextPhraseFirst } },
        };
        const double pxPerMs = 1.0;

        Assert.Same(last, EventInteractionHitTester.HitTestLyric(phrases, beats, 1200, pxPerMs).Lyric);
        Assert.Null(EventInteractionHitTester.HitTestLyric(phrases, beats, 1700, pxPerMs).Lyric);
    }

    /// <summary>The degenerate-end fallback is the same number on both sides.</summary>
    [Fact]
    public void HitTestLyric_DegenerateSpan_UsesTheLayersOwnFallback()
    {
        var beats = Beats(10);
        var lyric = LyAt(beats, 1000, 1000);
        var phrases = new List<LyricPhrase> { new() { Lyrics = { lyric } } };

        Assert.Same(lyric,
            EventInteractionHitTester.HitTestLyric(phrases, beats,
                1000 + LyricPillMetrics.DefaultDurationMs - 1, 1.0).Lyric);
    }
}
