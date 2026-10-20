namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Builds the expected-sound timeline live judging runs against: every note and chord
///     of the chosen difficulty level(s) as an <see cref="ExpectedSound" />, sorted by chart
///     time, and the phrase spans a leveled run picks its levels over.
///     <para>
///         The build is all this does. <see cref="LiveNoteMatcher" /> owns the list it fills,
///         the cursor and the stats, and keeps the carry-over a mid-song reload needs (judged
///         verdicts, the levels of phrase iterations already played) on its side. Runs once
///         per song or per difficulty reload, never on the tick path.
///     </para>
/// </summary>
public sealed class LiveExpectedTimeline
{
    // Scratch for template-resolved chord shapes — reused across builds.
    private readonly List<ChordNote> _templateShape = [];

    /// <summary>Fills <paramref name="expected" /> from one arrangement level (whole song at a
    /// single difficulty), shifted by <paramref name="transposeSemitones" />.</summary>
    public void Build(List<ExpectedSound> expected, Arrangement arrangement, ChartLevel level,
        IList<BeatMarker> beats, int transposeSemitones)
    {
        expected.Clear();

        var levelIndex = Math.Max(0, arrangement.Levels.IndexOf(level));
        AppendLevelRegion(expected, arrangement, level, beats,
            double.NegativeInfinity, double.PositiveInfinity, levelIndex, -1, transposeSemitones);

        expected.Sort(static (a, b) => a.TimeMs.CompareTo(b.TimeMs));
        ClampContourWindows(expected);
    }

    /// <summary>
    ///     Fills <paramref name="expected" /> phrase by phrase, asking
    ///     <paramref name="selectLevel"/> which difficulty level to load for
    ///     each phrase iteration (Rocksmith-style leveled play). Charts
    ///     without phrase data fall back to one whole-song span at the
    ///     highest available level; an arrangement without levels leaves it empty.
    /// </summary>
    public void Build(List<ExpectedSound> expected, Arrangement arrangement, IList<BeatMarker> beats,
        LiveDifficultySelector selectLevel, int transposeSemitones)
    {
        expected.Clear();
        if (arrangement.Levels.Count == 0) return;

        var spans = BuildPhraseSpans(arrangement, beats);
        if (spans.Count == 0)
        {
            var idx = arrangement.Levels.Count - 1;
            AppendLevelRegion(expected, arrangement, arrangement.Levels[idx], beats,
                double.NegativeInfinity, double.PositiveInfinity, idx, -1, transposeSemitones);
        }
        else
        {
            foreach (var span in spans)
            {
                var idx = LevelIndexFor(span, selectLevel, arrangement);
                AppendLevelRegion(expected, arrangement, arrangement.Levels[idx], beats,
                    span.StartMs, span.EndMs, idx, span.IterationIndex, transposeSemitones);
            }
        }

        expected.Sort(static (a, b) => a.TimeMs.CompareTo(b.TimeMs));
        ClampContourWindows(expected);
    }

    /// <summary>The index into <c>arrangement.Levels</c> a span is judged at: the selector's pick,
    /// clamped to the phrase's max difficulty. One rule for the matcher and for what the highway
    /// draws (<see cref="DynamicDifficultyComposite" />), so the two cannot disagree.</summary>
    public static int LevelIndexFor(LivePhraseSpan span, LiveDifficultySelector selectLevel, Arrangement arrangement) =>
        Math.Clamp(selectLevel(span), 0, Math.Min(span.MaxDifficulty, arrangement.Levels.Count - 1));

    /// <summary>
    ///     The arrangement's phrase iterations as consecutive time spans
    ///     (each ends where the next begins; the last one is open-ended).
    ///     Empty when the chart carries no phrase iterations.
    /// </summary>
    public static List<LivePhraseSpan> BuildPhraseSpans(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var iterations = new List<(double Ms, int PhraseId)>(arrangement.PhraseIterations.Count);
        foreach (var pi in arrangement.PhraseIterations)
            iterations.Add((pi.Position.GetPositionMs(beats), pi.PhraseId));
        iterations.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));

        var spans = new List<LivePhraseSpan>(iterations.Count);
        for (var i = 0; i < iterations.Count; i++)
        {
            var (ms, phraseId) = iterations[i];
            var end = i + 1 < iterations.Count ? iterations[i + 1].Ms : double.PositiveInfinity;
            var name = phraseId >= 0 && phraseId < arrangement.Phrases.Count
                ? arrangement.Phrases[phraseId]
                : $"phrase{phraseId}";
            // A phrase without explicit metadata is assumed fully leveled —
            // clamping it to the default MaxDifficulty of 0 would silently
            // drop every chart without PhraseInfos to level 0.
            var maxDifficulty = arrangement.PhraseInfos.TryGetValue(name, out var info)
                ? info.MaxDifficulty
                : arrangement.Levels.Count - 1;
            spans.Add(new LivePhraseSpan(i, name, ms, end, Math.Max(0, maxDifficulty)));
        }

        return spans;
    }

    /// <summary>
    ///     Appends one level's notes/chords inside [startMs, endMs).
    ///     <para>
    ///         A note or chord marked <c>Ignore</c> is NOT an expected sound: it
    ///         is never judged, never a miss, and never in <c>TotalSounds</c> or
    ///         the accuracy denominator. That is Rocksmith's rule (the SNG
    ///         exporter keeps them out of <c>MaxNotesAndChordsReal</c>), and
    ///         <c>ChartSpacingEnforcer.DeriveIgnoreFlags</c> sets the flag itself
    ///         on fret 23+ and sustained 7th-fret harmonics, which no detector
    ///         can score. The highway still draws them — it reads the chart, not
    ///         this list. Only the CHORD's flag counts; a chord note's own
    ///         <c>Ignore</c> leaves the strum judged, as the SNG mask does.
    ///     </para>
    /// </summary>
    private void AppendLevelRegion(List<ExpectedSound> expected, Arrangement arrangement, ChartLevel level,
        IList<BeatMarker> beats, double startMs, double endMs, int levelIndex, int phraseIterationIndex,
        int transposeSemitones)
    {
        foreach (var note in level.Notes)
        {
            if (note.Ignore) continue;
            var timeMs = note.Position.GetPositionMs(beats);
            if (timeMs < startMs || timeMs >= endMs) continue;
            var midi = ExpectedMidi(arrangement, note.StringIndex, note.Fret, transposeSemitones);
            var slideToMidi = note.SlideTo is { } st && !note.UnpitchedSlide
                ? ExpectedMidi(arrangement, note.StringIndex, st, transposeSemitones)
                : null;
            expected.Add(new ExpectedSound
            {
                TimeMs = timeMs,
                MidiNotes = midi is { } m ? [m] : [],
                IsChord = false,
                PalmMute = note.Mute == MuteType.Palm,
                FullMute = note.Mute == MuteType.Full,
                Harmonic = note.Harmonic != HarmonicType.None,
                PinchHarmonic = note.Harmonic == HarmonicType.Pinch,
                SlapOrPop = note.BassPicking != BassPickingType.None,
                Legato = note.Hopo != HopoType.None,
                SlideToMidi = slideToMidi,
                UnpitchedSlide = note.UnpitchedSlide,
                MaxBendSemis = LivePitchAcceptance.MaxBendSemis(note.BendValues),
                Tremolo = note.Tremolo,
                Vibrato = note.Vibrato,
                TechniqueContour = midi is { } sounding && note.Harmonic == HarmonicType.None
                    ? ContourOf(note, timeMs, beats, slideToMidi - sounding)
                    : default,
                Label = $"s{note.StringIndex} f{note.Fret}",
                StringIndex = note.StringIndex,
                StringMask = midi != null ? 1 << note.StringIndex : 0,
                Fret = note.Fret,
                LevelIndex = levelIndex,
                PhraseIterationIndex = phraseIterationIndex
            });
        }

        foreach (var chord in level.Chords)
        {
            if (chord.Ignore) continue;
            var timeMs = chord.Position.GetPositionMs(beats);
            if (timeMs < startMs || timeMs >= endMs) continue;
            var chordNotes = ChordShape(arrangement, chord);
            var midis = new List<int>(chordNotes.Count);
            var palm = false;
            var full = chordNotes.Count > 0;
            var slapOrPop = false;
            var legato = false;
            var harmonic = false;
            var pinch = false;
            // A chord's bend, tremolo and SLIDE are carried here, not dropped, because a
            // bent, tremolo-picked or sliding strum would be invisible to the
            // pitch window AND to the report card. They fold the same way every
            // other chord flag does: ANY string carrying it makes the strum carry
            // it. A chord slide moves the WHOLE SHAPE by one fret delta, so it is
            // carried as a signed semitone displacement rather than as a single
            // absolute target (which would widen the wrong tones — see
            // ExpectedSound.SlideSemis).
            var tremolo = false;
            var vibrato = false;
            var bendSemis = 0;
            var slideSemis = 0;
            var loString = int.MaxValue;
            var loFret = 0;
            var mask = 0;
            foreach (var cn in chordNotes)
            {
                palm |= cn.Mute == MuteType.Palm;
                full &= cn.Mute == MuteType.Full;
                slapOrPop |= cn.BassPicking != BassPickingType.None;
                legato |= cn.Hopo != HopoType.None;
                harmonic |= cn.Harmonic != HarmonicType.None;
                pinch |= cn.Harmonic == HarmonicType.Pinch;
                tremolo |= cn.Tremolo;
                vibrato |= cn.Vibrato;
                bendSemis = Math.Max(bendSemis, LivePitchAcceptance.MaxBendSemis(cn.BendValues));
                if (cn.SlideTo is { } st && !cn.UnpitchedSlide && Math.Abs(st - cn.Fret) > Math.Abs(slideSemis))
                    slideSemis = st - cn.Fret;
                loString = Math.Min(loString, cn.StringIndex);
                if (cn.Fret > 0 && (loFret == 0 || cn.Fret < loFret)) loFret = cn.Fret;
                if (cn.Mute == MuteType.Full) continue;
                if (ExpectedMidi(arrangement, cn.StringIndex, cn.Fret, transposeSemitones) is not { } m) continue;
                midis.Add(m);
                mask |= 1 << cn.StringIndex;
            }

            expected.Add(new ExpectedSound
            {
                TimeMs = timeMs,
                MidiNotes = midis.ToArray(),
                IsChord = midis.Count > 1,
                PalmMute = palm,
                FullMute = full,
                SlapOrPop = slapOrPop,
                Legato = legato,
                Harmonic = harmonic,
                PinchHarmonic = pinch,
                Tremolo = tremolo,
                Vibrato = vibrato,
                MaxBendSemis = bendSemis,
                SlideSemis = slideSemis,
                Label = $"chord@{chord.Position}",
                StringIndex = loString == int.MaxValue ? -1 : loString,
                StringMask = mask,
                Fret = loFret,
                LevelIndex = levelIndex,
                PhraseIterationIndex = phraseIterationIndex
            });
        }
    }

    /// <summary>
    /// What a single note's pitch contour must show: its bend (largest step and when
    ///     the chart first reaches it), its slide interval and its vibrato, followed for the
    ///     note's sustain — at least long enough for a bend to arrive.
    ///     <see cref="ClampContourWindows" /> then stops the window at the next sound.
    /// </summary>
    private static TechniqueContourExpectation ContourOf(GuitarNote note, double timeMs, IList<BeatMarker> beats,
        int? slideSemis)
    {
        var techniques = ContourTechniques.None;
        double bendSemis = 0, peakAfterMs = 0;
        foreach (var b in note.BendValues)
        {
            if (b.Step <= bendSemis) continue;
            bendSemis = b.Step;
            peakAfterMs = Math.Max(0, b.Position.GetPositionMs(beats) - timeMs);
        }

        if (bendSemis > 0) techniques |= ContourTechniques.Bend;
        if (slideSemis is { } semis && semis != 0) techniques |= ContourTechniques.Slide;
        if (note.Vibrato) techniques |= ContourTechniques.Vibrato;
        if (techniques == ContourTechniques.None) return default;

        var window = (note.Position + note.Sustain).GetPositionMs(beats) - timeMs;
        if (bendSemis > 0) window = Math.Max(window, peakAfterMs + TechniqueContourJudge.BendLateSlackMs);
        return new TechniqueContourExpectation(techniques, bendSemis, peakAfterMs, slideSemis ?? 0, Math.Max(0, window));
    }

    /// <summary>A note's pitch can only be followed until the next sound is struck: every
    /// contour window ends at the next later chart time.</summary>
    private static void ClampContourWindows(List<ExpectedSound> sorted)
    {
        var next = double.PositiveInfinity;
        for (var i = sorted.Count - 1; i >= 0; i--)
        {
            var sound = sorted[i];
            if (!sound.TechniqueContour.IsEmpty && next - sound.TimeMs < sound.TechniqueContour.WindowAfterMs)
                sound.TechniqueContour = sound.TechniqueContour with { WindowAfterMs = next - sound.TimeMs };
            if (i == 0 || sorted[i - 1].TimeMs < sound.TimeMs - LiveNoteMatcher.SimultaneousToleranceMs)
                next = sound.TimeMs;
        }
    }

    /// <summary>
    ///     The chord notes to judge a strum by. Normally the chord's own
    ///     <see cref="Chord.ChordNotes"/> — but a chord may legally carry NONE
    ///     and name its shape only through <see cref="Chord.TemplateId"/>, which
    ///     is exactly what RS-XML does (<c>chordNote</c> children are written
    ///     only for strings that need per-note techniques, so an ordinary strum
    ///     has none at all). Such a chord would reach the matcher with an
    ///     EMPTY pitch set, and <c>LiveNoteMatcher.Verifies</c> answers an empty pitch set
    ///     with a flat <c>false</c>: every templated chord in an RS-XML-imported
    ///     arrangement was an automatic Miss no matter how perfectly it was
    ///     played, in both the candidate and the CQT judging modes. The template
    ///     is the chart's own statement of which frets sound, so read it.
    /// </summary>
    private IReadOnlyList<ChordNote> ChordShape(Arrangement arrangement, Chord chord)
    {
        if (chord.ChordNotes.Count > 0 || chord.TemplateId < 0) return chord.ChordNotes;

        ChordTemplate? template = null;
        foreach (var t in arrangement.ChordTemplates)
        {
            if (t.Id == chord.TemplateId)
            {
                template = t;
                break;
            }
        }

        if (template == null || template.Frets.Count == 0) return chord.ChordNotes;

        _templateShape.Clear();
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (stringIndex < 0 || fret < 0) continue;
            _templateShape.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        }

        return _templateShape.Count > 0 ? _templateShape : chord.ChordNotes;
    }

    private static int? ExpectedMidi(Arrangement arrangement, int stringIndex, int fret, int transposeSemitones) =>
        LivePitchAcceptance.SoundingMidi(arrangement, stringIndex, fret) + transposeSemitones;
}
