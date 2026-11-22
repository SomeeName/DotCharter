using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Collisions;

/// <summary>Plans settings enforcement on detached arrangements and records all resulting changes together.</summary>
public static class ProjectSpacingEdit
{
    public static IChartAction Plan(ChartProject project, AppConfig config, Action changed)
    {
        var actions = new List<IChartAction>();
        foreach (var arrangement in project.Arrangements)
        {
            var before = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
            var after = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
            ChartSpacingEnforcer.EnforceArrangement(after, project.Beats, config);
            if (SameEnforcedContent(before, after)) continue;
            actions.Add(new EnforceArrangementSpacingAction(arrangement, before, after, changed));
        }

        return new CompositeAction(actions)
        {
            LabelKey = "History_EnforceProjectSpacing",
            LinkPolicyOverride = LinkPolicy.UnlinkOnDivergence
        };
    }

    private static bool SameEnforcedContent(Arrangement before, Arrangement after)
    {
        if (before.ChordTemplates.Count != after.ChordTemplates.Count) return false;
        for (var i = 0; i < before.ChordTemplates.Count; i++)
        {
            var left = before.ChordTemplates[i];
            var right = after.ChordTemplates[i];
            if (left.Id != right.Id || !left.Equals(right)) return false;
        }

        for (var i = 0; i < before.Levels.Count; i++)
        {
            var left = before.Levels[i];
            var right = after.Levels[i];
            if (!SameItems(left.Notes, right.Notes, ChartActionStateComparer.GuitarNotesEqual) ||
                !SameItems(left.Chords, right.Chords, ChartActionStateComparer.ChordsEqual) ||
                !SameItems(left.HandShapes, right.HandShapes, ChartActionStateComparer.HandShapesEqual) ||
                !SameItems(left.Anchors, right.Anchors, ChartActionStateComparer.AnchorsEqual))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameItems<T>(IList<T> before, IList<T> after, Func<T, T, bool> equal)
    {
        if (before.Count != after.Count) return false;
        for (var i = 0; i < before.Count; i++)
        {
            if (!equal(before[i], after[i])) return false;
        }

        return true;
    }

    private sealed class EnforceArrangementSpacingAction(Arrangement live, Arrangement before, Arrangement after,
        Action changed) : IChartAction, ILinkPolicyAction
    {
        public LinkPolicy LinkPolicy => LinkPolicy.UnlinkOnDivergence;
        public long EstimatedBytes { get; } = ChartProjectFootprint.EstimateHistoryBytes(before) + ChartProjectFootprint.EstimateHistoryBytes(after);

        public void Execute() => Restore(after);
        public void Undo() => Restore(before);

        private void Restore(Arrangement snapshot)
        {
            ChartProjectRestorer.RestoreArrangement(live, snapshot);
            changed();
        }
    }
}
