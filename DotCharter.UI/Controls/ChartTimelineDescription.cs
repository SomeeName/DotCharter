using System.Globalization;

namespace DotCharter.UI.Controls;

/// <summary>
/// The words a screen reader hears for the chart timeline: the
///     surface's NAME (arrangement + playhead) and its VALUE (the selection,
///     e.g. "Chord Am, bar 12 beat 3, 4 notes"). Pure — selection, beats and a
///     localizer in, a string out — so <see cref="ChartRendererAutomationPeer" />
///     stays a thin reader and every wording rule is testable without a window.
///     <para>
///         Words reuse the keys the rest of the app already shows for the same
///         thing (<c>ChartDiff_Chord</c>, <c>ChartDiff_NoteOnString</c>,
///         <c>ChartDiff_Fret</c>, the technique names of the query and pattern
///         panels, <c>StatusBar_Selection</c>, <c>Inspector_Selection_None</c>);
///         only the glue is new (<c>Timeline_A11y_*</c>).
///     </para>
/// </summary>
public static class ChartTimelineDescription
{
    /// <summary>"Chart timeline, Lead, bar 12 beat 3" — or just "Chart timeline"
    /// when nothing is loaded.</summary>
    public static string DescribeSurface(Arrangement? arrangement, IList<BeatMarker> beats, double playheadMs,
        ILocalizationService loc)
    {
        if (arrangement is null) return loc["Timeline_A11y_Name"];
        return loc.Format("Timeline_A11y_NameFmt", arrangement.Name, DescribeTime(beats, playheadMs, loc));
    }

    /// <summary>The selection as one sentence; "No selection" when empty.</summary>
    public static string DescribeSelection(ISelectionService selection, Arrangement? arrangement,
        IList<BeatMarker> beats, ILocalizationService loc)
    {
        var total = selection.SelectedNotes.Count + selection.SelectedChords.Count +
                    selection.SelectedHandShapes.Count + selection.SelectedAnchors.Count +
                    selection.SelectedEventPoints.Count + selection.SelectedTones.Count +
                    (selection.SelectedLyric is null ? 0 : 1);
        if (total == 0) return loc["Inspector_Selection_None"];
        if (total > 1) return DescribeMany(selection, total, beats, loc);

        if (selection.SelectedChords.Count == 1) return DescribeChord(selection.SelectedChords[0], arrangement, beats, loc);
        if (selection.SelectedNotes.Count == 1) return DescribeNote(selection.SelectedNotes[0], beats, loc);
        if (selection.SelectedHandShapes.Count == 1)
            return Join(loc, loc["ChartDiff_HandShape"], DescribePosition(selection.SelectedHandShapes[0].Position, beats, loc));
        if (selection.SelectedAnchors.Count == 1)
        {
            var anchor = selection.SelectedAnchors[0];
            return Join(loc, loc["ChartDiff_Anchor"], DescribePosition(anchor.Position, beats, loc),
                loc.Format("ChartDiff_Fret", anchor.Fret));
        }

        if (selection.SelectedEventPoints.Count == 1)
            return Join(loc, loc["ChartDiff_EventPoint"], DescribePosition(selection.SelectedEventPoints[0].Position, beats, loc));
        if (selection.SelectedTones.Count == 1)
            return Join(loc, loc["ChartDiff_ToneChange"], DescribePosition(selection.SelectedTones[0].Position, beats, loc));
        return loc.Format("StatusBar_Selection", total);
    }

    /// <summary>"bar 12 beat 3" (a fractional beat reads "beat 3.5", its decimal
    /// mark from the Windows REGION, like every number the app prints) — the
    /// numbers a charter counts by. Bars restart at every <c>IsMeasure</c> beat;
    /// a grid that does not open on one counts its first beat as bar 1.</summary>
    public static string DescribePosition(FractionalPosition position, IList<BeatMarker> beats, ILocalizationService loc)
    {
        if (beats.Count == 0 || position.BeatId < 0) return DescribeClock(position.GetPositionMs(beats), loc);
        var (bar, beat) = BarAndBeat(beats, Math.Min(position.BeatId, beats.Count - 1));
        var beatValue = beat + position.Fraction.DoubleValue;
        return loc.Format("Timeline_A11y_BarBeatFmt", bar, beatValue.ToString("0.##", CultureInfo.CurrentCulture));
    }

    /// <summary>The playhead in bars and beats; before the first beat, or on a
    /// chart without a grid, as a clock time.</summary>
    public static string DescribeTime(IList<BeatMarker> beats, double timeMs, ILocalizationService loc)
    {
        var index = -1;
        for (var i = 0; i < beats.Count && beats[i].Time <= timeMs; i++) index = i;
        if (index < 0) return DescribeClock(timeMs, loc);
        var (bar, beat) = BarAndBeat(beats, index);
        return loc.Format("Timeline_A11y_BarBeatFmt", bar, beat.ToString(CultureInfo.CurrentCulture));
    }

    /// <summary>The keys that move the selection, read from the user's OWN
    /// bindings (every one is rebindable), for the peer's help text.</summary>
    public static string DescribeKeys(KeybindConfig keys, ILocalizationService loc) =>
        loc.Format("Timeline_A11y_HelpFmt",
            keys.SeekNext.ToDisplayString(loc), keys.SeekPrev.ToDisplayString(loc),
            keys.SeekNextExtend.ToDisplayString(loc), keys.SeekPrevExtend.ToDisplayString(loc),
            keys.SeekNextBeat.ToDisplayString(loc), keys.SeekPrevBeat.ToDisplayString(loc));

    private static string DescribeChord(Chord chord, Arrangement? arrangement, IList<BeatMarker> beats,
        ILocalizationService loc)
    {
        var name = FindTemplate(arrangement, chord.TemplateId)?.GetDisplayName();
        var kind = string.IsNullOrWhiteSpace(name)
            ? loc["ChartDiff_Chord"]
            : loc.Format("Timeline_A11y_KindNameFmt", loc["ChartDiff_Chord"], name);
        var parts = new List<string>
        {
            kind,
            DescribePosition(chord.Position, beats, loc),
            loc.Plural("ChartDiff_ChordNotesOne", "ChartDiff_ChordNotes", chord.ChordNotes.Count),
        };
        if (chord.IsFullyMuted() || chord.IsPalmMuted()) parts.Add(loc["Pattern_Technique_Mute"]);
        if (chord.Accent) parts.Add(loc["Pattern_Technique_Accent"]);
        return Join(loc, [.. parts]);
    }

    private static string DescribeNote(GuitarNote note, IList<BeatMarker> beats, ILocalizationService loc)
    {
        var parts = new List<string>
        {
            loc["Inspector_Tab_Note"],
            DescribePosition(note.Position, beats, loc),
            loc.Format("ChartDiff_NoteOnString", note.StringIndex + 1),
            loc.Format("ChartDiff_Fret", note.Fret),
        };
        if (note.BendValues.Count > 0) parts.Add(loc["Query_Technique_Bend"]);
        if (note.SlideTo is not null || note.UnpitchedSlide) parts.Add(loc["Query_Technique_Slide"]);
        if (note.Hopo is HopoType.HammerOn or HopoType.PullOff) parts.Add(loc["Pattern_Technique_Hopo"]);
        if (note.Harmonic != HarmonicType.None) parts.Add(loc["Query_Technique_Harmonic"]);
        if (note.Vibrato) parts.Add(loc["Query_Technique_Vibrato"]);
        if (note.Tremolo) parts.Add(loc["Pattern_Technique_Tremolo"]);
        if (note.Mute != MuteType.None) parts.Add(loc["Pattern_Technique_Mute"]);
        if (note.Accent) parts.Add(loc["Pattern_Technique_Accent"]);
        return Join(loc, [.. parts]);
    }

    private static string DescribeMany(ISelectionService selection, int total, IList<BeatMarker> beats,
        ILocalizationService loc)
    {
        var count = loc.Format("StatusBar_Selection", total);
        FractionalPosition? first = null;
        foreach (var note in selection.SelectedNotes)
        {
            if (first is null || note.Position < first.Value) first = note.Position;
        }

        foreach (var chord in selection.SelectedChords)
        {
            if (first is null || chord.Position < first.Value) first = chord.Position;
        }

        return first is { } at
            ? Join(loc, count, loc.Format("Timeline_A11y_FirstAtFmt", DescribePosition(at, beats, loc)))
            : count;
    }

    private static (int Bar, int Beat) BarAndBeat(IList<BeatMarker> beats, int index)
    {
        int bar = 0, beat = 0;
        for (var i = 0; i <= index; i++)
        {
            if (bar == 0 || beats[i].IsMeasure)
            {
                bar++;
                beat = 1;
            }
            else
            {
                beat++;
            }
        }

        return (bar, beat);
    }

    private static string DescribeClock(double timeMs, ILocalizationService loc)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, timeMs));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.CurrentCulture);
    }

    private static ChordTemplate? FindTemplate(Arrangement? arrangement, int templateId)
    {
        if (arrangement is null || templateId < 0) return null;
        var templates = arrangement.ChordTemplates;
        for (var i = 0; i < templates.Count; i++)
        {
            if (templates[i].Id == templateId)
                return templates[i];
        }

        return null;
    }

    private static string Join(ILocalizationService loc, params string[] parts) =>
        string.Join(loc["Timeline_A11y_Separator"], parts);
}
