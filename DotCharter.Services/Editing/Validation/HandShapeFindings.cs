namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     The hand shapes a validation pass found something wrong WITH, read off its
/// findings — never re-derived. The 2D timeline tints these bars, and it
///     must agree with the inspector's issue list by construction, so the only input
///     is that list.
///     <list type="bullet">
///         <item>
///             <b>E05</b> (<see cref="ArrangementIssueType.SoundViolatesHandShape" />):
///             a sound under the shape disagrees with its grip — per finding,
///             <see cref="ArrangementIssue.HandShape" />.
///         </item>
///         <item>
///             <b>D09</b> (<see cref="ArrangementIssueType.HandShapeWithoutTemplate" />):
///             the shape names no template. One summary finding per arrangement, so
///             the shapes ride on <see cref="ArrangementIssue.HandShapes" />.
///         </item>
///     </list>
///     D11 (<see cref="ArrangementIssueType.ChordOutsideHandShape" />) is about a
///     chord NO bar covers, so there is no bar to mark; it is deliberately not here.
/// </summary>
public static class HandShapeFindings
{
    /// <summary>The empty answer, shared so "no findings" never allocates.</summary>
    public static IReadOnlySet<HandShape> None { get; } = new HashSet<HandShape>(ReferenceEqualityComparer.Instance);

    /// <summary>True for the finding types that mark a hand-shape bar.</summary>
    public static bool MarksABar(ArrangementIssueType type) =>
        type is ArrangementIssueType.SoundViolatesHandShape or ArrangementIssueType.HandShapeWithoutTemplate;

    /// <summary>
    ///     Every hand shape an E05 or D09 finding in <paramref name="issues" />
    ///     points at, by reference (two shapes with equal fields are still two
    ///     bars). <see cref="None" /> when there are none.
    /// </summary>
    public static IReadOnlySet<HandShape> Collect(IEnumerable<ArrangementIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        HashSet<HandShape>? found = null;
        foreach (var issue in issues)
        {
            if (!MarksABar(issue.Type)) continue;
            if (issue.HandShape != null)
                (found ??= new HashSet<HandShape>(ReferenceEqualityComparer.Instance)).Add(issue.HandShape);

            if (issue.HandShapes is not { Count: > 0 } shapes) continue;
            found ??= new HashSet<HandShape>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < shapes.Count; i++)
                found.Add(shapes[i]);
        }

        return found ?? None;
    }
}
