namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Chart technique a judged sound demanded (0.21 technique report card).
///     Derived from the <see cref="ExpectedSound"/> flags — a sound can
///     demand several at once (each tallies separately), and a pinch
///     harmonic counts as BOTH <see cref="Harmonic"/> and
///     <see cref="PinchHarmonic"/> (the matcher's flags nest the same way).
/// </summary>
public enum PlayedTechnique
{
    PalmMute,
    Harmonic,
    PinchHarmonic,
    Legato,
    SlapOrPop,
    Slide,
    FullMute,

    // 0.48 — the three the Progress hub's report card asked for and nothing
    // tallied. All three are APPENDED, never inserted: the persisted form is
    // the enum's NAME (`TechniquePlayRecord.Technique` is a string), so a
    // renumbering would not corrupt an old store — but the UI's worst-first
    // ordering and every switch over this enum read better with the original
    // seven left where they were.

    /// <summary>The chart bends this sound (any bend point at all). Derived
    /// from <see cref="ExpectedSound.MaxBendSemis" />, which the matcher has
    /// always carried for the pitch window and never counted.</summary>
    Bend,

    /// <summary>Two or more strings sound together —
    /// <see cref="ExpectedSound.IsChord" />. A chord is judged as ONE sound, so
    /// this measures how cleanly strums land, not how many strings rang.</summary>
    Chord,

    /// <summary>Tremolo picking (<see cref="ExpectedSound.Tremolo" />).</summary>
    Tremolo,

    /// <summary>Vibrato (<see cref="ExpectedSound.Vibrato" />) — appended last (the stored order), when
    /// the contour judge made it something a run can get wrong.</summary>
    Vibrato
}

/// <summary>One technique's judged tallies for a single session.
/// <see cref="WrongTechnique"/> is the report card's core signal: the note
/// was played but the technique wasn't
/// (<see cref="LivePlayVerdict.HitWrongTechnique"/>).</summary>
public readonly record struct TechniqueResult(
    PlayedTechnique Technique, int Hits, int WrongTechnique, int Misses)
{
    public int Judged => Hits + WrongTechnique + Misses;
}

/// <summary>
///     Pure per-technique aggregation over a run's judged sounds. One scan
///     at session stop (never on the tick path); unjudged sounds are skipped
///     and only techniques with any judged sound are returned (in enum
///     order), so the persisted form stays small.
/// </summary>
public static class LiveTechniqueTally
{
    private static readonly int TechniqueCount = Enum.GetValues<PlayedTechnique>().Length;

    public static IReadOnlyList<TechniqueResult> Compute(IReadOnlyList<ExpectedSound> sounds)
    {
        // [technique, 0=hit 1=wrong 2=miss]
        var counts = new int[TechniqueCount, 3];
        for (var i = 0; i < sounds.Count; i++)
        {
            var sound = sounds[i];
            if (sound.Verdict is not { } verdict) continue;
            var slot = verdict switch
            {
                LivePlayVerdict.Hit => 0,
                LivePlayVerdict.HitWrongTechnique => 1,
                _ => 2
            };

            if (sound.PalmMute) counts[(int)PlayedTechnique.PalmMute, slot]++;
            if (sound.Harmonic) counts[(int)PlayedTechnique.Harmonic, slot]++;
            if (sound.PinchHarmonic) counts[(int)PlayedTechnique.PinchHarmonic, slot]++;
            if (sound.Legato) counts[(int)PlayedTechnique.Legato, slot]++;
            if (sound.SlapOrPop) counts[(int)PlayedTechnique.SlapOrPop, slot]++;
            // SlideSemis is the CHORD form of the same technique (a strum slides
            // as one shape) — without it the report card graded every chord slide
            // in the chart as no slide at all.
            if (sound.SlideToMidi is not null || sound.UnpitchedSlide || sound.SlideSemis != 0)
                counts[(int)PlayedTechnique.Slide, slot]++;
            if (sound.FullMute) counts[(int)PlayedTechnique.FullMute, slot]++;
            if (sound.MaxBendSemis > 0) counts[(int)PlayedTechnique.Bend, slot]++;
            if (sound.IsChord) counts[(int)PlayedTechnique.Chord, slot]++;
            if (sound.Tremolo) counts[(int)PlayedTechnique.Tremolo, slot]++;
            if (sound.Vibrato) counts[(int)PlayedTechnique.Vibrato, slot]++;
        }

        List<TechniqueResult>? results = null;
        for (var t = 0; t < TechniqueCount; t++)
        {
            var hits = counts[t, 0];
            var wrong = counts[t, 1];
            var misses = counts[t, 2];
            if (hits + wrong + misses == 0) continue;
            (results ??= []).Add(
                new TechniqueResult((PlayedTechnique)t, hits, wrong, misses));
        }

        return results ?? (IReadOnlyList<TechniqueResult>)[];
    }
}
