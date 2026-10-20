using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Deep clone of a <see cref="ChartProject"/> object graph. The single
///     source of truth for snapshotting a project — undo/redo bulk actions
///     and the save pipeline both depend on it, so every new
///     project-level collection MUST be added here or it silently vanishes
///     from undo history and torn-save protection.
///     <para>
///         "Added here" now literally means added to
///         <see cref="ChartProjectRestorer"/>: a clone is a restore into a
///         brand-new empty object, so the two cannot drift. Undo's in-place
///         snapshot restore (see <c>ChartMaintenanceService</c>) needs the same
///         field-by-field knowledge, and a second hand-written copy of it would
///         silently stop carrying any newly added model field.
///     </para>
/// </summary>
public static class ChartProjectCloner
{
    public static ChartProject Clone(ChartProject source)
    {
        var clone = new ChartProject();
        ChartProjectRestorer.Restore(clone, source);
        return clone;
    }

    public static Arrangement CloneArrangement(Arrangement source)
    {
        var clone = new Arrangement();
        ChartProjectRestorer.RestoreArrangement(clone, source);
        return clone;
    }

    public static ChartLevel CloneLevel(ChartLevel source)
    {
        var clone = new ChartLevel();
        ChartProjectRestorer.RestoreLevel(clone, source);
        return clone;
    }
}
