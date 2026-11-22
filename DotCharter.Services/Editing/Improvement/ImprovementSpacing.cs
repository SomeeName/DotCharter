using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Improvement;

/// <summary>Counts the elements changed by spacing and compatibility cleanup, including metadata-only edits.</summary>
internal static class ImprovementSpacing
{
    public static int Enforce(ChartProject project, AppConfig config)
    {
        var changes = 0;
        foreach (var arrangement in project.Arrangements)
        {
            var before = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
            ChartSpacingEnforcer.EnforceArrangement(arrangement, project.Beats, config);
            changes += CountChanges(before.ChordTemplates, arrangement.ChordTemplates, (a, b) => a.Equals(b));
            for (var i = 0; i < arrangement.Levels.Count; i++)
            {
                var oldLevel = before.Levels[i];
                var level = arrangement.Levels[i];
                changes += CountChanges(oldLevel.Notes, level.Notes, ChartActionStateComparer.GuitarNotesEqual);
                changes += CountChanges(oldLevel.Chords, level.Chords, ChartActionStateComparer.ChordsEqual);
                changes += CountChanges(oldLevel.Anchors, level.Anchors, ChartActionStateComparer.AnchorsEqual);
                changes += CountChanges(oldLevel.HandShapes, level.HandShapes, ChartActionStateComparer.HandShapesEqual);
            }
        }

        return changes;
    }

    // Comparing ordered elements also notices sorting, removals and newly minted templates.
    private static int CountChanges<T>(IList<T> before, IList<T> after, Func<T, T, bool> equal)
    {
        var changes = Math.Abs(before.Count - after.Count);
        for (var i = 0; i < Math.Min(before.Count, after.Count); i++)
        {
            if (!equal(before[i], after[i])) changes++;
        }

        return changes;
    }
}
