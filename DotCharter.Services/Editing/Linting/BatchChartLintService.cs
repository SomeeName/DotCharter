using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Export;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Editing.Linting;

/// <summary>
/// Headless batch runner over <c>ArrangementValidationService</c> +
/// <c>ArrangementAutoFixService</c>: lints a set of project files and
/// produces a per-chart report (issues found, fixes applied when the flag is
/// set, load/save failures). No UI, no shared state — one file's failure is
/// recorded and the batch moves on. The project is read and written through the
/// injected <see cref="IChartSerializer"/>.
/// <para>
///     <b>A <c>.rscp</c> is never rewritten with DotCharter's XML</b> (Charter could not open
///     the result): its fixed project is written as the same-named <c>.dcproj</c> beside it, and the
///     Charter companion is written through the save path's own
///     <see cref="ISaveCompanionService.WriteCharterProject" />, which leaves a Charter project
///     byte-for-byte as it is. An older DotCharter <c>.rscp</c> is kept as <c>.rscp.bak</c> first,
///     like the session's first save does. A <c>.dcproj</c> already there is never overwritten.
/// </para>
/// </summary>
public sealed class BatchChartLintService : IBatchChartLintService
{
    private readonly IArrangementAutoFixService _autoFixService;
    private readonly IChartSerializer _serializer;
    private readonly IArrangementValidationService _validationService;
    private readonly ISaveCompanionService _companions;

    public BatchChartLintService(IChartSerializer serializer,
        IArrangementValidationService validationService,
        IArrangementAutoFixService autoFixService,
        ISaveCompanionService companions)
    {
        _serializer = serializer;
        _validationService = validationService;
        _autoFixService = autoFixService;
        _companions = companions;
    }

    public ChartLintBatchResult Run(IEnumerable<string> projectFilePaths,
        ChartLintOptions? options = null,
        IProgress<ChartLintProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectFilePaths);
        options ??= new ChartLintOptions();

        // Materialized because the batch has to know its own SIZE to report a
        // position at all — an IEnumerable that is still being enumerated cannot
        // say how far along it is.
        var files = projectFilePaths as IReadOnlyList<string> ?? projectFilePaths.ToList();
        var reports = new List<ChartLintReport>(files.Count);
        var canceled = false;

        for (var i = 0; i < files.Count; i++)
        {
            // Checked between files and NEVER inside one: the save half of
            // LintOne stages a temp file and then swaps it in, so a cancel that
            // landed mid-file could abandon a project between the two. Stopping
            // on a whole-file boundary means every entry in the partial result
            // is a file that was completely handled.
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            progress?.Report(new ChartLintProgress(files[i], i, files.Count));
            reports.Add(LintOne(files[i], options));
        }

        progress?.Report(new ChartLintProgress(string.Empty, reports.Count, files.Count));
        return new ChartLintBatchResult(reports, canceled);
    }

    private ChartLintReport LintOne(string path, ChartLintOptions options)
    {
        ChartProject project;
        try
        {
            project = _serializer.Load(path);
        }
        catch (Exception ex)
        {
            return new ChartLintReport(path, $"Load failed: {ex.Message}",
                Array.Empty<ArrangementLintReport>(), saved: false);
        }

        double? audioLengthMs = null;
        if (options.AudioLengthMsByPath != null && options.AudioLengthMsByPath.TryGetValue(path, out var length))
            audioLengthMs = length;

        var arrangementReports = new List<ArrangementLintReport>();
        var anyFixes = false;
        foreach (var arrangement in project.Arrangements)
        {
            var before = _validationService.Validate(arrangement, project.Beats, audioLengthMs, project);

            if (!options.ApplyAutoFixes)
            {
                arrangementReports.Add(new ArrangementLintReport(arrangement.Name, before.Count, before,
                    new Dictionary<string, int>()));
                continue;
            }

            var fixResult = new ArrangementFixResult();
            _autoFixService.Fix(arrangement, fixResult);
            anyFixes |= fixResult.TotalChanges > 0;

            var after = fixResult.TotalChanges > 0
                ? _validationService.Validate(arrangement, project.Beats, audioLengthMs, project)
                : before;
            arrangementReports.Add(new ArrangementLintReport(arrangement.Name, before.Count, after,
                new Dictionary<string, int>(fixResult.Changes)));
        }

        var saved = false;
        if (options is { ApplyAutoFixes: true, SaveFixedProjects: true } && anyFixes)
        {
            try
            {
                // STAGE AND SWAP. No .dcproj writer in the repo may
                // hand the real project path straight to the serializer —
                // and the serializer opens FileMode.Create (truncate) and then
                // fsyncs, on the documented assumption that IT is the staging
                // half of a temp-write → rename. So a crash, a full disk or a
                // serializer throw mid-write would leave a TRUNCATED project, made
                // durable, with no rename to swap in a good one.
                //
                // Blast radius is what makes this the worst instance: batch lint
                // walks a whole folder tree with SearchOption.AllDirectories and
                // rewrites headlessly, so the files at risk are projects the user
                // never opened and would not think to check.
                var target = CharterRscp.IsCharterProject(path) ? ProjectFiles.Beside(path) : path;
                if (target != path && File.Exists(target))
                    throw new IOException($"'{Path.GetFileName(target)}' already exists, and the .rscp is left as it is");
                var staging = path + ".lint-saving";
                _serializer.Save(staging, project);
                AtomicFile.ReplaceWithRetry(staging, target);
                saved = true;
                if (target != path) WriteCharterSideOfMigration(path, target, project);
            }
            catch (Exception ex)
            {
                TryDeleteStaging(path + ".lint-saving");
                return new ChartLintReport(path, $"Save failed: {ex.Message}", arrangementReports, saved: false);
            }
        }

        return new ChartLintReport(path, error: null, arrangementReports, saved);
    }

    /// <summary>
    ///     After a <c>.rscp</c>'s fixed project landed as <paramref name="dcproj" />: an older DotCharter
    ///     file is renamed to its backup name, then the Charter companion is written. Both are derived
    ///     from a project that is already safely on disk, so neither can fail the file.
    /// </summary>
    private void WriteCharterSideOfMigration(string rscpPath, string dcproj, ChartProject project)
    {
        try
        {
            if (CharterRscp.IsWrittenByDotCharter(rscpPath)) CharterRscp.MoveToLegacyBackup(rscpPath);
            _companions.WriteCharterProject(dcproj, project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The .dcproj is the result; a Charter file that could not be refreshed stays as it was.
        }
    }

    /// <summary>
    ///     Removes a half-written staging file. Best effort: the original project
    ///     is untouched either way, and leaving a stray <c>.lint-saving</c> is a
    ///     far smaller problem than the failure that produced it.
    /// </summary>
    private static void TryDeleteStaging(string staging)
    {
        try
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
