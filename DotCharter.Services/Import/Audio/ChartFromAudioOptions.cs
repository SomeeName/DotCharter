using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Import.Audio;

/// <summary>
///     What the chart-from-audio wizard has to know about the arrangement it is
///     drafting FOR, kept as a pure function of that arrangement so it can be
///     tested without the dialog's whole graph.
///     <para>
///     The wizard must not pick its string map from the arrangement TYPE alone —
///     standard six-string guitar, or standard four-string bass — and then stamp
///     the template's real <c>Tuning</c>, <c>StringCount</c> and <c>Capo</c> onto
///     the draft it produces. Three ways that is wrong, all silent:
///     </para>
///     <list type="bullet">
///     <item>a detected pitch on a Drop-C chart was spelled at its STANDARD-tuning
///     fret, so in the arrangement it landed in it sounded a different note —
///     every string off by its own tuning offset;</item>
///     <item>under a capo it emitted frets BELOW the capo, which is the malformed
///     shape inspector rule L01 / <c>ChartExportValidator.KeyNoteBelowCapo</c>
///     exists to report: the app minting exactly what its own linter flags;</item>
///     <item>on a 7-string (or 5-string bass) the map had 6 (4) entries while
///     <c>StringCount</c> was copied from the template, so the extra string was
///     unreachable and every pitch that only fits there was dropped without a
///     word.</item>
///     </list>
/// </summary>
public static class ChartFromAudioOptions
{
    /// <summary>
    ///     Open-string MIDI pitches for <paramref name="arrangement" />, lowest
    ///     string first, one entry per string it really has.
    ///     <para>
    ///     Derived through <see cref="LivePitchAcceptance.SoundingMidi" /> — the app's
    ///     ONE sounding-pitch derivation, which folds the tuning offsets and the
    ///     capo — and then the capo is taken back OFF, because the analyzer
    ///     measures a fret from the un-capoed nut: a fret is ABSOLUTE and a
    ///     capo-stopped string sounds at <c>max(fret, capo)</c>. Pair this with
    ///     <see cref="AudioToChartOptions.MinFret" /> = the capo, which is what
    ///     keeps the sub-capo frets out.
    ///     </para>
    ///     With no arrangement at all there is nothing to derive from, so the
    ///     standard map stands in — the same answer the wizard has always given.
    /// </summary>
    public static IReadOnlyList<int> OpenStringMidiFor(Arrangement? arrangement)
    {
        if (arrangement == null) return AudioToChartOptions.StandardGuitarMidi;

        var isBass = arrangement.ArrangementType == ArrangementType.Bass;
        var count = arrangement.StringCount > 0
            ? arrangement.StringCount
            : (isBass ? AudioToChartOptions.StandardBassMidi.Count : AudioToChartOptions.StandardGuitarMidi.Count);

        var fallback = isBass ? AudioToChartOptions.StandardBassMidi : AudioToChartOptions.StandardGuitarMidi;
        var open = new int[count];
        for (var s = 0; s < count; s++)
        {
            open[s] = LivePitchAcceptance.SoundingMidi(arrangement, s, 0) is { } sounding
                ? sounding - arrangement.Capo
                : fallback[Math.Min(s, fallback.Count - 1)];
        }

        return open;
    }

    /// <summary>Frets below the capo cannot sound, so the search floors there.
    /// A capo of 0 is a strict no-op.</summary>
    public static int MinFretFor(Arrangement? arrangement) => Math.Max(0, arrangement?.Capo ?? 0);
}
