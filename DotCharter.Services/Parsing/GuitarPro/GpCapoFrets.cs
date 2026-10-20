namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     The one place a Guitar Pro fret number becomes a DotCharter fret number.
///     <para>
///     <b>Guitar Pro writes tab numbers RELATIVE to the track's capo</b> — a "0"
///     on a capo-2 track is the capo'd open string and a "4" is the fourth fret
///     ABOVE the capo (absolute fret 6). DotCharter's model is ABSOLUTE on the
///     un-capoed neck with fret <c>0</c> meaning "open, which the capo stops at
///     the capo fret", so a sound's sounding offset is <c>max(fret, capo)</c>
///     (<c>RefretPlanner.SoundingMidi</c>, <c>LivePitchAcceptance.SoundingMidi</c>,
///     <c>CapoAnchorRangeRule</c>). Storing GP's number verbatim therefore left
///     every fretted note on a capo'd track <c>capo</c> semitones flat, poisoned
///     its chord templates/names/fingering and dragged its fret-hand anchors
///     down the neck.
///     </para>
///     <para>
///     The conversion is EOF's ("Capo added to fretted strings only",
///     <c>rs.c</c>, which <c>EofProGuitarTrackParser</c> already mirrors for the
///     <c>.eof</c> format): add the capo to a fretted number, leave <c>0</c>
///     alone. Fret <c>0</c> needs no shift because <c>max(0, capo) == capo</c>
///     already sounds it at the capo — and lifting it to <c>capo</c> would turn
///     an open string into a stopped one, losing the distinction the renderers
///     and the export validator both rely on.
///     </para>
///     <b>Capo <c>0</c> — nearly every chart — is a strict no-op.</b>
/// </summary>
internal static class GpCapoFrets
{
    /// <summary>
    ///     Highest fret an absolute fret number may reach. The chart model's
    ///     fret ceiling: <c>RefretOptions.MaxFret</c>, the GP importers' own
    ///     slide/trill clamps and <c>GpPostProcessor.MaterializeSlideIns</c> all
    ///     use 24. (The 22 in <c>FretAnchorGenerationService</c> is a ceiling on
    ///     ANCHOR positions, not on notes, and must not be used here.)
    /// </summary>
    public const int MaxAbsoluteFret = 24;

    /// <summary>
    ///     Folds a capo number taken VERBATIM from a source file into the range
    ///     the rest of the app can represent (<c>0..<see cref="MaxAbsoluteFret"/></c>).
    ///     <para>
    ///     None of the three tab importers owns its capo value — the GP3-5 binary
    ///     track block reads it as a raw <c>ReadInt32</c>, gpif as
    ///     <c>&lt;Property name="CapoFret"&gt;</c> and Songsterr as a JSON
    ///     <c>capo</c> field — and all three must not admit it with nothing but a
    ///     <c>Math.Max(0, …)</c> floor. A capo near <see cref="int.MaxValue"/>
    ///     therefore reaches <see cref="ToAbsolute"/>, where <c>gpFret + capo</c>
    ///     OVERFLOWS to a large negative and sails straight through the
    ///     <c>Math.Min(24, …)</c> ceiling — so the "clamped" helper would hand the
    ///     chart model a fret of about −2.1 billion, which no renderer, exporter
    ///     or validator has a case for. A capo the neck does not have is
    ///     meaningless anyway: fold it to the ceiling and the chart stays
    ///     representable.
    ///     </para>
    ///     <b>Every real capo (0–24, and 0 for the overwhelming majority of
    ///     charts) is a strict no-op.</b>
    /// </summary>
    public static int Sanitize(int capo) => Math.Clamp(capo, 0, MaxAbsoluteFret);

    /// <summary>
    ///     The lowest ABSOLUTE fret a SYNTHESIZED fretted note may take on a
    ///     capo'd track: <c>max(1, capo)</c>.
    ///     <para>
    ///     Every importer that invents a fret — the unpitched slide-out landing
    ///     (<c>GpNoteParser.ApplySlideFlags</c> and its Songsterr twin, both
    ///     <c>fret − 5</c>) and the slide-in origin
    ///     (<c>GpPostProcessor.MaterializeSlideIns</c>, <c>fret ± distance</c>) —
    ///     must not floor at a hard <c>1</c>, which is only the capo-0 form of the
    ///     rule. Frets in the model are ABSOLUTE, so on a capo-5 track a GP fret
    ///     1 arrives as 6 and the −5 would land on 1: five frets BELOW the capo,
    ///     where the string is already stopped. That is not cosmetic —
    ///     <c>ChartExportValidator</c> raises <c>KeyNoteBelowCapo</c> at Error
    ///     severity for exactly this and BLOCKS the export dialog, and
    ///     <c>GpCapoFrets.ToRelative</c> folds such a fret back to <c>0</c> on
    ///     re-export, keeping the pitch and destroying the gesture.
    ///     </para>
    ///     <para>
    ///     The floor is the CAPO, not <c>capo + 1</c>, so that it agrees exactly
    ///     with the two rules the rest of the app already enforces:
    ///     <c>ChartExportValidator</c>'s <c>fret != 0 &amp;&amp; fret &lt; capo</c>
    ///     and <c>ArrangementAutoFixService</c>'s repair of a sub-capo
    ///     <c>SlideTo</c> to <c>capo</c>. Capo 0 is a strict no-op.
    ///     </para>
    /// </summary>
    public static int MinFrettedFret(int capo) => Math.Max(1, Sanitize(capo));

    /// <summary>
    ///     Converts a capo-RELATIVE Guitar Pro fret into an ABSOLUTE chart fret.
    ///     <para>
    ///     Non-positive input reads as <c>0</c> (open) and is returned unshifted:
    ///     that covers both a genuine open string and Guitar Pro's negative
    ///     "dead note" fret, which the binary reader has always clamped to 0.
    ///     An overflowing sum is clamped to <see cref="MaxAbsoluteFret"/> rather
    ///     than allowed through — a fret the neck does not have is rejected by
    ///     the export validator and drawn nowhere, so a clamped (marginally flat)
    ///     note is strictly better than an unrepresentable one. It takes a
    ///     relative fret above <c>24 - capo</c> to reach that at all, which no
    ///     real tab has.
    ///     </para>
    /// </summary>
    /// <param name="gpFret">The fret exactly as Guitar Pro wrote it — relative to
    /// <paramref name="capo"/>.</param>
    /// <param name="capo">The owning TRACK's capo fret (each GP track carries its
    /// own). Non-positive means no capo and makes this the identity on any
    /// non-negative input.</param>
    /// <returns>The absolute fret to store on the chart sound.</returns>
    public static int ToAbsolute(int gpFret, int capo)
    {
        if (gpFret <= 0) return 0;
        // The ceiling applies on BOTH branches. It must not guard only the
        // capo'd sum, so an uncapoed `<Fret>9999</Fret>` reached the model, the
        // chord templates, the fingering suggester and the renderers untouched.
        // Still a strict no-op for every real fret.
        //
        // The sum is taken in `long` because both operands come from a file:
        // `int.MaxValue` frets plus any capo (or any fret plus an unsanitized
        // `int.MaxValue` capo) wrapped to a large NEGATIVE, which `Math.Min`
        // then happily returned as the "clamped" fret. Callers should still
        // pass a capo through `Sanitize`; this makes the helper's own promise
        // true regardless.
        return (int)Math.Min(MaxAbsoluteFret, capo <= 0 ? gpFret : (long)gpFret + capo);
    }

    /// <summary>
    ///     Converts an ABSOLUTE chart fret back into the capo-RELATIVE number
    ///     Guitar Pro expects — the inverse of <see cref="ToAbsolute"/>, and the
    ///     one place <c>GpExporter</c> may turn a fret into a written tab number.
    ///     <para>
    ///     Both halves must exist or neither: <c>GpExporter.BuildScore</c> writes
    ///     <c>Staff.Capo</c>, so Guitar Pro reads every fret it emits as relative.
    ///     Writing our absolute numbers there put every fretted note on a capo'd
    ///     track <c>capo</c> semitones sharp.
    ///     </para>
    ///     <para>
    ///     <b>Fret <c>0</c> stays <c>0</c></b> — it means the same thing in both
    ///     models (the open string, which the capo stops at the capo fret), and
    ///     it is the one number the two models share.
    ///     </para>
    ///     <para>
    ///     <b>A "sub-capo" fret — <c>1..capo</c> — is written as <c>0</c>.</b>
    ///     Such a fret is malformed (the export validator flags it as
    ///     <c>ChartExportValidator.KeyNoteBelowCapo</c>, inspector rule
    ///     <c>L01</c>) because the capo already stops the string higher up: its
    ///     sounding offset is <c>max(fret, capo) == capo</c>, i.e. exactly what
    ///     an open string sounds. Folding it to <c>0</c> therefore preserves the
    ///     PITCH the chart actually makes, and is the only choice that stays a
    ///     legal tab number — the naive <c>fret - capo</c> would emit a negative
    ///     fret, which Guitar Pro reads as a dead note (or rejects outright).
    ///     The model-side rules agree: a sound at or below the capo is treated
    ///     as OPEN everywhere else in the app (it never constrains the fret hand
    ///     and never anchors an FHP). Fret <c>== capo</c> folds to <c>0</c> for
    ///     the same reason, and is not even malformed — the validator's test is
    ///     strict (<c>fret &lt; capo</c>), and <c>ArrangementAutoFixService.
    ///     FixCapoFrets</c> RAISES a sub-capo fret to exactly <c>capo</c>, so
    ///     that is the shape a fixed chart actually arrives in.
    ///     </para>
    ///     <para>
    ///     This deliberately diverges from the <c>.eof</c> writer, whose
    ///     <c>EofNoteBuilder.EncodeFret</c> clamps the same case UP to relative
    ///     fret <c>1</c> (and reports it as an approximation). That is a
    ///     format constraint — EOF cannot spell the case — and it sharpens the
    ///     note by a semitone. Guitar Pro has no such constraint, so preserving
    ///     the sounding pitch is strictly better here.
    ///     </para>
    ///     <b>Capo <c>0</c> is a strict no-op</b>, identical to the
    ///     <c>Math.Max(0, fret)</c> this replaced — so the overwhelming majority
    ///     of charts export byte-for-byte as before.
    /// </summary>
    /// <param name="chartFret">The absolute fret stored on the chart sound.</param>
    /// <param name="capo">The arrangement's capo fret. Non-positive means no capo
    /// and makes this the identity on any non-negative input.</param>
    /// <returns>The capo-relative fret to write into the Guitar Pro document,
    /// never negative.</returns>
    public static int ToRelative(int chartFret, int capo)
    {
        if (chartFret <= 0) return 0;
        if (capo <= 0) return chartFret;
        return chartFret <= capo ? 0 : chartFret - capo;
    }
}
