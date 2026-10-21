// .NET 10 file-based program: runs the real Guitar Pro importer over a local tab corpus and
// reports import health and linked-measure candidates (#82, Phase 0 of #76). Read-only.
//
// Usage, outputs and the rules for the corpus: tools/tab-corpus/README.md.
#:project ../../DotCharter.Services/DotCharter.Services.csproj
#:property PublishAot=false
#:property Nullable=enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Import.GuitarPro.Report;

// Stay out of the way of whoever is using this machine: upper half of the cores, idle priority.
try
{
    using var self = Process.GetCurrentProcess();
    self.PriorityClass = ProcessPriorityClass.Idle;
    if (Environment.ProcessorCount >= 16) self.ProcessorAffinity = (IntPtr)0xFF00;
}
catch (Exception) { /* best effort */ }

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string? Get(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var tabs = Get("--tabs") ?? Environment.GetEnvironmentVariable("DOTCHARTER_TABS_DIR");
var output = Get("--out") ?? Environment.GetEnvironmentVariable("DOTCHARTER_TAB_REPORT_DIR");
if (string.IsNullOrWhiteSpace(tabs) || !Directory.Exists(tabs) || string.IsNullOrWhiteSpace(output))
{
    Console.Error.WriteLine("usage: dotnet run tools/tab-corpus/TabCorpusReport.cs -- [--tabs <dir>] --out <dir> [--parallel N] [--limit N] [--filter <substr>] [--no-roundtrip] [--timeout-s N]");
    Console.Error.WriteLine("       --tabs defaults to DOTCHARTER_TABS_DIR, --out to DOTCHARTER_TAB_REPORT_DIR. The output must be outside the repository.");
    Environment.Exit(2);
}

var parallel = int.TryParse(Get("--parallel"), out var p) && p > 0 ? p : 6;
var limit = int.TryParse(Get("--limit"), out var l) && l > 0 ? l : int.MaxValue;
var timeout = TimeSpan.FromSeconds(int.TryParse(Get("--timeout-s"), out var t) && t > 0 ? t : 180);
var filter = Get("--filter");
var options = new GpProbeOptions(RoundTrip: !args.Contains("--no-roundtrip"),
    TempDirectory: Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dc-tab-corpus")).FullName);

string[] extensions = [".gp", ".gpx", ".gp3", ".gp4", ".gp5"];
var files = Directory.EnumerateFiles(tabs, "*", SearchOption.AllDirectories)
    .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
    .Where(f => filter == null || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal)
    .Take(limit)
    .ToList();
Directory.CreateDirectory(output);
var details = Directory.CreateDirectory(Path.Combine(output, "details")).FullName;
Console.WriteLine($"tab-corpus: {files.Count} files, {parallel} in parallel, round trip {(options.RoundTrip ? "on" : "off")}");

var reports = new ConcurrentDictionary<int, GpProbeReport>();
var done = 0;
var clock = Stopwatch.StartNew();
Parallel.ForEach(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
{
    var path = files[i];
    var work = Task.Run(() => GpImportHealthProbe.Run(path, options));
    var report = work.Wait(timeout)
        ? work.Result
        : new GpProbeReport { Path = path, Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), ErrorClass = "timeout", ElapsedMs = (long)timeout.TotalMilliseconds };
    reports[i] = report;
    var n = Interlocked.Increment(ref done);
    if (n % 100 == 0) Console.WriteLine($"  {n}/{files.Count} ({clock.Elapsed.TotalMinutes:0.0} min)");
});

var ordered = Enumerable.Range(0, files.Count).Select(i => reports[i]).ToList();
var relative = (string path) => Path.GetRelativePath(tabs, path);
Report.WriteCsv(output, ordered, relative);
Report.WriteDetails(details, ordered, relative);
var summary = Report.Summary(ordered, relative, clock.Elapsed);
File.WriteAllText(Path.Combine(output, "summary.md"), summary, new UTF8Encoding(false));
Console.WriteLine(summary.Split("## Worst first")[0]);
Console.WriteLine($"tab-corpus: wrote {output}");

internal static class Report
{
    public static double Score(GpProbeReport r)
    {
        if (!r.Ok) return 1000;
        double score = r.Warnings.Count;
        foreach (var a in r.Arrangements)
        {
            if (a.Sounds == 0) continue;
            score += 100.0 * (a.BadString + a.FretOver24 + a.BelowCapo + a.OffGrid) / a.Sounds;
            if (a.RoundTripMeasures > 0) score += 100.0 * (a.RoundTripMeasures - a.RoundTripMeasuresEqual) / a.RoundTripMeasures;
            if (a.RoundTrip.StartsWith("error", StringComparison.Ordinal)) score += 50;
            if (a.GpRepeatPairs > 0) score += 100.0 * (a.GpRepeatPairs - a.GpRepeatPairsEqual) / a.GpRepeatPairs;
            foreach (var (technique, authored) in a.SourceTechniques)
            {
                if (technique == "notes" || authored == 0) continue;
                var lost = authored - a.ImportedTechniques.GetValueOrDefault(technique);
                if (lost > 0) score += 20.0 * lost / authored;
            }
        }

        return score;
    }

    public static void WriteCsv(string output, IReadOnlyList<GpProbeReport> reports, Func<string, string> relative)
    {
        var files = new StringBuilder("file,format,ok,error_class,ms,tracks,arrangements,measures,source_bars,repeat_played_measures,cross_groups,truncated,score,warnings\n");
        var parts = new StringBuilder("file,track,name,type,bass,strings,tuning,capo,link_class,notes,chords,sounds,bad_string,fret_over_24,below_capo,off_grid,measures,content_measures,repeated_measures,repeated_beyond_gp,repeat_groups,shared_measures,shared_same_position,gp_repeat_pairs,gp_repeat_pairs_equal,gp_repeat_pairs_grouped,round_trip,rt_measures,rt_measures_equal,rt_sounds_after");
        foreach (var technique in GpTechniqueCensus.Techniques) parts.Append(CultureInfo.InvariantCulture, $",src_{technique},imp_{technique}");
        parts.Append('\n');

        foreach (var r in reports)
        {
            files.AppendLine(string.Join(",", Csv(relative(r.Path)), r.Format, r.Ok, Csv(r.ErrorClass ?? ""), r.ElapsedMs, r.Tracks,
                r.Arrangements.Count, r.Measures, r.SourceBars, r.RepeatPlayedMeasures, r.CrossGroups, r.Truncated,
                Score(r).ToString("0.0", CultureInfo.InvariantCulture), Csv(string.Join(" ", r.Warnings))));
            foreach (var a in r.Arrangements)
            {
                parts.Append(string.Join(",", Csv(relative(r.Path)), a.Track, Csv(a.Name), a.Type, a.IsBass, a.StringCount, Csv(a.Tuning),
                    a.Capo, Csv(a.LinkClass ?? ""), a.Notes, a.Chords, a.Sounds, a.BadString, a.FretOver24, a.BelowCapo, a.OffGrid,
                    a.Measures, a.ContentMeasures, a.RepeatedMeasures, a.RepeatedBeyondGp, a.RepeatGroups, a.SharedMeasures,
                    a.SharedSamePosition, a.GpRepeatPairs, a.GpRepeatPairsEqual, a.GpRepeatPairsGrouped, a.RoundTrip,
                    a.RoundTripMeasures, a.RoundTripMeasuresEqual, a.RoundTripSoundsAfter));
                foreach (var technique in GpTechniqueCensus.Techniques)
                {
                    var authored = a.SourceTechniques.TryGetValue(technique, out var s) ? s.ToString(CultureInfo.InvariantCulture) : "";
                    parts.Append(CultureInfo.InvariantCulture, $",{authored},{a.ImportedTechniques.GetValueOrDefault(technique)}");
                }

                parts.Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(output, "files.csv"), files.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "arrangements.csv"), parts.ToString(), new UTF8Encoding(false));
    }

    public static void WriteDetails(string directory, IReadOnlyList<GpProbeReport> reports, Func<string, string> relative)
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        foreach (var r in reports)
        {
            var name = relative(r.Path);
            var id = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(name)))[..10];
            var safe = new string(Path.GetFileNameWithoutExtension(name).Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(40).ToArray());
            File.WriteAllText(Path.Combine(directory, $"{safe}-{id}.json"), JsonSerializer.Serialize(new { File = name, Score = Score(r), Report = r }, json));
        }
    }

    public static string Summary(IReadOnlyList<GpProbeReport> reports, Func<string, string> relative, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        string Pct(long part, long whole) => whole == 0 ? "-" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        var ok = reports.Where(r => r.Ok).ToList();
        var parts = ok.SelectMany(r => r.Arrangements).ToList();
        var sounding = parts.Where(a => a.Sounds > 0).ToList();

        sb.AppendLine("# Tab corpus report").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{reports.Count} files in {elapsed.TotalMinutes:0.0} min. Aggregates only above the worst-first list; the list and `details/` name files and stay local.").AppendLine();

        sb.AppendLine("## Import").AppendLine();
        sb.AppendLine("| format | files | ok | failed |").AppendLine("|---|---|---|---|");
        foreach (var g in reports.GroupBy(r => r.Format).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | {g.Count(r => r.Ok)} | {g.Count(r => !r.Ok)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| all | {reports.Count} | {ok.Count} | {reports.Count - ok.Count} |").AppendLine();

        sb.AppendLine("Failure classes:").AppendLine();
        foreach (var g in reports.Where(r => !r.Ok).GroupBy(r => r.ErrorClass ?? "?").OrderByDescending(g => g.Count()).Take(15))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{g.Key}` × {g.Count()}");
        sb.AppendLine().AppendLine("Warnings (files carrying each):").AppendLine();
        foreach (var g in ok.SelectMany(r => r.Warnings).GroupBy(w => w).OrderByDescending(g => g.Count()))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{g.Key}` × {g.Count()}");

        sb.AppendLine().AppendLine("## Tracks").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{parts.Count} tracks imported, {sounding.Count} with sound. By arrangement type (with sound):").AppendLine();
        foreach (var g in sounding.GroupBy(a => (a.Type, a.IsBass)).OrderByDescending(g => g.Count()))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {g.Key.Type}{(g.Key.IsBass && g.Key.Type.ToString() != "Bass" ? " (bass by tuning)" : "")}: {g.Count()}");
        sb.AppendLine().AppendLine("String counts: " + string.Join(", ", sounding.GroupBy(a => a.StringCount).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}")));
        sb.AppendLine(CultureInfo.InvariantCulture, $"With a capo: {sounding.Count(a => a.Capo > 0)} ({Pct(sounding.Count(a => a.Capo > 0), sounding.Count)}).");
        sb.AppendLine("Top tunings (semitone offsets per string): " + string.Join("; ", sounding.GroupBy(a => a.Tuning).OrderByDescending(g => g.Count()).Take(8).Select(g => $"[{g.Key}] {g.Count()}")));
        var totalSounds = sounding.Sum(a => (long)a.Sounds);
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture,
            $"Sounds: {totalSounds}. Out of range: bad string {sounding.Sum(a => a.BadString)}, fret > 24 {sounding.Sum(a => a.FretOver24)}, below the capo {sounding.Sum(a => a.BelowCapo)}, off the grid {sounding.Sum(a => a.OffGrid)} ({Pct(sounding.Sum(a => (long)a.BadString + a.FretOver24 + a.BelowCapo + a.OffGrid), totalSounds)}).");

        var census = sounding.Where(a => a.SourceTechniques.Count > 0).ToList();
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## Techniques (gpif files, {census.Count} tracks; repeats unfolded on both sides)").AppendLine();
        sb.AppendLine("| technique | authored | imported | imported / authored | tracks losing some |").AppendLine("|---|---|---|---|---|");
        foreach (var technique in GpTechniqueCensus.Techniques)
        {
            var authored = census.Sum(a => (long)a.SourceTechniques.GetValueOrDefault(technique));
            var importedCount = census.Sum(a => (long)a.ImportedTechniques.GetValueOrDefault(technique));
            var losing = census.Count(a => a.ImportedTechniques.GetValueOrDefault(technique) < a.SourceTechniques.GetValueOrDefault(technique));
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {technique} | {authored} | {importedCount} | {Pct(importedCount, authored)} | {losing} |");
        }

        var tried = sounding.Where(a => a.RoundTrip != "skipped").ToList();
        sb.AppendLine().AppendLine("## Guitar Pro round trip (import → GP export → re-import, sounds only)").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{tried.Count} tracks tried, {tried.Count(a => a.RoundTrip == "ok")} ok. Measures equal: {tried.Sum(a => (long)a.RoundTripMeasuresEqual)} of {tried.Sum(a => (long)a.RoundTripMeasures)} ({Pct(tried.Sum(a => (long)a.RoundTripMeasuresEqual), tried.Sum(a => (long)a.RoundTripMeasures))}). Tracks fully equal: {tried.Count(a => a.RoundTrip == "ok" && a.RoundTripMeasures == a.RoundTripMeasuresEqual)}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Sounds before / after: {tried.Sum(a => (long)a.Sounds)} / {tried.Sum(a => (long)a.RoundTripSoundsAfter)}.");
        foreach (var g in tried.Where(a => a.RoundTrip != "ok").GroupBy(a => a.RoundTrip).OrderByDescending(g => g.Count()))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{g.Key}` × {g.Count()}");

        // Why the unequal measures differ (GpRoundTripDiff): a measure counts once per class it
        // carries; "only" is the share where that class is the sole difference.
        var diffClasses = tried.SelectMany(a => a.RoundTripDiffs.Keys).Where(k => !k.StartsWith("only:", StringComparison.Ordinal))
            .Distinct().ToList();
        if (diffClasses.Count > 0)
        {
            sb.AppendLine().AppendLine("| difference | unequal measures with it | only difference |").AppendLine("|---|---|---|");
            foreach (var c in diffClasses.OrderByDescending(c => tried.Sum(a => (long)a.RoundTripDiffs.GetValueOrDefault(c))))
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {c} | {tried.Sum(a => (long)a.RoundTripDiffs.GetValueOrDefault(c))} | {tried.Sum(a => (long)a.RoundTripDiffs.GetValueOrDefault("only:" + c))} |");
            }
        }

        var linkable = sounding.Where(a => a.LinkClass != null).ToList();
        var content = linkable.Sum(a => (long)a.ContentMeasures);
        sb.AppendLine().AppendLine("## Linked measures (hardest level, content measures only)").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Within an arrangement: {linkable.Sum(a => (long)a.RepeatedMeasures)} of {content} content measures repeat ({Pct(linkable.Sum(a => (long)a.RepeatedMeasures), content)}), in {linkable.Sum(a => (long)a.RepeatGroups)} groups; {Pct(linkable.Sum(a => (long)a.RepeatedBeyondGp), content)} are bars GP plays once (written-out repetition).");
        var classed = ok.SelectMany(r => r.Arrangements.Where(a => a.Sounds > 0 && a.LinkClass != null)
            .GroupBy(a => a.LinkClass).Where(g => g.Count() > 1).SelectMany(g => g)).ToList();
        var classedContent = classed.Sum(a => (long)a.ContentMeasures);
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Across arrangements: {classed.Count} tracks have a compatible partner (same instrument, strings, tuning, capo). {classed.Sum(a => (long)a.SharedMeasures)} of their {classedContent} content measures are in a shared group ({Pct(classed.Sum(a => (long)a.SharedMeasures), classedContent)}); {Pct(classed.Sum(a => (long)a.SharedSamePosition), classedContent)} equal the same measure of a partner. {ok.Sum(r => (long)r.CrossGroups)} cross groups; files with any: {ok.Count(r => r.CrossGroups > 0)}.");
        var pairs = linkable.Sum(a => (long)a.GpRepeatPairs);
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Against GP's repeats: {pairs} pairs of measures play one written bar; {Pct(linkable.Sum(a => (long)a.GpRepeatPairsEqual), pairs)} have equal keys, {Pct(linkable.Sum(a => (long)a.GpRepeatPairsGrouped), pairs)} were put in one group. Files using repeats: {ok.Count(r => r.RepeatPlayedMeasures > 0)}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Detector truncated (degenerate part): {ok.Count(r => r.Truncated)} files.");

        sb.AppendLine().AppendLine("## Worst first (local only)").AppendLine();
        foreach (var r in reports.OrderByDescending(Score).ThenBy(r => r.Path, StringComparer.Ordinal).Take(60))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {Score(r):0.0} `{relative(r.Path)}` {(r.Ok ? string.Join(" ", r.Warnings) : r.ErrorClass + ": " + r.ErrorMessage)}");
        return sb.ToString();
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
