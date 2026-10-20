using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Catalog;

/// <summary>How badly a phrase and its destination disagree.</summary>
public enum PhrasePasteSeverity
{
    /// <summary>Same strings, same tuning, same capo — paste is a plain copy.</summary>
    Ok = 0,

    /// <summary>The paste is legal but the phrase will not SOUND the way it was
    /// captured. Allowed, and reported.</summary>
    Warning = 1,

    /// <summary>The paste would write a chart the export gate rejects. Refused.</summary>
    Blocked = 2
}

/// <summary>
///     One reason a phrase does not fit its destination. Carries a localization
///     KEY plus arguments rather than a message, so the check stays pure and the
///     UI owns the wording (EN + de-DE).
/// </summary>
public sealed class PhrasePasteIssue
{
    public PhrasePasteIssue(PhrasePasteSeverity severity, string localizationKey, params object[] args)
    {
        Severity = severity;
        LocalizationKey = localizationKey;
        Args = args;
    }

    public PhrasePasteSeverity Severity { get; }
    public string LocalizationKey { get; }
    public IReadOnlyList<object> Args { get; }
}

/// <summary>The verdict for one (phrase, destination arrangement) pair.</summary>
public sealed class PhrasePasteCheck
{
    public PhrasePasteCheck(PhrasePasteSeverity severity, IReadOnlyList<PhrasePasteIssue> issues)
    {
        Severity = severity;
        Issues = issues;
    }

    public PhrasePasteSeverity Severity { get; }
    public IReadOnlyList<PhrasePasteIssue> Issues { get; }
    public bool IsBlocked => Severity == PhrasePasteSeverity.Blocked;
}

/// <summary>
///     Decides what happens when a catalog phrase meets an arrangement with a
///     different tuning, capo or string count. Pure — no services, no logging.
///     <para>
///         <b>The rule.</b> Frets are ABSOLUTE on the un-capoed neck and fret
///         <c>0</c> is whatever the capo stops (see <c>DotCharter.Core/AGENTS.md</c>),
///         so a phrase pastes VERBATIM — no fret is ever rewritten. What differs
///         is whether the result is still playable and still the same music:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Blocked</b> — the paste would produce chart data the export gate
///             rejects outright: a string the destination does not have
///             (<c>KeyStringOutOfRange</c>), a fret outside 0..24
///             (<c>KeyFretOutOfRange</c>), or a non-zero fret under the
///             destination's capo (<c>KeyNoteBelowCapo</c> / inspector <c>L01</c>).
///             Transposing out of these is a REFRET, not a paste — the user has
///             <c>RefretService</c> for that, and guessing here would silently
///             change the music.
///         </item>
///         <item>
///             <b>Warning</b> — the paste is legal but does not sound like the
///             captured phrase: the tuning differs on a string the phrase uses, or
///             the capo differs and the phrase plays an open string. The paste
///             proceeds and the reason is surfaced.
///         </item>
///     </list>
///     <para>
///         The one thing this must never do is stay quiet: silently pasting an
///         unplayable or wrongly-pitched phrase is the failure mode the whole
///         check exists to prevent.
///     </para>
/// </summary>
public static class PhrasePasteCompatibility
{
    /// <summary>Model fret ceiling — the same bound
    /// <c>ChartExportValidator.KeyFretOutOfRange</c> enforces.</summary>
    public const int MaxFret = 24;

    public const string KeyStringOutOfRange = "PhraseCatalog_Issue_StringOutOfRange";
    public const string KeyFretOutOfRange = "PhraseCatalog_Issue_FretOutOfRange";
    public const string KeyFretBelowCapo = "PhraseCatalog_Issue_FretBelowCapo";
    public const string KeyTuningDiffers = "PhraseCatalog_Issue_TuningDiffers";
    public const string KeyCapoDiffers = "PhraseCatalog_Issue_CapoDiffers";
    public const string KeyPlayOnly = "PhraseCatalog_Issue_PlayOnly";

    private static readonly PhrasePasteCheck Fine =
        new(PhrasePasteSeverity.Ok, Array.Empty<PhrasePasteIssue>());

    /// <summary>
    ///     Evaluates <paramref name="entry" /> against <paramref name="target" />.
    ///     A null target is <see cref="PhrasePasteSeverity.Ok" /> — "no destination"
    ///     is the caller's problem, not a compatibility question.
    /// </summary>
    public static PhrasePasteCheck Evaluate(PhraseCatalogEntry entry, Arrangement? target)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Evaluate(entry.Payload, entry.Source, target);
    }

    /// <summary>
    ///     The same check for any payload: the clipboard's Paste and Paste Special
    /// run it too, so all three ways into one commit answer alike. A
    ///     null <paramref name="source" /> (a payload copied before the source was
    ///     recorded) still gets the hard checks; only the tuning and capo
    ///     comparisons need to know where it came from.
    /// </summary>
    public static PhrasePasteCheck Evaluate(ClipboardPayload payload, PhraseSourceContext? source, Arrangement? target)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (target == null) return Fine;

        var issues = new List<PhrasePasteIssue>();

        // Play-only content is never exported: pasting it into an unrestricted
        // arrangement would launder it past every exporter's guard.
        if (source != null && source.Provenance != ChartProvenance.Unrestricted &&
            target.Provenance == ChartProvenance.Unrestricted)
        {
            issues.Add(new PhrasePasteIssue(PhrasePasteSeverity.Blocked, KeyPlayOnly));
        }

        var targetStrings = target.StringCount;
        var targetCapo = target.Capo;
        var targetOffsets = target.TuningPitches.Select(t => t.Step).ToArray();

        var usedStrings = new SortedSet<int>();
        var worstString = -1;
        var worstFret = int.MinValue;
        var subCapoFret = int.MinValue;
        var playsOpenString = false;

        foreach (var (stringIndex, fret) in EnumerateSounds(payload))
        {
            usedStrings.Add(stringIndex);

            if (stringIndex < 0 || stringIndex >= targetStrings)
            {
                if (worstString < 0 || stringIndex > worstString)
                    worstString = stringIndex;
            }

            if (fret is < 0 or > MaxFret)
            {
                if (worstFret == int.MinValue || fret > worstFret)
                    worstFret = fret;
            }

            if (fret == 0)
            {
                playsOpenString = true;
            }
            else if (targetCapo > 0 && fret < targetCapo)
            {
                if (subCapoFret == int.MinValue || fret < subCapoFret)
                    subCapoFret = fret;
            }
        }

        if (worstString >= 0)
        {
            issues.Add(new PhrasePasteIssue(PhrasePasteSeverity.Blocked, KeyStringOutOfRange,
                worstString + 1, targetStrings));
        }

        if (worstFret != int.MinValue)
            issues.Add(new PhrasePasteIssue(PhrasePasteSeverity.Blocked, KeyFretOutOfRange, worstFret, MaxFret));
        if (subCapoFret != int.MinValue)
            issues.Add(new PhrasePasteIssue(PhrasePasteSeverity.Blocked, KeyFretBelowCapo, subCapoFret, targetCapo));

        // Tuning: only the strings the phrase actually plays matter. A phrase
        // that never touches the low E does not care that the destination is in
        // drop D. Compare OFFSETS, not the tuning-type name — two arrangements
        // can spell the same tuning differently.
        var mismatched = source == null
            ? []
            : usedStrings
                .Where(s => s >= 0 && s < targetStrings)
                .Where(s => OffsetAt(source.TuningOffsets, s) != OffsetAt(targetOffsets, s))
                .ToList();
        if (mismatched.Count > 0)
        {
            issues.Add(new PhrasePasteIssue(PhrasePasteSeverity.Warning, KeyTuningDiffers,
                string.Join(", ", mismatched.Select(s => s + 1))));
        }

        // Capo: a fretted note sounds the same whatever the capo (the capo only
        // raises the floor), so the ONLY pitch casualty is an open string.
        if (playsOpenString && source != null && source.Capo != targetCapo)
        {
            issues.Add(new PhrasePasteIssue(PhrasePasteSeverity.Warning, KeyCapoDiffers,
                source.Capo, targetCapo));
        }

        if (issues.Count == 0) return Fine;

        var severity = issues.Max(i => i.Severity);
        return new PhrasePasteCheck(severity, issues);
    }

    /// <summary>Every (string, fret) pair the phrase plays — notes and chord notes.
    /// Hand shapes and anchors carry frets too, but they follow whatever the
    /// sounds do, so blocking on them would double-report the same problem.</summary>
    private static IEnumerable<(int StringIndex, int Fret)> EnumerateSounds(ClipboardPayload payload)
    {
        foreach (var note in payload.Notes)
            yield return (note.StringIndex, note.Fret);
        foreach (var chord in payload.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
                yield return (chordNote.StringIndex, chordNote.Fret);
        }
    }

    private static int OffsetAt(IReadOnlyList<int> offsets, int stringIndex)
        => stringIndex >= 0 && stringIndex < offsets.Count ? offsets[stringIndex] : 0;
}
