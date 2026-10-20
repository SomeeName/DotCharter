using System.Collections.ObjectModel;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Highway;

/// <summary>
///     The write of a technique edit that the "Apply to" ladder took past the selection: one <see cref="MutateChordsAction" /> per level the targets sit on, each against THAT
///     level's chord list with that level's spacing pass, folded into ONE history node — so a
///     single undo puts every level back.
///     <para>
///         The ONE builder for every door into a technique: the inspector's toggles and its chord
///         bend editor (<c>InspectorViewModel.ApplyChordMutation</c>) and the keyboard's technique
///         verbs (<see cref="HighwayEditingService" />, through <see cref="IChordTechniqueReach" />).
///         It lives here, under both, so the two cannot drift.
///     </para>
///     <para>
///         Only the chords change; a template is never touched (a technique lives on the chord
///         and its strings, and a template is shared). A per-string mutation picks its strings on
///         each chord by STRING index, so a string mask on the chord on screen reaches the same
///         strings of every target chord.
///     </para>
///     <para>
///         Pure apart from the actions it builds, so the default gate drives it without the
///         inspector's service graph (<c>ChordTechniqueApplyToTests</c>).
///     </para>
/// </summary>
public static class ChordTechniqueEdit
{
    /// <summary>
    ///     The history node for <paramref name="mutation" /> on <paramref name="targets" />, or null
    ///     when no target sits on a level. <paramref name="activeChords" /> is the level on screen,
    ///     which keeps the editor's own <paramref name="activePostPass" />;
    ///     <paramref name="levelPostPass" /> gives every other level its own.
    /// </summary>
    public static IChartAction? Build(IReadOnlyList<ChartLevel> levels, ObservableCollection<Chord> activeChords,
        IReadOnlyList<Chord> targets, Action<Chord> mutation, Action? activePostPass,
        Func<ChartLevel, Action?> levelPostPass)
    {
        var actions = BuildPerLevel(levels, activeChords, targets, mutation, activePostPass, levelPostPass);
        return actions.Count switch
        {
            0 => null,
            1 => actions[0],
            _ => new CompositeAction(actions)
        };
    }

    /// <summary>The per-level actions <see cref="Build" /> folds, for a caller that puts them in a
    /// node of its own (the keyboard verbs, whose selected notes ride in the same node).</summary>
    public static List<IChartAction> BuildPerLevel(IReadOnlyList<ChartLevel> levels,
        ObservableCollection<Chord> activeChords, IReadOnlyList<Chord> targets, Action<Chord> mutation,
        Action? activePostPass, Func<ChartLevel, Action?> levelPostPass)
    {
        var wanted = new HashSet<Chord>(targets, ReferenceEqualityComparer.Instance);
        var actions = new List<IChartAction>();
        var activeSeen = false;
        foreach (var level in levels)
        {
            var isActive = ReferenceEquals(level.Chords, activeChords);
            activeSeen |= isActive;
            AddLevel(actions, level.Chords, wanted, mutation, isActive ? activePostPass : levelPostPass(level));
        }

        // The level on screen is not always one of the arrangement's (a test scope, a notation voice).
        if (!activeSeen) AddLevel(actions, activeChords, wanted, mutation, activePostPass);

        return actions;
    }

    private static void AddLevel(List<IChartAction> actions, ObservableCollection<Chord> chords, HashSet<Chord> wanted,
        Action<Chord> mutation, Action? postPass)
    {
        var onLevel = chords.Where(wanted.Contains).ToList();
        if (onLevel.Count == 0) return;
        var oldStates = onLevel.Select(c => c.Clone()).ToList();
        var newStates = onLevel.Select(c =>
        {
            var clone = c.Clone();
            mutation(clone);
            return clone;
        }).ToList();
        actions.Add(new MutateChordsAction(chords, onLevel, oldStates, newStates, postPass));
    }
}
