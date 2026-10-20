using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Where a strum's SHAPE comes from, and what counts as playing it.
///     <para>
///     <b>The regression.</b> A <see cref="Chord" /> may legally carry no
///     <see cref="Chord.ChordNotes" /> at all and name its shape only through
///     <see cref="Chord.TemplateId" /> — which is precisely what RS-XML does:
///     <c>chordNote</c> children are written only for strings that need per-note
///     techniques, so an ordinary strum has none. <c>LiveNoteMatcher</c> read
///     <c>ChordNotes</c> and nothing else, so such a chord reached judging with an
///     EMPTY pitch set, and an empty pitch set is answered with a flat <c>false</c>:
///     <b>every templated chord in an RS-XML-imported arrangement was an automatic
///     Miss</b>, in the candidate path and in the CQT pull-verify path alike. No
///     amount of correct playing could score one.
///     </para>
///     <para>
///     <b>The second half.</b> A chord tone was matched EXACTLY or at ±12 and
///     nothing else, while a single note has always accepted the range a slide
///     traverses, the range a bend reaches and a harmonic's real overtone. So a
///     natural-harmonic strum (which sounds at +19/+24, not +12) could not verify
///     one of its own tones, and a chord slide was invisible to the pitch window
///     AND to the technique report card — the same shape as the bend/tremolo drop
///     fixed in 0.48, with slide left behind.
///     </para>
/// </summary>
public sealed class ChordShapeVerificationTests
{
    private const int Sr = 44100;

    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static Arrangement NewArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Rhythm };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    /// <summary>G5 as RS-XML writes it: a chord element with a chordId and NO
    /// chordNote children. Low E fret 3 = G2 (43), A fret 5 = D3 (50).</summary>
    private static Arrangement TemplateOnlyPowerChord(int beat = 1)
    {
        var arr = NewArr();
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 4,
            Name = "G5",
            Frets = new Dictionary<int, int> { [0] = 3, [1] = 5 }
        });
        arr.Levels[0].Chords.Add(new Chord
        {
            Position = new FractionalPosition(beat),
            TemplateId = 4
        });
        return arr;
    }

    private static LiveNoteMatcher Loaded(Arrangement arr, LiveNoteMatcher? matcher = null)
    {
        matcher ??= new LiveNoteMatcher();
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    // ── the money test ──────────────────────────────────────────────────────────

    [Fact]
    public void A_chord_that_names_its_shape_only_through_a_template_can_be_hit()
    {
        var matcher = Loaded(TemplateOnlyPowerChord());

        Assert.Equal(new[] { 43, 50 }, matcher.Expected[0].MidiNotes);
        Assert.True(matcher.Expected[0].IsChord, "a two-string template shape IS a chord");

        Span<LivePitch> strum =
        [
            new LivePitch(43, 0f, 1f),
            new LivePitch(50, 0f, 0.7f)
        ];
        var judged = matcher.OfferPitches(strum, timeMs: 500);

        Assert.NotNull(judged);
        Assert.Equal(LivePlayVerdict.Hit, judged!.Value.Verdict);
        Assert.Equal(1, matcher.Stats.Hits);
    }

    /// <summary>The guard: reading the template must not make the matcher
    /// credulous. A chord nobody played is still a Miss.</summary>
    [Fact]
    public void A_template_shape_that_was_not_played_is_still_a_miss()
    {
        var matcher = Loaded(TemplateOnlyPowerChord());

        Span<LivePitch> wrong =
        [
            new LivePitch(49, 0f, 1f),   // Db3 — a tritone off both tones
            new LivePitch(56, 0f, 0.7f)
        ];
        Assert.Null(matcher.OfferPitches(wrong, timeMs: 500));

        var events = matcher.AdvanceTo(playheadMs: 1000);
        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, events[0].Verdict);
    }

    /// <summary>A chord's OWN notes are authoritative — they carry per-string
    /// techniques the template cannot express, so the template must only ever
    /// fill in for a chord that has none.</summary>
    [Fact]
    public void Explicit_chord_notes_win_over_the_template()
    {
        var arr = TemplateOnlyPowerChord();
        var chord = arr.Levels[0].Chords[0];
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 10 }); // D3 = 50
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 12 }); // A3 = 57

        var matcher = Loaded(arr);

        Assert.Equal(new[] { 50, 57 }, matcher.Expected[0].MidiNotes);
    }

    /// <summary>A template id that resolves to nothing must degrade exactly as
    /// before rather than throw — an arrangement can lose its templates.</summary>
    [Fact]
    public void A_dangling_template_id_does_not_throw()
    {
        var arr = TemplateOnlyPowerChord();
        arr.ChordTemplates.Clear();

        var matcher = Loaded(arr);

        Assert.Empty(matcher.Expected[0].MidiNotes);
    }

    // ── end to end, on synthesised audio, through the CQT pull-verifier ─────────

    /// <summary>
    ///     The same chord judged the way a real session judges it: realistic
    ///     plucks (attack transient, 8 partials, decaying envelope) pushed into
    ///     the streaming CQT, pull-verified when the hit window closes. This is
    ///     the path <c>LivePlayViewModel.Tick</c> drives, and it was equally
    ///     blind — <c>LiveCqtVerifier.Verify</c> answers an empty pitch set with
    ///     <c>false</c> too.
    /// </summary>
    [Fact]
    public void The_templated_chord_also_scores_on_the_streaming_CQT_path()
    {
        var audio = Strum(new[] { 43, 50 }, leadInSec: 0.4);
        var verifier = Streamed(audio);
        var matcher = Loaded(TemplateOnlyPowerChord(), new LiveNoteMatcher { ChartVerify = verifier.Verify });

        var events = matcher.AdvanceTo(playheadMs: 900);

        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Hit, events[0].Verdict);
    }

    [Fact]
    public void The_streaming_CQT_path_still_rejects_a_chord_nobody_played()
    {
        var audio = Strum(new[] { 43, 50 }, leadInSec: 0.4);
        var verifier = Streamed(audio);

        var arr = TemplateOnlyPowerChord();
        arr.ChordTemplates[0].Frets = new Dictionary<int, int> { [0] = 9, [1] = 11 }; // Db5, six semitones up
        var matcher = Loaded(arr, new LiveNoteMatcher { ChartVerify = verifier.Verify });

        var events = matcher.AdvanceTo(playheadMs: 900);

        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, events[0].Verdict);
    }

    // ── the chord tone's accepted pitches ───────────────────────────────────────

    /// <summary>Natural harmonics over the 5th-fret node ring TWO OCTAVES AND A
    /// FIFTH above the fretted pitch (+19), which the old ±12 chord test could
    /// not reach — so a harmonic strum verified none of its tones.</summary>
    [Fact]
    public void A_harmonic_strum_verifies_at_the_overtone_the_node_actually_rings()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5, Harmonic = HarmonicType.Normal });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Harmonic = HarmonicType.Normal });
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        // Fretted pitches would be 45 and 50; the 5th-fret node sounds +19.
        Span<LivePitch> ringing =
        [
            new LivePitch(45 + 19, 0f, 1f),
            new LivePitch(50 + 19, 0f, 0.8f)
        ];

        Assert.NotNull(matcher.OfferPitches(ringing, timeMs: 500));
    }

    /// <summary>A strum slides as ONE SHAPE: every tone moves by the same fret
    /// delta, and the pitch window must accept the range it traverses — exactly
    /// as it does for a single sliding note.</summary>
    [Fact]
    public void A_sliding_strum_accepts_the_range_the_shape_traverses()
    {
        var matcher = Loaded(SlidingPowerChord());

        Assert.Equal(4, matcher.Expected[0].SlideSemis);

        Span<LivePitch> midSlide =
        [
            new LivePitch(43 + 2, 0f, 1f),   // halfway between fret 3 and fret 7
            new LivePitch(50 + 2, 0f, 0.8f)
        ];

        Assert.NotNull(matcher.OfferPitches(midSlide, timeMs: 500));
    }

    /// <summary>…and a shape displacement must not become a licence to hit
    /// anything: the widening stops at the slide's destination. Asserted in
    /// Strict voicing practice, where the ±octave tolerance is off, so the
    /// boundary is the slide's and nothing else's.</summary>
    [Fact]
    public void A_sliding_strum_stops_accepting_at_its_destination()
    {
        Span<LivePitch> insideTheSlide =
        [
            new LivePitch(43 + 2, 0f, 1f),
            new LivePitch(50 + 2, 0f, 0.9f)
        ];
        Assert.NotNull(Strictly().OfferPitches(insideTheSlide, timeMs: 500));

        Span<LivePitch> pastTheDestination =
        [
            new LivePitch(43 + 6, 0f, 1f),   // the shape slid to fret 7, not fret 9
            new LivePitch(50 + 6, 0f, 0.9f)
        ];
        Assert.Null(Strictly().OfferPitches(pastTheDestination, timeMs: 500));

        static LiveNoteMatcher Strictly() => Loaded(SlidingPowerChord(),
            new LiveNoteMatcher { ChordStrictness = LiveChordVoicingStrictness.Strict });
    }

    [Fact]
    public void The_report_card_counts_a_chord_slide_as_a_slide()
    {
        var matcher = Loaded(SlidingPowerChord());
        Span<LivePitch> strum = [new LivePitch(43, 0f, 1f), new LivePitch(50, 0f, 0.8f)];
        matcher.OfferPitches(strum, timeMs: 500);

        var tallied = LiveTechniqueTally.Compute(matcher.Expected);

        var slide = Assert.Single(tallied, t => t.Technique == PlayedTechnique.Slide);
        Assert.Equal(1, slide.Hits);
    }

    /// <summary>G5 at frets 3/5 sliding up four frets to 7/9.</summary>
    private static Arrangement SlidingPowerChord()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, SlideTo = 7 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, SlideTo = 9 });
        arr.Levels[0].Chords.Add(chord);
        return arr;
    }

    // ── signal synthesis (real envelopes and partials, not impulses) ────────────

    private static float[] Strum(IReadOnlyList<int> midis, double leadInSec, double durSec = 1.4)
    {
        int lead = (int)(leadInSec * Sr), len = (int)(durSec * Sr);
        var y = new float[lead + len];
        for (var n = 0; n < midis.Count; n++)
        {
            var f0 = 440.0 * Math.Pow(2.0, (midis[n] - 69) / 12.0);
            // Strings of a strum do not start together — the pick crosses them.
            var offset = (int)(n * 0.012 * Sr);
            for (var i = 0; i + offset < len; i++)
            {
                var t = i / (double)Sr;
                double s = 0;
                for (var h = 1; h <= 8; h++)
                {
                    var fh = f0 * h;
                    if (fh > Sr / 2.0) break;
                    s += Math.Sin(2 * Math.PI * fh * t) / h;
                }

                var env = (1.0 - Math.Exp(-t * 400.0)) * Math.Exp(-t * 1.6);
                y[lead + i + offset] += (float)(s * env) * 0.35f;
            }
        }

        return y;
    }

    private static LiveCqtVerifier Streamed(float[] audio)
    {
        var v = LiveCqtVerifier.CreateStreaming(Sr);
        const int chunk = 1600;
        for (var off = 0; off < audio.Length; off += chunk)
            v.Push(audio.AsSpan(off, Math.Min(chunk, audio.Length - off)), off);
        return v;
    }
}
