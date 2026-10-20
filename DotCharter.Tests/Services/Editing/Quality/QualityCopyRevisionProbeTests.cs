using System.Globalization;
using System.Text;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Quality;

namespace DotCharter.Tests.Services.Editing.Quality;

/// <summary>
///     The MEASUREMENT behind #720, not a gate: what the quality report's UI-thread copy costs on the
///     five biggest corpus projects with a 20-level ladder, when the spare is restored in full, when
///     nothing changed since it was filled, after one committed edit in one arrangement, and after
///     that edit's undo. Skips unless <c>DOTCHARTER_EOF_ROUNDTRIP_PROBE=1</c> and the corpus is
///     present; prints a Markdown table. Debug/JIT numbers under load are noisy.
/// </summary>
public sealed class QualityCopyRevisionProbeTests(ITestOutputHelper output)
{
    private const string ProbeVar = "DOTCHARTER_EOF_ROUNDTRIP_PROBE";

    [Fact]
    public void TheLadderedCharts_LogTheCopyCost()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(ProbeVar) != "1", $"{ProbeVar} is not 1");
        var root = EofRoundTripCostProbeTests.FindCorpus();
        Assert.SkipWhen(root == null, "No RsCharter corpus (TestData/ is git-ignored).");

        var table = new StringBuilder();
        table.AppendLine("| Project | Arr | Levels | Notes+chords | Clone (ms) | Spare, full restore (ms) | Spare, nothing changed (ms) | Spare, one edit (ms) | Spare, after undo (ms) |");
        table.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (song, project) in EofRoundTripCostProbeTests.LoadCorpus(root!)
                     .OrderByDescending(p => EofRoundTripCostProbeTests.Notes(p.Project)).Take(5))
        {
            var live = EofRoundTripCostProbeTests.Laddered(project);
            var state = new ProjectState { ActiveProject = live };
            var history = new HistoryService(new FakeAppLogger(), new EditingScope(state));
            var level = live.Arrangements.SelectMany(a => a.Levels).Last(l => l.Notes.Count > 0);
            var note = level.Notes[level.Notes.Count / 2];

            var clone = EofRoundTripCostProbeTests.Median(() => EofRoundTripCostProbeTests.Collected(() => ChartProjectCloner.Clone(live)));
            var spare = ChartProjectCloner.Clone(live);
            var full = EofRoundTripCostProbeTests.Median(() => EofRoundTripCostProbeTests.Collected(() => ChartProjectRestorer.Restore(spare, live)));

            var copies = new QualityReportCopies();
            copies.Return(copies.Copy(live, out _));
            var unchanged = Sample(copies, live, () => { });
            var edited = Sample(copies, live,
                () => history.Do(new MutateNotesAction(level.Notes, [note], n => n.Fret = n.Fret == 3 ? 4 : 3)));
            var undone = Sample(copies, live, history.Undo);

            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {song} (20-level ladder) | {live.Arrangements.Count} | {live.Arrangements.Sum(a => a.Levels.Count)} | {EofRoundTripCostProbeTests.Notes(live)} | {clone:F1} | {full:F1} | {unchanged:F1} | {edited:F1} | {undone:F1} |"));
        }

        output.WriteLine(table.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "dotcharter_quality_copy_revision_cost.md"), table.ToString());
    }

    /// <summary>The median copy of the spare after <paramref name="change" />, the spare handed back each time.</summary>
    private static double Sample(QualityReportCopies copies, ChartProject live, Action change) =>
        EofRoundTripCostProbeTests.Median(() =>
        {
            change();
            ChartProject? copy = null;
            var ms = EofRoundTripCostProbeTests.Collected(() => copy = copies.Copy(live, out _));
            copies.Return(copy!);
            return ms;
        });
}
