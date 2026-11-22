using Avalonia.Threading;
using DotCharter.Services.Editing.Selection;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The app state a surface needs before the UI sweep builds it, where the shared fixture (a song with nothing
///     selected) would leave the surface showing only its empty line. Each stage puts the state in through the
///     product's own services and takes it out again on dispose, so no later surface sees it.
///     <list type="bullet">
///         <item><c>SoundShapeView</c> (the Note tab's Shape page): a chord of the active level is selected, so the
///         page shows its rows, header and pages and the accessibility audit walks them ("edit a chord" on the
///         task path, <see cref="AccessibilityAuditUiPerfSmokeTests.TaskPath" />).</item>
///     </list>
/// </summary>
internal static class UiSweepStages
{
    /// <summary>Stages <paramref name="surface" />; dispose the result after the surface was swept.</summary>
    public static IDisposable Enter(string surface, IServiceProvider services) => surface switch
    {
        "SoundShapeView" => SelectAChord(services),
        _ => Nothing.Instance
    };

    private static IDisposable SelectAChord(IServiceProvider services)
    {
        var level = services.GetRequiredService<ProjectState>().ActiveLevel
                    ?? throw new InvalidOperationException("the sweep fixture has no active level to select a chord in");
        var chord = level.Chords.FirstOrDefault()
                    ?? throw new InvalidOperationException("the sweep fixture's active level holds no chord");
        var selection = services.GetRequiredService<ISelectionService>();
        selection.ClearSelections();
        selection.SelectedChords.Add(chord);
        Dispatcher.UIThread.RunJobs();
        return new Restore(() =>
        {
            selection.ClearSelections();
            Dispatcher.UIThread.RunJobs();
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
