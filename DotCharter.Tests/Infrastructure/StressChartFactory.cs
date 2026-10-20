using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Synthetic large-chart builder for the 0.14 perf gates (the roadmap's
///     "20k notes / 6 arrangements" stress fixture). Fully synthetic — perf
///     fixtures must never depend on the git-ignored local TestData folder.
///     Everything derives from a real beat grid (timing is grid-relative;
///     a FractionalPosition is meaningless without the Beats timeline) and is
///     generated in Position order, because the renderers' visible-range
///     binary search requires sorted collections. Events, tones, lyrics and
///     showlights scale with chart LENGTH (fixed per-beat cadence) so the
///     viewport-bound gate covers their projections too: an O(total) walk in any
///     of them visits 10x the items on the 10x-longer chart.
/// </summary>
public static class StressChartFactory
{
    public sealed record StressChart(
        List<BeatMarker> Beats,
        List<GuitarNote> Notes,
        List<Chord> Chords,
        List<FretAnchor> Anchors,
        List<HandShape> HandShapes,
        List<EventPoint> EventPoints,
        List<ToneChange> Tones,
        List<LyricPhrase> LyricPhrases,
        List<Showlight> Showlights)
    {
        public double LengthMs => Beats.Count == 0 ? 0 : Beats[^1].Time;
    }

    public static List<BeatMarker> BeatGrid(double lengthMinutes = 10, double bpm = 120)
    {
        var beatMs = 60000.0 / bpm;
        var count = (int)Math.Ceiling(lengthMinutes * 60000.0 / beatMs) + 1;
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * beatMs, Bpm = bpm });
        return beats;
    }

    /// <summary>
    ///     Spreads <paramref name="soundCount" /> sounds evenly over the whole
    ///     grid. Every 10th sound is a 3-string chord with a matching hand
    ///     shape; among plain notes every 8th sustains a quarter beat, every
    ///     16th carries vibrato (exercises the technique-origin scan) and
    ///     every 32nd links next (exercises the linked-target hash). Anchors
    ///     land every 4 beats. Chart length stays constant regardless of
    ///     <paramref name="soundCount" /> so beat-dependent costs cancel out
    ///     when comparing two densities.
    /// </summary>
    public static StressChart Chart(int soundCount, double lengthMinutes = 10, double bpm = 120)
    {
        var beats = BeatGrid(lengthMinutes, bpm);
        var usableBeats = beats.Count - 2;
        var perBeat = Math.Max(1, (int)Math.Ceiling(soundCount / (double)usableBeats));

        var notes = new List<GuitarNote>(soundCount);
        var chords = new List<Chord>(soundCount / 10 + 1);
        var handShapes = new List<HandShape>(soundCount / 10 + 1);
        var quarterBeat = new FractionalPosition(new Fraction(1, 4));
        var halfBeat = new FractionalPosition(new Fraction(1, 2));

        for (var i = 0; i < soundCount; i++)
        {
            var position = new FractionalPosition(i / perBeat, new Fraction(i % perBeat, perBeat));

            if (i % 10 == 9)
            {
                var chord = new Chord { Position = position };
                for (var s = 0; s < 3; s++)
                {
                    chord.ChordNotes.Add(new ChordNote
                    {
                        StringIndex = s,
                        Fret = 1 + i % 12,
                        Sustain = i % 20 == 19 ? quarterBeat : FractionalPosition.Zero
                    });
                }

                chords.Add(chord);
                handShapes.Add(new HandShape
                {
                    Position = position,
                    EndPosition = position + halfBeat
                });
            }
            else
            {
                notes.Add(new GuitarNote
                {
                    Position = position,
                    StringIndex = i % 6,
                    Fret = i % 25,
                    Sustain = i % 8 == 7 ? quarterBeat : FractionalPosition.Zero,
                    Vibrato = i % 16 == 15,
                    LinkNext = i % 32 == 31
                });
            }
        }

        var anchors = new List<FretAnchor>();
        for (var b = 0; b < usableBeats; b += 4)
            anchors.Add(new FretAnchor(new FractionalPosition(b), 1 + b / 4 % 20));

        // Timeline decorations at a fixed per-beat cadence (counts scale with
        // length, density stays constant): a phrase event every 8 beats with
        // a section every 32nd beat (sections sparse, phrases dense — the
        // shape the sticky section/phrase prefix scan has to handle), a tone
        // change every 32 beats, a 4-word lyric phrase every 8 beats (second
        // word marked as word part), and a showlight every 8 beats cycling
        // fog -> beams -> lasers so every lane carries sticky state.
        var eventPoints = new List<EventPoint>();
        var tones = new List<ToneChange>();
        var lyricPhrases = new List<LyricPhrase>();
        var showlights = new List<Showlight>();
        var half = new Fraction(1, 2);
        for (var b = 0; b < usableBeats; b += 8)
        {
            eventPoints.Add(new EventPoint
            {
                Position = new FractionalPosition(b),
                Phrase = PhraseNames[b / 8 % PhraseNames.Length],
                Section = b % 32 == 0 ? SectionNames[b / 32 % SectionNames.Length] : ""
            });

            if (b % 32 == 0)
            {
                tones.Add(new ToneChange
                {
                    Position = new FractionalPosition(b),
                    ToneName = b % 64 == 0 ? "Lead" : "Rhythm"
                });
            }

            var phrase = new LyricPhrase
            {
                Position = new FractionalPosition(b),
                EndPosition = new FractionalPosition(b + 4)
            };
            for (var w = 0; w < 4; w++)
            {
                phrase.Lyrics.Add(new LyricEvent
                {
                    Position = new FractionalPosition(b + w),
                    EndPosition = new FractionalPosition(b + w, half),
                    Text = LyricWords[w],
                    IsWordPart = w == 1
                });
            }

            lyricPhrases.Add(phrase);

            // 24 = FOG_GREEN, 48 = BEAMS_GREEN, 66 = LASERS_ON.
            showlights.Add(new Showlight
            {
                Position = new FractionalPosition(b),
                Note = (b / 8 % 3) switch { 0 => 24, 1 => 48, _ => 66 }
            });
        }

        return new StressChart(beats, notes, chords, anchors, handShapes,
            eventPoints, tones, lyricPhrases, showlights);
    }

    private static readonly string[] PhraseNames = ["intro", "verse", "bridge", "chorus"];
    private static readonly string[] SectionNames = ["Intro", "Verse", "Chorus", "Solo"];
    private static readonly string[] LyricWords = ["some", "syl-", "lable", "line"];
}
