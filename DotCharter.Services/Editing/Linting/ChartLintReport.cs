namespace DotCharter.Services.Editing.Linting;

public sealed class ChartLintOptions
{
    /// <summary>Run the <c>ArrangementAutoFixService</c> pipeline over every
    /// arrangement before the final validation pass.</summary>
    public bool ApplyAutoFixes { get; init; }

    /// <summary>Write a project back through the serializer when auto-fixes
    /// changed anything. Requires <see cref="ApplyAutoFixes"/>.</summary>
    public bool SaveFixedProjects { get; init; }

    /// <summary>Optional song length per file (keyed by path) so the
    /// note-after-song-end check can run; unknown files pass null.</summary>
    public IReadOnlyDictionary<string, double>? AudioLengthMsByPath { get; init; }
}

/// <summary>Lint outcome for one arrangement of one project.</summary>
public sealed class ArrangementLintReport
{
    public ArrangementLintReport(string arrangementName, int issuesBeforeFixes,
        IReadOnlyList<ArrangementIssue> issues, IReadOnlyDictionary<string, int> fixesApplied)
    {
        ArrangementName = arrangementName;
        IssuesBeforeFixes = issuesBeforeFixes;
        Issues = issues;
        FixesApplied = fixesApplied;
    }

    public string ArrangementName { get; }

    /// <summary>Issue count of the initial validation pass (before fixes).</summary>
    public int IssuesBeforeFixes { get; }

    /// <summary>The reportable issues: the post-fix re-validation when fixes
    /// ran, otherwise the initial pass.</summary>
    public IReadOnlyList<ArrangementIssue> Issues { get; }

    /// <summary>Per-fixer change counts (empty when fixes were not applied).</summary>
    public IReadOnlyDictionary<string, int> FixesApplied { get; }

    public int FixCount => FixesApplied.Values.Sum();
}

/// <summary>Lint outcome for one project file.</summary>
public sealed class ChartLintReport
{
    public ChartLintReport(string filePath, string? error,
        IReadOnlyList<ArrangementLintReport> arrangements, bool saved)
    {
        FilePath = filePath;
        Error = error;
        Arrangements = arrangements;
        Saved = saved;
    }

    public string FilePath { get; }

    /// <summary>Non-null when the file could not be loaded (or saved back);
    /// per-arrangement data collected before the failure is still present.</summary>
    public string? Error { get; }

    public bool Failed => Error != null;
    public IReadOnlyList<ArrangementLintReport> Arrangements { get; }

    /// <summary>True when auto-fixes changed the project and it was written back.</summary>
    public bool Saved { get; }

    public int TotalIssues => Arrangements.Sum(a => a.Issues.Count);
    public int TotalFixes => Arrangements.Sum(a => a.FixCount);
}

/// <summary>Whole-batch outcome.</summary>
public sealed class ChartLintBatchResult
{
    public ChartLintBatchResult(IReadOnlyList<ChartLintReport> charts, bool canceled = false)
    {
        Charts = charts;
        Canceled = canceled;
    }

    public IReadOnlyList<ChartLintReport> Charts { get; }

    /// <summary>
    ///     True when the caller cancelled and the batch stopped early. Everything
    ///     in <see cref="Charts" /> still ran to completion — the flag exists so a
    ///     partial result is REPORTED as partial instead of reading like a clean
    ///     sweep of a library that was never fully walked.
    /// </summary>
    public bool Canceled { get; }

    /// <summary>Distinct fixer names → total change count, across the batch. The
    /// honest answer to "what did that bulk overwrite actually change?".</summary>
    public IReadOnlyDictionary<string, int> FixClasses
    {
        get
        {
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var chart in Charts)
            {
                foreach (var arrangement in chart.Arrangements)
                {
                    foreach (var (fixer, count) in arrangement.FixesApplied)
                        totals[fixer] = totals.TryGetValue(fixer, out var running) ? running + count : count;
                }
            }

            return totals;
        }
    }
    public int FailedCount => Charts.Count(c => c.Failed);
    public int SavedCount => Charts.Count(c => c.Saved);
    public int TotalIssues => Charts.Sum(c => c.TotalIssues);
    public int TotalFixes => Charts.Sum(c => c.TotalFixes);
}
