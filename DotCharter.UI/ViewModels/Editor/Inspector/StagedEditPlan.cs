namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>The Note pages whose edits are staged and written by the pinned Apply (design D).</summary>
public enum StagedPage
{
    Placement,
    Selection,
    Technique,
    Bends
}

/// <summary>Why an Apply passes a target (or part of one) by. Each is named in the review: as skipped when
/// nothing is written there, as written in part when the rest still lands.</summary>
public enum StagedSkipReason
{
    /// <summary>The target does not play the string a staged per-string value is for.</summary>
    StringSilent,

    /// <summary>A staged slide would end off the neck there; the target's slide stays as it is.</summary>
    SlideOffNeck,

    /// <summary>No staged value lands on the target (a strum on a note, a note mute on a chord).</summary>
    NothingLands,

    /// <summary>A move would push the target off the neck or the strings; it stays where it is.</summary>
    LeavesNeck,

    /// <summary>A sustain step would go below zero; the target stops at zero.</summary>
    SustainFloor,

    /// <summary>A sustain step stops before the next sound (its spacing gap).</summary>
    SustainNextSound,

    /// <summary>A sustain step stops at the end of the audio.</summary>
    SustainAudioEnd,

    /// <summary>A linked sustain is the gap to the next note and does not step.</summary>
    SustainLinked
}

/// <summary>A target an Apply passes by, or part of it (<paramref name="StringIndex" />), and why.</summary>
public sealed record StagedSkip(ITimedElement Target, StagedSkipReason Reason, int? StringIndex = null);

/// <summary>
///     What one Apply would do, worked out before it writes anything: the targets it writes, the targets it
///     passes by (<see cref="Skips" />: nothing is written there), the written targets that keep part of
///     the edit (<see cref="Partly" />), and the written targets whose own authored bend it replaces. Pure
///     data: the review sentence and the footer count read it, and Apply writes exactly <see cref="Writes" />.
/// </summary>
public sealed record StagedEditPlan(
    IReadOnlyList<ITimedElement> Writes,
    IReadOnlyList<StagedSkip> Skips,
    IReadOnlyList<ITimedElement> Replacements,
    IReadOnlyList<StagedSkip> Partly)
{
    public static readonly StagedEditPlan Empty = new([], [], [], []);

    public int Count => Writes.Count;

    /// <summary>The plan for <paramref name="writes" />: each note on a written target is a part it keeps
    /// (<see cref="Partly" />), each on any other target a skip; a replacement counts only where it is written.</summary>
    public static StagedEditPlan From(IReadOnlyList<ITimedElement> writes, IEnumerable<StagedSkip> notes,
        IEnumerable<ITimedElement> replacements)
    {
        var written = new HashSet<ITimedElement>(writes, ReferenceEqualityComparer.Instance);
        var all = notes.ToList();
        return new StagedEditPlan(writes, [.. all.Where(n => !written.Contains(n.Target))],
            [.. replacements.Where(written.Contains)], [.. all.Where(n => written.Contains(n.Target))]);
    }

    /// <summary>The same plan written only on the targets <paramref name="changes" /> holds for (a target
    /// that already holds every staged value is not counted); a note on a dropped target becomes a skip.</summary>
    public StagedEditPlan Where(Func<ITimedElement, bool> changes) =>
        From([.. Writes.Where(changes)], [.. Skips, .. Partly], Replacements);
}

/// <summary>
///     The sentence the footer shows before Apply writes, and whether Apply must ask first: more than one
///     target, a skip or a replaced bend is confirmed (D's rule); a single plain write is not.
/// </summary>
public sealed record StagedEditReview(string Text, bool NeedsConfirm, int Count)
{
    public static readonly StagedEditReview None = new(string.Empty, false, 0);
}
