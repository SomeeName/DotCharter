namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>
///     Compares by what a string SOUNDS, not by its index, when the tab and the chart do not
///     count the same number of strings (a five-string bass against a four-string chart, a
///     seven-string guitar against six strings): every index is off by the strings the
///     layouts do not share. The tab's sounds are re-spelled onto the chart's strings.
/// </summary>
public static class TabChartStrings
{
    /// <summary>Octaves tried, nearest to none first (<c>MaxBy</c> keeps the first of equals).</summary>
    private static readonly int[] OctaveShifts = [0, -12, 12, -24, 24];

    /// <summary>
    ///     The tab's measures with each sound on the chart string that opens on the same pitch. A sound on a
    ///     string the chart lacks moves to the chart's lowest string that can reach the pitch (a low B
    ///     charted on the E string), and, failing that, to a string index no chart string has, so it
    ///     can never match by position. Equal string counts return the measures unchanged.
    /// </summary>
    public static List<MeasureSounds> OnChartStrings(List<MeasureSounds> tabMeasures, Arrangement tab, Arrangement chart)
    {
        if (tab.StringCount == chart.StringCount) return tabMeasures;

        var chartOpen = Enumerable.Range(0, chart.StringCount).Select(c => SoundingPitch.OpenStringMidi(chart, c)).ToList();
        var tabOpen = Enumerable.Range(0, Math.Max(tab.StringCount, 1)).Select(s => SoundingPitch.OpenStringMidi(tab, s)).ToList();

        // Parts differ in the octave they write (a bass on the bass path against one on a guitar path):
        // the shift that lines up most open strings is the one both are written in.
        var shift = OctaveShifts.MaxBy(k => tabOpen.Count(o => o != null && chartOpen.Contains(o + k)));
        var sameOpen = new Dictionary<int, int>();
        for (var s = 0; s < tabOpen.Count; s++)
        {
            if (tabOpen[s] is { } open && chartOpen.IndexOf(open + shift) is var c and >= 0) sameOpen[s] = c;
        }

        return [.. tabMeasures.Select(m => m with { Sounds = [.. m.Sounds.Select(s => Respell(s, sameOpen, shift, chart))] })];
    }

    private static StruckSound Respell(StruckSound sound, Dictionary<int, int> sameOpen, int shift, Arrangement chart)
    {
        if (sameOpen.TryGetValue(sound.String, out var shared)) return sound with { String = shared, SourceString = sound.String };

        if (sound.Midi is { } pitch)
        {
            for (var c = 0; c < chart.StringCount; c++)
            {
                if (SoundingPitch.FretForPitch(chart, c, pitch + shift) is { } fret)
                    return sound with { String = c, Fret = fret, SourceString = sound.String };
            }
        }

        return sound with { String = -1 - sound.String, SourceString = sound.String };
    }
}
