using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The quality report's checks, the spare copy that outlives one opening, and the two steps a test
/// replaces: the copy after the busy row has painted (a background post) and the checks off the UI thread.</summary>
public sealed class QualityReportChecks(ChartQualityReporter reporter, IUiDispatcher ui)
{
    public ChartQualityReporter Reporter { get; } = reporter;

    public QualityReportCopies Copies { get; } = new();

    internal Action<Action> AfterPaint { get; set; } = ui.Post;

    internal Func<Func<QualityReportCheck>, Task<QualityReportCheck>> OffThread { get; set; } = Task.Run;
}
