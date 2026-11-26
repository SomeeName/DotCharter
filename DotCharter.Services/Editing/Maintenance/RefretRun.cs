using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Refret;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
///     One refret pass over a COPY of a live arrangement: the before/after clones and the engine
///     result, plus the log line. A collaborator <see cref="ChartMaintenanceService" /> owns.
/// </summary>
internal sealed class RefretRun
{
    private readonly bool _isSelection;

    private RefretRun(Arrangement before, Arrangement target, RefretResult result, bool isSelection)
    {
        Before = before;
        Target = target;
        Result = result;
        _isSelection = isSelection;
    }

    public Arrangement Before { get; }
    public Arrangement Target { get; }
    public RefretResult Result { get; }

    public static RefretRun Execute(
        IRefretService refretService,
        ChartProject project,
        Arrangement live,
        int levelIndex,
        (FractionalPosition Start, FractionalPosition End)? range)
    {
        // One arrangement changes, so the step snapshots one.
        var before = ChartProjectCloner.CloneArrangement(live, retainIdentities: true);
        var target = ChartProjectCloner.CloneArrangement(live, retainIdentities: true);

        // A re-fretted power chord or octave the arrangement has no majority for
        // follows the song's other arrangements (read against the LIVE one, so
        // the copy never votes for itself).
        var options = new RefretOptions
        {
            SongStyle = ArrangementFingeringStyle.OfOtherArrangements(project.Arrangements, live)
        };
        var result = range != null && levelIndex >= 0 && levelIndex < target.Levels.Count
            ? refretService.RefretRange(target, target.Levels[levelIndex], project.Beats,
                range.Value.Start, range.Value.End, options)
            : refretService.RefretArrangement(target, project.Beats, options);
        return new RefretRun(before, target, result, range != null);
    }

    public string LogLine() =>
        $"Refret applied to arrangement '{Target.Name}'. Scope={(_isSelection ? "selection" : "arrangement")}, " +
        $"sounds={Result.SoundsConsidered}, notesChanged={Result.NotesChanged}, chordsChanged={Result.ChordsChanged}, " +
        $"templatesAdded={Result.TemplatesAdded}, anchorsRegenerated={Result.AnchorsRegenerated}, " +
        $"cost {Result.CostBefore:0.##}→{Result.CostAfter:0.##}, " +
        $"travel {Result.TravelBefore:0.#}→{Result.TravelAfter:0.#} (−{Result.TravelImprovementPercent}%).";
}
