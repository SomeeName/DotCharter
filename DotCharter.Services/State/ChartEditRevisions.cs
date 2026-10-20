namespace DotCharter.Services.State;

/// <summary>
/// Marks the arrangements a committed chart action can have changed, so
///     <see cref="Arrangement.GetRevision" /> moves on every edit. <see cref="HistoryService" /> calls it
///     for each do, merged gesture tick, direct push, undo and redo, and for an apply that threw.
///     <para>
///         It errs WIDE: an action it cannot place marks every arrangement of the project. Only two
///         shapes are placed narrowly. A collection action (<see cref="ICollectionChartAction" />: the
///         add, remove and mutate actions) edits its collection and that collection's elements, so it
///         marks the arrangement that owns the collection — every arrangement when no arrangement
///         does (the beat grid, a collection a tool has since replaced). Its post-execute pass is the
///         spacing enforcer over the editing scope's level, which can be another arrangement's on an
///         undo, so an action with one also marks the scope's arrangement. A composite marks what its
///         children mark.
///     </para>
/// </summary>
internal static class ChartEditRevisions
{
    public static void MarkEdited(IChartAction? action, ChartProject? project, Arrangement? scope)
    {
        if (project == null)
        {
            scope?.MarkEdited();
            return;
        }

        var reached = new List<Arrangement>(2);
        if (action == null || !TryPlace(action, project, scope, reached))
        {
            foreach (var arrangement in project.Arrangements) arrangement.MarkEdited();
            return;
        }

        foreach (var arrangement in reached) arrangement.MarkEdited();
    }

    private static bool TryPlace(IChartAction action, ChartProject project, Arrangement? scope, List<Arrangement> reached)
    {
        switch (action)
        {
            case CompositeAction composite:
                foreach (var child in composite.Actions)
                {
                    if (!TryPlace(child, project, scope, reached)) return false;
                }

                return true;
            case ICollectionChartAction edit:
                if (OwnerOf(project, edit.EditedCollection) is not { } owner) return false;
                Add(reached, owner);
                if (!edit.HasPostPass) return true;
                if (scope == null) return false;
                Add(reached, scope);
                return true;
            default:
                return false;
        }
    }

    private static void Add(List<Arrangement> reached, Arrangement arrangement)
    {
        foreach (var known in reached)
        {
            if (ReferenceEquals(known, arrangement)) return;
        }

        reached.Add(arrangement);
    }

    /// <summary>The arrangement whose own collections, levels or notation voices hold <paramref name="collection" />.</summary>
    internal static Arrangement? OwnerOf(ChartProject project, object collection)
    {
        foreach (var arrangement in project.Arrangements)
        {
            if (Holds(arrangement, collection)) return arrangement;
        }

        return null;
    }

    private static bool Holds(Arrangement a, object c)
    {
        if (ReferenceEquals(a.TuningPitches, c) || ReferenceEquals(a.ToneNames, c) || ReferenceEquals(a.Levels, c) ||
            ReferenceEquals(a.NotationVoices, c) || ReferenceEquals(a.ChordTemplates, c) ||
            ReferenceEquals(a.FretHandMuteTemplates, c) || ReferenceEquals(a.EventPoints, c) ||
            ReferenceEquals(a.ToneChanges, c) || ReferenceEquals(a.PhraseIterations, c))
        {
            return true;
        }

        foreach (var level in a.Levels)
        {
            if (ReferenceEquals(level.Notes, c) || ReferenceEquals(level.Chords, c) || ReferenceEquals(level.Anchors, c) ||
                ReferenceEquals(level.HandShapes, c) || ReferenceEquals(level.FretHandMutes, c))
            {
                return true;
            }
        }

        foreach (var voice in a.NotationVoices)
        {
            if (ReferenceEquals(voice.Notes, c) || ReferenceEquals(voice.Chords, c)) return true;
        }

        return false;
    }
}

/// <summary>An action whose every edit is to one collection and its elements, plus an optional post-execute pass.</summary>
internal interface ICollectionChartAction
{
    object EditedCollection { get; }
    bool HasPostPass { get; }
}
