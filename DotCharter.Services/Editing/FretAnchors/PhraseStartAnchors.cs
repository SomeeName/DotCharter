using DotCharter.Services.Editing.Improvement;

namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
///     The hand position a phrase needs at its start — inspector rule E06 (<c>PhraseAnchorRule</c>):
///     a phrase that plays sounds on a level must own an anchor somewhere from its start to its
///     first sound there. ONE reading of that rule for the code that ADDS such anchors: the bulk
///     auto-fix (<c>ArrangementAutoFixService.AddMissingAnchorsAtPhraseStarts</c>) and the AI chat's
/// sections &amp; phrases job, which places the anchor a proposed phrase needs.
/// </summary>
public static class PhraseStartAnchors
{
    /// <summary>The first sound <paramref name="level" /> plays in
    /// [<paramref name="start" />, <paramref name="end" />) — null <paramref name="end" /> runs to
    /// the end of the level — or null when it plays nothing there (EOF's "populated" test).</summary>
    public static FractionalPosition? FirstSound(ChartLevel level, FractionalPosition start, FractionalPosition? end)
    {
        ArgumentNullException.ThrowIfNull(level);
        FractionalPosition? first = null;
        foreach (var note in level.Notes)
        {
            if (note.Position >= start && (end is not { } e || note.Position < e) && (first == null || note.Position < first.Value))
                first = note.Position;
        }

        foreach (var chord in level.Chords)
        {
            if (chord.Position >= start && (end is not { } e || chord.Position < e) && (first == null || chord.Position < first.Value))
                first = chord.Position;
        }

        return first;
    }

    /// <summary>True when an anchor of <paramref name="level" /> sits anywhere from
    /// <paramref name="start" /> to <paramref name="firstSound" /> — not only on the boundary.</summary>
    public static bool IsCovered(ChartLevel level, FractionalPosition start, FractionalPosition firstSound)
        => level.Anchors.Any(a => a.Position >= start && a.Position <= firstSound);

    /// <summary>The anchor in force just before <paramref name="start" />: the latest one that
    /// starts before it, or null.</summary>
    public static FretAnchor? Previous(ChartLevel level, FractionalPosition start)
    {
        ArgumentNullException.ThrowIfNull(level);
        FretAnchor? previous = null;
        foreach (var a in level.Anchors)
        {
            if (a.Position < start && (previous == null || a.Position > previous.Position))
                previous = a;
        }

        return previous;
    }

    /// <summary>
    ///     The anchor the phrase <paramref name="phrases" />[<paramref name="index" />] needs on
    ///     <paramref name="level" />, or null when it needs none (it plays nothing there, or it already
    ///     owns an anchor). Never an invented fret:
    ///     <list type="bullet">
    ///         <item>the anchor IN FORCE is restated at the phrase start — the same fret and width, so
    ///         the hand and the chart stay exactly as they were (the anchor is split, not moved), floored
    ///         at <c>capo + 1</c> like the auto-fix (inspector L02);</item>
    ///         <item>with none in force (the phrase opens before the level's first anchor), the anchor the
    ///         FHP generator (<paramref name="generator" />) puts in force at the phrase's first sound;</item>
    ///         <item>null when the generator places none there either (open strings only).</item>
    ///     </list>
    /// </summary>
    /// <param name="arrangement">Owns <paramref name="level" />: its capo, templates and tuning.</param>
    /// <param name="level">The level.</param>
    /// <param name="phrases">The arrangement's playable phrases (<see cref="PhraseMarkers.Playable" />).</param>
    /// <param name="index">The phrase in <paramref name="phrases" />.</param>
    /// <param name="generator">The FHP generator, read only when no anchor is in force.</param>
    public static FretAnchor? Needed(Arrangement arrangement, ChartLevel level,
        IReadOnlyList<PhraseMarkers.PhraseMarker> phrases, int index, IFretAnchorGenerationService generator)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(generator);
        var start = phrases[index].Position;
        FractionalPosition? end = index + 1 < phrases.Count ? phrases[index + 1].Position : null;
        if (FirstSound(level, start, end) is not { } first || IsCovered(level, start, first)) return null;

        if (Previous(level, start) is { } previous)
            return new FretAnchor(start, Math.Max(previous.Fret, ChartImprovementService.MinAnchorFret(arrangement.Capo)), previous.Width);

        var generated = generator.GenerateAnchors(level, chordTemplates: arrangement.ChordTemplates, capo: arrangement.Capo,
            bassArrangement: ArrangementInstrument.IsBassPart(arrangement), tuning: arrangement);
        var inForce = generated.LastOrDefault(a => a.Position <= first);
        return inForce is null ? null : new FretAnchor(start, inForce.Fret, inForce.Width);
    }
}
