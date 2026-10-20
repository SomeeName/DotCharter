namespace DotCharter.Services.Export;

/// <summary>
///     The closing <c>noguitar</c> section Editor on Fire adds to every Rocksmith
///     export that has none (<c>eof_rs_export_common</c>, EOF_SRC/rs.c: "Adding
///     missing noguitar RS section"), so the last authored section ends where the
///     playing does instead of running on to the end of the audio. The RS-XML
///     writer and the SNG mapper both ask here, so the two files agree.
///     <list type="bullet">
///         <item>Only when the arrangement has NO section spelled exactly
///         <c>noguitar</c> anywhere — EOF's test, case-sensitive like its
///         <c>ustrcmp</c>.</item>
///         <item>On the first beat after the last note or chord ends — EOF's
///         <c>endbeat</c>, which is also where a missing END phrase is added — even
///         when the charter put END somewhere else, as EOF does.</item>
///         <item>Never over a section the charter already put on that spot: EOF
///         exports one section per beat and warns; the authored one is kept.</item>
///         <item>Never for an arrangement with no notes or chords — EOF does not
///         export an empty track.</item>
///         <item>Never for an arrangement with no sections at all. EOF would ship
///         that as a lone noguitar with every note before it, outside any section;
///         DotCharter gives such a chart a whole structure instead
///         (<see cref="PhraseSectionGenerator"/>, which ends in its own noguitar)
///         or the SNG mapper's one-riff stub, and a lone closing section would
///         make the RS-XML and the SNG built from it disagree.</item>
///     </list>
///     Pure: reads the arrangement, never mutates it.
/// </summary>
internal static class EndNoGuitarSection
{
    public const string Name = "noguitar";

    /// <summary>
    ///     Where the closing section starts, in ms, or null when none is added.
    /// </summary>
    public static double? StartMs(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var sections = arrangement.EventPoints.Where(ep => !string.IsNullOrEmpty(ep.Section)).ToList();
        if (sections.Count == 0 || sections.Any(ep => string.Equals(ep.Section, Name, StringComparison.Ordinal)))
            return null;
        if (BeatAfterContent(arrangement, beats) is not { } afterContent) return null;

        var startMs = afterContent.GetPositionMs(beats);
        var occupied = sections.Any(ep =>
            TimelineMath.ToWholeMs(ep.Position.GetPositionMs(beats)) == TimelineMath.ToWholeMs(startMs));
        return occupied ? null : startMs;
    }

    /// <summary>
    ///     The first beat after the last note or chord ends (sustain included),
    ///     or the last beat when nothing ends before it; null when the arrangement
    ///     has no notes or chords, or there is no beat map.
    /// </summary>
    public static FractionalPosition? BeatAfterContent(Arrangement arrangement, IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return null;

        var lastContentTimeMs = beats[0].Time;
        var hasContent = false;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                lastContentTimeMs = Math.Max(lastContentTimeMs, note.EndPosition.GetPositionMs(beats));
                hasContent = true;
            }

            foreach (var chord in level.Chords)
            {
                lastContentTimeMs = Math.Max(lastContentTimeMs, chord.EndPosition.GetPositionMs(beats));
                hasContent = true;
            }
        }

        if (!hasContent) return null;

        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].Time > lastContentTimeMs)
                return new FractionalPosition(i);
        }

        return new FractionalPosition(beats.Count - 1);
    }
}
