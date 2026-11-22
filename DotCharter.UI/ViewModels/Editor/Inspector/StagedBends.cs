namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     A staged bend curve: the string it is drawn on (null for a note), the curve, the curve the source held when
///     staging began, and the span (onset and sustain) both were drawn over. Only an EDITED curve writes: an untouched
///     one never overwrites another target's bend. The span keeps the draft relative to its sound: a move or a sustain
///     change carries it along (<see cref="StagedBends.Rebase" />).
/// </summary>
public sealed record BendDraft(int? StringIndex, IReadOnlyList<BendValue> Values, IReadOnlyList<BendValue> Baseline,
    FractionalPosition Onset, FractionalPosition Sustain)
{
    public bool IsEdited => !StagedBends.SameCurve(Values, Baseline);
}

/// <summary>
///     The Bends page (design D's <c>BendReplacements</c> on real types), pure. A chord's curve lands on
///     the same STRING of every target that plays it (a target without it is named and passed by), every staged string
///     at once; each target gets the curve remapped onto its own onset and sustain (<see cref="BendCurveRemap" />); a
///     target whose own authored bend differs, the source included, is named before Apply replaces it.
/// </summary>
internal static class StagedBends
{
    internal static StagedEditPlan Plan(ITimedElement source, IReadOnlyList<BendDraft> drafts, IReadOnlyList<ITimedElement> targets)
    {
        var writes = new List<ITimedElement>();
        var written = new HashSet<ITimedElement>(ReferenceEqualityComparer.Instance);
        var skips = new List<StagedSkip>();
        var replaced = new HashSet<ITimedElement>(ReferenceEqualityComparer.Instance);
        foreach (var draft in drafts)
        {
            if (!draft.IsEdited || Span(source, draft.StringIndex) is null) continue;
            foreach (var target in targets)
            {
                if (Curve(target, draft.StringIndex) is not { } held || Span(target, draft.StringIndex) is not { } to)
                {
                    skips.Add(new StagedSkip(target, StagedSkipReason.StringSilent, draft.StringIndex));
                    continue;
                }

                if (written.Add(target)) writes.Add(target);
                if (!held.Any(v => v.Step > 0)) continue;
                var landing = BendCurveRemap.Remap(draft.Values, draft.Onset, draft.Sustain, to.Onset, to.Sustain);
                if (!SameCurve(held, landing)) replaced.Add(target);
            }
        }

        return StagedEditPlan.From(writes, skips, [.. writes.Where(replaced.Contains)]);
    }

    /// <summary>What Apply does to a note target.</summary>
    internal static Action<GuitarNote> NoteWrite(BendDraft draft) =>
        BendCurveRemap.NoteWrite(draft.Values, draft.Onset, draft.Sustain);

    /// <summary>What Apply does to a chord target: every staged string, each onto the same string of the target.</summary>
    internal static Action<Chord> ChordWrite(IReadOnlyList<BendDraft> drafts) => drafts
        .Where(d => d is { IsEdited: true, StringIndex: not null })
        .Select(d => BendCurveRemap.ChordWrite(d.StringIndex!.Value, d.Values, d.Onset, d.Sustain))
        .Aggregate((Action<Chord>)(_ => { }), (all, one) => all + one);

    /// <summary>
    ///     The draft against its sound after a history change. A sound that moved or changed its sustain carries the draft
    ///     along (curve and baseline remapped onto the new span). A sound whose bend on the string was edited since (it
    ///     holds neither the baseline nor the baseline carried along) returns null with <paramref name="edited" />, so Apply
    ///     never writes over the keyboard; a sound without the string returns null.
    /// </summary>
    internal static BendDraft? Rebase(ITimedElement source, BendDraft draft, out bool edited)
    {
        edited = false;
        if (Curve(source, draft.StringIndex) is not { } held || Span(source, draft.StringIndex) is not { } span) return null;
        var moved = span.Onset != draft.Onset || span.Sustain != draft.Sustain;
        var carried = moved ? Carry(draft.Baseline, draft, span) : draft.Baseline;
        if (!SameCurve(held, draft.Baseline) && !SameCurve(held, carried))
        {
            edited = true;
            return null;
        }

        var values = moved ? Carry(draft.Values, draft, span) : draft.Values;
        return draft with { Values = values, Baseline = [.. held], Onset = span.Onset, Sustain = span.Sustain };
    }

    private static IReadOnlyList<BendValue> Carry(IReadOnlyList<BendValue> values, BendDraft from,
        (FractionalPosition Onset, FractionalPosition Sustain) to) =>
        BendCurveRemap.Remap(values, from.Onset, from.Sustain, to.Onset, to.Sustain);

    /// <summary>The curve <paramref name="sound" /> holds on <paramref name="stringIndex" />, or null when it has no such string.</summary>
    internal static IReadOnlyList<BendValue>? Curve(ITimedElement sound, int? stringIndex) => sound switch
    {
        GuitarNote note when stringIndex is null => note.BendValues,
        Chord chord when stringIndex is { } index => chord.ChordNotes.FirstOrDefault(n => n.StringIndex == index)?.BendValues,
        _ => null
    };

    internal static bool SameCurve(IReadOnlyList<BendValue> a, IReadOnlyList<BendValue> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Position == p.Second.Position && p.First.Step.Equals(p.Second.Step));

    /// <summary>The onset and sustain the curve on <paramref name="stringIndex" /> spans, or null without that string.</summary>
    internal static (FractionalPosition Onset, FractionalPosition Sustain)? Span(ITimedElement sound, int? stringIndex) => sound switch
    {
        GuitarNote note when stringIndex is null => (note.Position, note.Sustain),
        Chord chord when chord.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex) is { } strand =>
            (chord.Position, strand.Sustain),
        _ => null
    };
}
