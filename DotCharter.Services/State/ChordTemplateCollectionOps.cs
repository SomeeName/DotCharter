namespace DotCharter.Services.State;

/// <summary>
///     Reference-identity operations over a collection of
///     <see cref="ChordTemplate" />, for the undo actions that OWN specific
///     template instances.
///     <para>
///         <b><see cref="ChordTemplate" /> has VALUE equality.</b> It overrides
///         <c>Equals</c>/<c>GetHashCode</c> over name + arpeggio flags + frets +
///         fingers and deliberately EXCLUDES <see cref="ChordTemplate.Id" /> —
///         the diff engine compares an arrangement's templates as a multiset and
///         the power-chord builder find-or-creates with it, and both are correct
///         to. That equality is not being changed.
///     </para>
///     <para>
///         What it means for a COLLECTION, though, is that every call resolved by
///         <see cref="EqualityComparer{T}.Default" /> — <c>Remove</c>,
///         <c>Contains</c>, <c>IndexOf</c> — silently targets the FIRST
///         VALUE-EQUAL template instead of the instance the caller holds. Two
///         value-equal templates with different ids are ordinary chart state (an
///         RS-XML / psarc / rscp file lists templates verbatim and nothing
///         dedupes them on the way in, and the template editor's "create new" +
///         rename path mints them by hand), so the undo of a template-creating
///         edit removed a DIFFERENT, still-referenced template. The chords
///         pointing at it were left with a dangling <c>TemplateId</c>, and
///         <c>SngFromDomainMapper</c> filters chords on
///         <c>TemplateIndexById.ContainsKey</c> — so the chord was dropped from
///         the exported psarc with no error anywhere.
///     </para>
///     <para>
///         Use these whenever the QUESTION is "this exact instance". Use the
///         value comparison when the question is "a template like this one".
///     </para>
/// </summary>
public static class ChordTemplateCollectionOps
{
    /// <summary>True when <paramref name="template" /> is IN
    /// <paramref name="collection" /> by reference.</summary>
    public static bool ContainsInstance(IEnumerable<ChordTemplate> collection, ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(template);

        foreach (var candidate in collection)
        {
            if (ReferenceEquals(candidate, template))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Removes exactly the instance <paramref name="template" /> from
    ///     <paramref name="collection" />; returns whether it was there.
    /// </summary>
    public static bool RemoveInstance(IList<ChordTemplate> collection, ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(template);

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(collection[i], template))
            {
                collection.RemoveAt(i);
                return true;
            }
        }

        return false;
    }
}
