namespace DotCharter.Services.Editing.Linting;

/// <summary>
///     One step of a batch lint run: the file that is ABOUT to be linted and how
///     many of the batch are already finished.
///     <para>
///         Reported BEFORE each file, so a UI can name the file it is working on
///         rather than the one it has just left behind, and once more after the
///         loop with an empty <see cref="FilePath" /> — that final report is what
///         drives the bar to its true end position on a run that stopped early.
///     </para>
/// </summary>
public readonly record struct ChartLintProgress(string FilePath, int CompletedCount, int TotalCount)
{
    /// <summary>0..1, clamped. 0 for an empty batch rather than NaN.</summary>
    public double Fraction =>
        TotalCount <= 0 ? 0.0 : Math.Clamp(CompletedCount / (double)TotalCount, 0.0, 1.0);
}

public interface IBatchChartLintService
{
    /// <summary>
    ///     Lints every project file in <paramref name="projectFilePaths" />:
    ///     loads it, validates every arrangement, optionally applies the
    ///     auto-fix pipeline (and re-validates), optionally saves fixed projects
    ///     back. One file's failure never aborts the batch — it becomes a failed
    ///     entry in the result. See <see cref="BatchChartLintService" />.
    ///     <para>
    ///         A whole library is MINUTES of recursive I/O that can also REWRITE
    ///         <c>.rscp</c> files the user never opened, so the run is
    ///         interruptible and reports its position: <paramref name="progress" />
    ///         fires per file and <paramref name="cancellationToken" /> stops the
    ///         batch on a whole-file boundary. The partial result is returned with
    ///         <see cref="ChartLintBatchResult.Canceled" /> set — never thrown, so
    ///         the caller can report honestly what really ran.
    ///     </para>
    /// </summary>
    ChartLintBatchResult Run(IEnumerable<string> projectFilePaths,
        ChartLintOptions? options = null,
        IProgress<ChartLintProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
