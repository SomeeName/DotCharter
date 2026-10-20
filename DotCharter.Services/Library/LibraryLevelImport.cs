using System.Runtime.ExceptionServices;
using DotCharter.Services.Editing.Improvement;
using DotCharter.Services.State;

namespace DotCharter.Services.Library;

/// <summary>
/// Settings ▸ Files ▸ "Hardest difficulty level only": a library song opens with each
/// arrangement squashed to one level instead of its whole ladder. It is the editor's
///     own Squash levels (<see cref="LevelSquashService" />), and the hardest level is already the
///     whole song (<c>RsLevelLadder.FillInheritedPhrases</c>), so no note is lost. The chord
///     templates only the dropped levels played go with them, by the editor squash's own rule
/// (<see cref="LevelTemplateOrphans" />).
/// </summary>
public static class LibraryLevelImport
{
    /// <summary>Squashes every arrangement with more than one level; how many it squashed.</summary>
    public static int KeepHardestOnly(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var squashed = 0;
        foreach (var arrangement in project.Arrangements)
        {
            if (SquashToHardest(arrangement)) squashed++;
        }

        return squashed;
    }

    /// <summary>
    ///     Squashes one arrangement in place and drops the chord templates the squash left with
    /// no user — the editor's Squash levels (<c>ChartMaintenanceService.SquashLevels</c>)
    ///     without its undo node, because an import has no history to step back into.
    ///     <see cref="LevelTemplateOrphans.Apply" /> renumbers the survivors to their positions and
    ///     remaps every chord and hand shape; the package and RS XML readers number templates by
    ///     position, which its guard needs.
    /// </summary>
    private static bool SquashToHardest(Arrangement arrangement)
    {
        var templates = LevelTemplateOrphans.Capture(arrangement);
        List<ChordTemplate> declared = [.. arrangement.ChordTemplates];
        if (!new LevelSquashService().TrySquash(arrangement)) return false;

        // The squash ran in place, so the "live" list is the one captured; each declared orphan
        // comes back parked (id -1) in its slot and leaves here, by instance.
        foreach (var orphan in templates.Apply(arrangement, declared))
            ChordTemplateCollectionOps.RemoveInstance(arrangement.ChordTemplates, orphan);
        return true;
    }

    /// <summary>
    /// The worker pass between a library song's parse and its apply:
    ///     <see cref="KeepHardestOnly" /> when <see cref="AppConfig.PsarcOpenHardestLevelOnly" />
    ///     asks for it, then <see cref="ChartSpacingEnforcer.EnforceProject" />. Both are a
    ///     loop of per-ARRANGEMENT work that reads only the beat grid and the config, so each
    ///     arrangement runs on its own pool thread; the result is the sequential one. A
    ///     failure is rethrown as the sequential order would have met it first (every
    ///     squash before any spacing pass), as the original exception.
    /// </summary>
    public static void PrepareForEditor(ChartProject project, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(config);
        var arrangements = project.Arrangements.ToArray();
        var squashFailures = new ExceptionDispatchInfo?[arrangements.Length];
        var spacingFailures = new ExceptionDispatchInfo?[arrangements.Length];
        Parallel.For(0, arrangements.Length, i =>
        {
            try
            {
                if (config.PsarcOpenHardestLevelOnly) SquashToHardest(arrangements[i]);
            }
            catch (Exception ex)
            {
                squashFailures[i] = ExceptionDispatchInfo.Capture(ex);
                return;
            }

            try
            {
                ChartSpacingEnforcer.EnforceArrangement(arrangements[i], project.Beats, config);
            }
            catch (Exception ex)
            {
                spacingFailures[i] = ExceptionDispatchInfo.Capture(ex);
            }
        });

        Array.Find(squashFailures, f => f != null)?.Throw();
        Array.Find(spacingFailures, f => f != null)?.Throw();
    }
}
