using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     The key a <see cref="ChordNamingStyle.Musical" /> chord name is spelled in,
///     and the spelling that key gives each black-key pitch class.
///     <para>
///         <b>The authored key is a hint, not the answer.</b> A chart's key comes
///         from <see cref="ChartData.KeyAccidentals" />, which a Guitar Pro import
/// copies off the tab's FIRST bar — and of the owner's 12 tabs, 5
///         leave it at C major while their typed chords are <c>Gb</c>,
///         <c>Ebm7</c> and <c>Abm9</c>, and one says E major (4 sharps) over a
///         song typed <c>Db</c>/<c>Gb</c>/<c>Bbm7</c>. So the key is read from the
///         arrangement's own notes (Krumhansl–Kessler key profiles over the
///         pitch-class counts of the hardest level) and the authored key is kept
///         only while it fits those notes about as well as the best key does.
///         With no notes the authored key stands; with neither, there is no key
///         and the namer keeps its old order.
///     </para>
/// </summary>
public static class ChordKeyEstimator
{
    /// <summary>
    ///     How far below the best key's profile correlation the AUTHORED key may
    ///     fall and still be used. Relative major/minor and neighbouring keys
    ///     sit within ~0.1 of each other on real charts; a wrong signature (E
    ///     major over a Db-major song) falls ~0.5 below.
    /// </summary>
    internal const double AuthoredKeyTolerance = 0.15;

    /// <summary>Notes below which the content is too thin to overrule anything.</summary>
    internal const int MinimumNotes = 8;

    private static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
    private static readonly int[] MajorScale = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>
    ///     The key to spell an arrangement's chord names in, or null when there
    ///     is nothing to go on (no notes and no authored key).
    /// </summary>
    /// <param name="arrangement">Supplies the notes (and tuning + capo to hear them at).</param>
    /// <param name="chart">
    ///     Supplies the authored key. <c>KeyAccidentals == 0</c> with a major
    ///     mode is indistinguishable from "never set", so it is treated as absent.
    /// </param>
    public static ChordKeySignature? Resolve(Arrangement? arrangement, ChartData? chart)
    {
        ChordKeySignature? authored = chart != null && (chart.KeyAccidentals != 0 || chart.KeyIsMinor)
            ? new ChordKeySignature(Math.Clamp(chart.KeyAccidentals, -7, 7), chart.KeyIsMinor)
            : null;

        var histogram = arrangement != null ? PitchClassHistogram(arrangement) : null;
        return Resolve(histogram, authored);
    }

    /// <summary>
    ///     <see cref="Resolve(Arrangement, ChartData)" /> on a pitch-class count
    ///     (index 0 = C) — the pure half, for tests.
    /// </summary>
    public static ChordKeySignature? Resolve(IReadOnlyList<double>? histogram, ChordKeySignature? authored)
    {
        if (histogram == null || histogram.Count != 12 || histogram.Sum() < MinimumNotes) return authored;

        var best = Estimate(histogram, out var bestScore);
        if (authored is { } key && Correlation(histogram, key.Tonic, key.IsMinor) >= bestScore - AuthoredKeyTolerance)
            return key;
        return best;
    }

    /// <summary>
    ///     The best-fitting of the 24 major and minor keys for a pitch-class count
    ///     (Krumhansl–Schmuckler).
    /// </summary>
    public static ChordKeySignature Estimate(IReadOnlyList<double> histogram, out double score)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        score = double.NegativeInfinity;
        var best = new ChordKeySignature(0, false);
        for (var tonic = 0; tonic < 12; tonic++)
        {
            foreach (var minor in (ReadOnlySpan<bool>)[false, true])
            {
                var r = Correlation(histogram, tonic, minor);
                if (r <= score) continue;
                score = r;
                best = ChordKeySignature.FromTonic(tonic, minor);
            }
        }

        return best;
    }

    /// <summary>
    ///     Whether a pitch class is spelled with a FLAT in this key. Only the five
    ///     black keys are ever asked; a white key is always its letter (the namer
    ///     never writes <c>Cb</c>, <c>Fb</c>, <c>E#</c> or <c>B#</c>).
    ///     <para>
    ///         A note in the key's scale follows the signature (flats in a flat
    ///         key, sharps in a sharp key). A note outside it follows its degree:
    ///         in major the ♭2, ♭3, ♭6 and ♭7 are flats and the ♯4 a sharp
    ///         (C major: <c>Db Eb F# Ab Bb</c>); in minor the ♭2 is a flat and the
    ///         raised 3rd, 4th, 6th and 7th are sharps (A minor: <c>Bb</c>,
    ///         <c>C#</c>, <c>D#</c>, <c>F#</c>, <c>G#</c>).
    ///     </para>
    /// </summary>
    public static bool SpellsFlat(ChordKeySignature key, int pitchClass)
    {
        var pc = ChordKeySignature.Mod12(pitchClass);
        var relativeMajor = ChordKeySignature.Mod12(7 * key.Accidentals);
        if (Array.IndexOf(MajorScale, ChordKeySignature.Mod12(pc - relativeMajor)) >= 0)
            return key.Accidentals < 0;

        var degree = ChordKeySignature.Mod12(pc - key.Tonic);
        return key.IsMinor ? degree == 1 : degree != 6;
    }

    /// <summary>
    ///     Onset counts per sounding pitch class over the arrangement's HARDEST
    ///     level (single notes and every chord note), tuning and capo folded in;
    ///     the chord templates stand in when the level holds nothing.
    /// </summary>
    public static double[] PitchClassHistogram(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var histogram = new double[12];
        var open = ChordNameSuggester.OpenStringPitchClasses(arrangement.Tuning);
        var capo = Math.Max(0, arrangement.Capo);

        void Add(int stringIndex, int fret)
        {
            if (fret < 0 || stringIndex < 0 || stringIndex >= open.Count || open[stringIndex] is not { } openPc) return;
            histogram[ChordKeySignature.Mod12(openPc + Math.Max(fret, capo))]++;
        }

        ChartLevel? hardest = null;
        foreach (var level in arrangement.Levels)
        {
            if (hardest == null || level.DifficultyIndex > hardest.DifficultyIndex)
                hardest = level;
        }

        if (hardest != null)
        {
            foreach (var note in hardest.Notes)
            {
                if (note.Mute == MuteType.Full) continue;
                Add(note.StringIndex, note.Fret);
            }

            foreach (var chord in hardest.Chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                {
                    if (chordNote.Mute == MuteType.Full) continue;
                    Add(chordNote.StringIndex, chordNote.Fret);
                }
            }
        }

        if (histogram.Sum() > 0) return histogram;

        foreach (var template in arrangement.ChordTemplates)
        {
            foreach (var (stringIndex, fret) in template.Frets)
                Add(stringIndex, fret);
        }

        return histogram;
    }

    private static double Correlation(IReadOnlyList<double> histogram, int tonic, bool minor)
    {
        var profile = minor ? MinorProfile : MajorProfile;
        double meanX = 0, meanY = 0;
        for (var i = 0; i < 12; i++)
        {
            meanX += histogram[i];
            meanY += profile[i];
        }

        meanX /= 12;
        meanY /= 12;
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++)
        {
            var dx = histogram[i] - meanX;
            var dy = profile[ChordKeySignature.Mod12(i - tonic)] - meanY;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }

        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
}
