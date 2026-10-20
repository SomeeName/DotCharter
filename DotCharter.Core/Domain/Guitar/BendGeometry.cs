using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Guitar;

/// <summary>
///     Pure geometry helpers for a sound's bend curve. The one invariant every
///     sustain-changing pass must uphold: a bend point can never lie outside the
///     note it belongs to — a bend cannot sound before the note is struck or after
///     it stops ringing.
///     <para>
///     This lives in <c>Core</c> so every layer that shortens a sustain shares the
///     SAME clamp: GP import (<c>GpPostProcessor</c>), the save/edit spacing pass
///     (<c>ChartSpacingEnforcer</c>) and any future editor mutation. Without it
///     the spacing enforcer would shorten sustains but leave bend points where they
///     were, so a bend authored near the end of a long sustain would render PAST the
///     shortened note — the recurring "bends outside the note sustain" report.
///     </para>
/// </summary>
public static class BendGeometry
{
    /// <summary>
    ///     Pulls every bend point of <paramref name="sound" /> into
    ///     <c>[position, position + sound.Sustain]</c>. Points past the end collapse
    ///     onto the end (the curve holds its reached value to the note end); points
    ///     before the start rise to the start. Idempotent — a curve already in range
    ///     is untouched. Empty bend lists are a no-op.
    /// </summary>
    /// <param name="sound">The sound whose bend points are clamped in place.</param>
    /// <param name="position">The sound's ABSOLUTE onset (note or chord position);
    /// bend positions are absolute too, so the end is <c>position + Sustain</c>.</param>
    public static void ClampBendsToSustain(IGuitarSound sound, FractionalPosition position)
    {
        var bends = sound.BendValues;
        if (bends.Count == 0) return;

        var end = position + sound.Sustain;
        foreach (var bend in bends)
        {
            if (bend.Position.CompareTo(end) > 0) bend.Position = end;
            else if (bend.Position.CompareTo(position) < 0) bend.Position = position;
        }
    }

    /// <summary>
    ///     True when any bend point of <paramref name="sound" /> lies strictly
    ///     outside <c>[position, position + sound.Sustain]</c> — the negation of the
    ///     invariant <see cref="ClampBendsToSustain" /> enforces. Exists so tests
    ///     (and asserts) can state the invariant directly.
    /// </summary>
    public static bool HasBendOutsideSustain(IGuitarSound sound, FractionalPosition position)
    {
        var bends = sound.BendValues;
        if (bends.Count == 0) return false;

        var end = position + sound.Sustain;
        foreach (var bend in bends)
        {
            if (bend.Position.CompareTo(end) > 0 || bend.Position.CompareTo(position) < 0)
                return true;
        }

        return false;
    }
}
