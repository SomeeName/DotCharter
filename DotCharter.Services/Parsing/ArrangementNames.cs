namespace DotCharter.Services.Parsing;

/// <summary>
///     The one "this arrangement name is already taken" rule every importer that
///     ADDS an arrangement to a project shares: <c>Lead</c>, then
///     <c>Lead (2)</c>, <c>Lead (3)</c>, … matched case-insensitively.
/// </summary>
/// <remarks>
///     <b>An arrangement name is not decoration — it is a lookup key.</b>
///     `RocksmithExportDialogViewModel.Blocks` resolves an export finding back to
///     the arrangement it came from with
///     <c>Arrangements.FirstOrDefault(a =&gt; a.Name == finding.ArrangementName)</c>,
///     so two arrangements sharing a name make a blocking error on the second one
///     read off the FIRST one's tick state, and the export gate opens for a chart
///     that has a real error in it. Rocksmith's own
///     <c>ArrangementName</c> vocabulary is a fixed six words, so an official
///     package with a bonus or alternate Lead ships TWO arrangements literally
///     named "Lead" — this is a psarc every library has, not an edge case.
/// </remarks>
public static class ArrangementNames
{
    /// <summary>How far the numeric disambiguation runs before falling back to
    /// a guid suffix (which is ugly, but is guaranteed free).</summary>
    private const int MaxNumberedAttempts = 999;

    /// <summary>
    ///     <paramref name="desired" /> when nothing in <paramref name="taken" />
    ///     already uses it, else the first free "<c>name (N)</c>" starting at 2.
    ///     A blank name falls back to <paramref name="fallback" />.
    /// </summary>
    public static string MakeUnique(IEnumerable<string> taken, string? desired, string fallback = "Imported")
    {
        ArgumentNullException.ThrowIfNull(taken);

        var name = string.IsNullOrWhiteSpace(desired) ? fallback : desired;
        var existing = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;

        for (var i = 2; i <= MaxNumberedAttempts; i++)
        {
            var candidate = $"{name} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }

        return $"{name} ({Guid.NewGuid():N})";
    }
}
