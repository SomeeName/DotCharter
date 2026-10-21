// .NET 10 file-based program: compares the Guitar Pro import of a tab with the owner's FINISHED
// chart of the same song (#91, level 2a). Deterministic: no audio, no model. Read-only on every
// source; writes only to --out, which must be outside the repository.
//
// Usage, outputs and the rules for the private sources: tools/tab-vs-chart/README.md.
#:project ../../DotCharter.Services/DotCharter.Services.csproj
#:property PublishAot=false
#:property Nullable=enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Project;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Import.GuitarPro.Report.AnswerKey;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Parsing.GuitarPro;

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
var csvPath = Get("--csv") ?? Environment.GetEnvironmentVariable("DOTCHARTER_CDLC_CSV");
var tabs = Get("--tabs") ?? Environment.GetEnvironmentVariable("DOTCHARTER_TABS_DIR");
var charts = Get("--charts") ?? Environment.GetEnvironmentVariable("DOTCHARTER_CHART_PROJECTS_DIR");
var overridesPath = Get("--overrides") ?? Environment.GetEnvironmentVariable("DOTCHARTER_TAB_CHART_OVERRIDES");
var output = Get("--out") ?? Environment.GetEnvironmentVariable("DOTCHARTER_LEVEL2_REPORT_DIR");
if (new[] { csvPath, tabs, charts, output }.Any(string.IsNullOrWhiteSpace) || !File.Exists(csvPath) || !Directory.Exists(tabs) || !Directory.Exists(charts))
{
    Console.Error.WriteLine("usage: dotnet run tools/tab-vs-chart/TabVsChart.cs -- --csv <cdlc.csv> --tabs <dir> --charts <dir> --out <dir>");
    Console.Error.WriteLine("       [--overrides <tsv>] [--since 2024-07-20] [--focus \"artist;artist\"] [--filter <substr>] [--match-only] [--parallel N]");
    Console.Error.WriteLine("       Defaults: DOTCHARTER_CDLC_CSV, DOTCHARTER_TABS_DIR, DOTCHARTER_CHART_PROJECTS_DIR, DOTCHARTER_TAB_CHART_OVERRIDES, DOTCHARTER_LEVEL2_REPORT_DIR.");
    Environment.Exit(2);
}

var since = DateTime.SpecifyKind(DateTime.Parse(Get("--since") ?? "2024-07-20", CultureInfo.InvariantCulture), DateTimeKind.Utc);
var focus = (Get("--focus") ?? Environment.GetEnvironmentVariable("DOTCHARTER_LEVEL2_FOCUS") ?? "")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var filter = Get("--filter");
var parallel = int.TryParse(Get("--parallel"), out var p) && p > 0 ? p : 4;
var clock = Stopwatch.StartNew();

// ---- 1. Matching ---------------------------------------------------------------------------
var rows = TabChartMatching.ReadCdlcCsv(File.ReadAllText(csvPath!));
var knownGood = rows.Where(r => r.IsKnownGood(since)).ToList();
var overrides = overridesPath != null && File.Exists(overridesPath)
    ? TabChartMatching.ReadOverrides(File.ReadAllText(overridesPath))
    : [];
var folders = Directory.GetDirectories(charts!).Select(Path.GetFileName).OfType<string>().ToList();
string[] tabExtensions = [".gp", ".gpx", ".gp5", ".gp4", ".gp3"];
var tabFiles = Directory.EnumerateFiles(tabs!, "*", SearchOption.AllDirectories)
    .Where(f => tabExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
    .Select(f => Path.GetRelativePath(tabs!, f))
    .Order(StringComparer.Ordinal)
    .ToList();

var matches = new List<SongMatch>();
foreach (var row in knownGood)
{
    overrides.TryGetValue(row.Id, out var manual);
    if (manual?.Skip == true)
    {
        matches.Add(new SongMatch(row, null, null, null, "skipped (override)"));
        continue;
    }

    var folder = manual?.ChartFolder != null
        ? new MatchCandidate(manual.ChartFolder, 1, TabChartMatching.SplitName(manual.ChartFolder).Artist)
        : TabChartMatching.BestChartFolder(row.Title, folders);
    var tab = manual?.Tab != null
        ? new MatchCandidate(manual.Tab, 1, null)
        : TabChartMatching.BestTab(row.Title, folder?.Artist, tabFiles);
    var why = folder == null ? "no chart folder" : tab == null ? "no tab" : "";
    matches.Add(new SongMatch(row, folder, tab, folder?.Artist, why));
}

Directory.CreateDirectory(output!);
Report.WriteMatching(output!, rows.Count, knownGood.Count, matches);
var matched = matches.Where(m => m.Why.Length == 0).ToList();
Console.WriteLine($"tab-vs-chart: {rows.Count} rows, {knownGood.Count} known-good, {matched.Count} matched to a tab and a chart folder");
if (args.Contains("--match-only")) return;

// Focus artists first, then the rest; a filter narrows by title or artist.
matched = [.. matched
    .Where(m => filter == null || $"{m.Artist} {m.Row.Title}".Contains(filter, StringComparison.OrdinalIgnoreCase))
    .OrderBy(m => IsFocus(m) ? 0 : 1)
    .ThenBy(m => m.Artist, StringComparer.OrdinalIgnoreCase)
    .ThenBy(m => m.Row.Title, StringComparer.OrdinalIgnoreCase)];

bool IsFocus(SongMatch m) => focus.Any(f => (m.Artist ?? "").Contains(f, StringComparison.OrdinalIgnoreCase));

// ---- 2-3. Import, align, compare ------------------------------------------------------------
var songsDir = Directory.CreateDirectory(Path.Combine(output!, "songs")).FullName;
var results = new ConcurrentDictionary<int, SongResult>();
Parallel.ForEach(Enumerable.Range(0, matched.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
{
    var m = matched[i];
    var result = Compare.Song(m, tabs!, charts!, IsFocus(m));
    results[i] = result;
    Report.WriteSong(songsDir, result);
    Console.WriteLine($"  [{results.Count}/{matched.Count}] {result.Slug}: {result.Error ?? $"{result.Arrangements.Count} arrangements, exact F1 {result.Score:0.000}"}");
});

var ordered = Enumerable.Range(0, matched.Count).Select(i => results[i]).ToList();
var summary = Report.Summary(ordered, rows.Count, knownGood.Count, matches, clock.Elapsed);
File.WriteAllText(Path.Combine(output!, "summary.md"), summary, new UTF8Encoding(false));
Report.WriteCsv(output!, ordered);
Console.WriteLine(summary.Split("## Worst first")[0]);
Console.WriteLine($"tab-vs-chart: wrote {output}");

internal sealed record SongMatch(CdlcRow Row, MatchCandidate? Folder, MatchCandidate? Tab, string? Artist, string Why);

internal sealed class SongResult
{
    public string Slug { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public bool Focus { get; set; }
    public string Tab { get; set; } = "";
    public string ChartFolder { get; set; } = "";
    public string? Error { get; set; }
    public List<string> SkippedCharts { get; set; } = [];
    public List<ArrangementComparison> Arrangements { get; set; } = [];

    /// <summary>Mean exact (string + fret) F1 over the arrangements; 0 when nothing compared.</summary>
    public double Score => Arrangements.Count == 0 ? 0 : Arrangements.Average(a => a.Counts.ExactF1);
}

internal static class Compare
{
    private static readonly AppConfig Config = new();

    public static SongResult Song(SongMatch m, string tabs, string charts, bool focus)
    {
        var slug = Slug($"{m.Artist} - {m.Row.Title}") + "-" + m.Row.Id;
        var result = new SongResult
        {
            Slug = slug, Id = m.Row.Id, Title = m.Row.Title, Artist = m.Artist ?? "", Focus = focus,
            Tab = m.Tab!.Path, ChartFolder = m.Folder!.Path
        };
        try
        {
            var moved = new Dictionary<string, int>();
            var chartParts = LoadCharts(Path.Combine(charts, m.Folder.Path), result.SkippedCharts, moved);
            if (chartParts.Count == 0)
            {
                result.Error = "no single-level RS2 XML in the chart folder";
                return result;
            }

            var importer = new GpImporter();
            var path = Path.Combine(tabs, m.Tab.Path);
            var tracks = importer.GetAvailableTracks(path);
            var imported = importer.ImportTracksWithMetadata(path, [.. tracks.Select(t => t.Id)]);
            var beats = GpBeatMapBuilder.Build(imported.Metadata, GpBeatMapBuilder.InitialBpm(imported.Metadata, 120), 0);
            foreach (var arrangement in imported.Arrangements)
                ChartSpacingEnforcer.EnforceArrangement(arrangement, beats, Config);

            result.Arrangements = TabChartComparer.CompareSong(imported.Arrangements, beats, chartParts);
            foreach (var a in result.Arrangements)
                a.MoveRPhrases = moved.GetValueOrDefault(a.ChartFile);
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }

        return result;
    }

    /// <summary>The folder's arrangement XMLs, single-level, each distinct part once.</summary>
    private static List<(string File, Arrangement Part, IList<BeatMarker> Beats)> LoadCharts(string folder, List<string> skipped,
        Dictionary<string, int> moved)
    {
        var parts = new List<(string File, Arrangement Part, IList<BeatMarker> Beats, DateTime Written)>();
        var seen = new HashSet<string>();
        // The folder's own XMLs first, then one level of subfolders (an export folder), never backups.
        var files = Directory.GetFiles(folder, "*.xml").OrderByDescending(File.GetLastWriteTimeUtc)
            .Concat(Directory.GetDirectories(folder)
                .Where(d => !Path.GetFileName(d).Contains("backup", StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => Directory.GetFiles(d, "*.xml").OrderByDescending(File.GetLastWriteTimeUtc)));
        foreach (var xml in files)
        {
            var name = Path.GetFileName(xml).ToLowerInvariant();
            if (new[] { "debug", "showlights", "tone2014", "vocals" }.Any(name.Contains) || !IsSong(xml)) continue;

            ChartProject project;
            int moveR;
            try { (project, moveR) = TabChartSections.LoadChart(xml); }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(xml)}: unreadable ({ex.GetType().Name})");
                continue;
            }

            var part = project.Arrangements.FirstOrDefault();
            if (part == null || part.Levels.Count == 0 || part.ArrangementType == ArrangementType.Vocal) continue;
            if (part.Levels.Count > 1)
            {
                skipped.Add($"{Path.GetFileName(xml)}: {part.Levels.Count} levels (DD)");
                continue;
            }

            if (!seen.Add(Signature(part.Levels[0]))) continue;
            moved[Path.GetRelativePath(folder, xml)] = moveR;
            parts.Add((Path.GetRelativePath(folder, xml), part, project.Beats, File.GetLastWriteTimeUtc(xml)));
        }

        return [.. parts.Select(x => (x.File, x.Part, x.Beats))];
    }

    private static bool IsSong(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            return reader.MoveToContent() == XmlNodeType.Element && reader.Name == "song";
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string Signature(ChartLevel level)
    {
        var sb = new StringBuilder();
        sb.Append(level.Notes.Count).Append('/').Append(level.Chords.Count).Append('/');
        foreach (var n in level.Notes.OrderBy(n => n.Position).Take(40)) sb.Append(n.StringIndex).Append(n.Fret).Append(';');
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var slug = sb.ToString().Trim('_');
        return slug.Length > 80 ? slug[..80] : slug;
    }
}

internal static class Report
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly string[] Roles = ["Lead", "Rhythm", "Combo", "Bass"];

    public static void WriteMatching(string output, int rows, int knownGood, List<SongMatch> matches)
    {
        var sb = new StringBuilder("id,title,artist,status,chart_folder,folder_score,tab,tab_score\n");
        foreach (var m in matches.OrderBy(m => m.Why).ThenBy(m => m.Row.Title, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(string.Join(",", m.Row.Id, Csv(m.Row.Title), Csv(m.Artist ?? ""), m.Why.Length == 0 ? "matched" : Csv(m.Why),
                Csv(m.Folder?.Path ?? ""), m.Folder?.Score.ToString("0.00") ?? "", Csv(m.Tab?.Path ?? ""), m.Tab?.Score.ToString("0.00") ?? ""));
        }

        File.WriteAllText(Path.Combine(output, "matching.csv"), sb.ToString(), new UTF8Encoding(false));
    }

    public static void WriteSong(string directory, SongResult song)
    {
        File.WriteAllText(Path.Combine(directory, song.Slug + ".json"), JsonSerializer.Serialize(song, Json), new UTF8Encoding(false));

        var sb = new StringBuilder();
        sb.AppendLine($"# {song.Artist} - {song.Title} (CDLC {song.Id})").AppendLine();
        sb.AppendLine($"- tab: `{song.Tab}`").AppendLine($"- chart folder: `{song.ChartFolder}`");
        if (song.Error != null) sb.AppendLine($"- **error:** {song.Error}");
        foreach (var s in song.SkippedCharts) sb.AppendLine($"- skipped chart: {s}");
        sb.AppendLine();
        if (song.Arrangements.Count > 0)
        {
            sb.AppendLine("| role | chart file | tab track | align | offset | at offset | measures tab/chart/aligned | onset F1 | exact F1 | pitch F1 | chords shape | FHP | fingering | links P/R | tuning |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var a in song.Arrangements)
            {
                var c = a.Counts;
                sb.AppendLine($"| {a.Role} | {a.ChartFile} | {a.TabTrack} {a.TabTrackName} | {a.AlignmentMethod} {a.MeanSimilarity:0.00} | {a.FirstNoteOffset} | {Pct(a.AtFirstNoteOffset)} | " +
                    $"{a.TabMeasures}/{a.ChartMeasures}/{a.AlignedMeasures} | {c.OnsetF1:0.000} | {c.ExactF1:0.000} | {c.PitchF1:0.000} | " +
                    $"{Pct(c.ChordShapeSame, c.ChordOnsets)} | {Pct(c.FhpSame, c.FhpSounds)} | {Pct(c.FingeringSame, c.FingeredChords)} | " +
                    $"{Pct(c.LinkPairsChartEqual, c.LinkPairs)} / {Pct(c.ChartRepeatPairsLinked, c.ChartRepeatPairs)} | {(a.TuningDiffers ? $"tab {a.TabTuning} c{a.TabCapo} / chart {a.ChartTuning} c{a.ChartCapo}" : "same")} |");
            }

            foreach (var a in song.Arrangements)
            {
                sb.AppendLine().AppendLine($"## {a.Role} ({a.ChartFile})").AppendLine();
                sb.AppendLine("Difference classes:").AppendLine();
                foreach (var g in a.Differences.GroupBy(d => d.Class).OrderByDescending(g => g.Count()))
                    sb.AppendLine($"- {g.Key}: {g.Count()}");
                sb.AppendLine().AppendLine("Worst measures (tab → chart, exact F1, sounds tab/chart):").AppendLine();
                foreach (var w in a.Measures.Where(x => x.TabSounds + x.ChartSounds > 0).OrderBy(x => x.ExactF1).ThenBy(x => x.TabMeasure).Take(12))
                    sb.AppendLine($"- {Measure(w.TabMeasure)} → {Measure(w.ChartMeasure)} [{w.Section ?? "-"}]: {w.ExactF1:0.00} ({w.TabSounds}/{w.ChartSounds}), {w.Differences} differences");
            }
        }

        File.WriteAllText(Path.Combine(directory, song.Slug + ".md"), sb.ToString(), new UTF8Encoding(false));
    }

    public static string Summary(List<SongResult> songs, int rows, int knownGood, List<SongMatch> matches, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        var compared = songs.Where(s => s.Arrangements.Count > 0).ToList();
        sb.AppendLine("# Tab import vs finished chart (level 2a)").AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} in {elapsed.TotalMinutes:0.0} min. Local only: never commit or paste song names.").AppendLine();
        sb.AppendLine("## Matching").AppendLine();
        sb.AppendLine($"- CSV rows: {rows}; known-good: {knownGood}");
        foreach (var g in matches.GroupBy(m => m.Why.Length == 0 ? "matched" : m.Why).OrderBy(g => g.Key))
            sb.AppendLine($"- {g.Key}: {g.Count()}");
        sb.AppendLine($"- compared (at least one arrangement): {compared.Count}; failed: {songs.Count(s => s.Error != null)}");
        foreach (var s in songs.Where(s => s.Error != null))
            sb.AppendLine($"  - {s.Slug}: {s.Error}");
        sb.AppendLine().AppendLine("Unmatched (add them to the override file):").AppendLine();
        foreach (var m in matches.Where(m => m.Why.Length > 0).OrderBy(m => m.Why).ThenBy(m => m.Row.Title, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"- {m.Row.Id} {m.Row.Title} ({m.Why}{(m.Artist != null ? ", folder artist " + m.Artist : "")})");

        sb.AppendLine().AppendLine("## Aggregate agreement").AppendLine();
        Aggregate(sb, "focus artists", compared.Where(s => s.Focus).ToList());
        Aggregate(sb, "all compared songs", compared);

        sb.AppendLine("## Techniques (all songs; matched sounds; Jaccard both / both+tab-only+chart-only)").AppendLine();
        sb.AppendLine("| technique | role | both | import only | chart only | agreement |").AppendLine("|---|---|---|---|---|---|");
        foreach (var role in Roles)
        {
            var total = Sum(compared.SelectMany(s => s.Arrangements).Where(a => a.Role == role));
            foreach (var (name, t) in total.Techniques.OrderBy(kv => kv.Key))
                sb.AppendLine($"| {name} | {role} | {t.Both} | {t.TabOnly} | {t.ChartOnly} | {Pct(t.Agreement ?? 0)} |");
        }

        sb.AppendLine().AppendLine("## Difference classes").AppendLine();
        sb.AppendLine("| class | " + string.Join(" | ", Roles) + " | all | songs |").AppendLine("|---|" + string.Concat(Roles.Select(_ => "---|")) + "---|---|");
        var all = compared.SelectMany(s => s.Arrangements.SelectMany(a => a.Differences.Select(d => (s.Slug, a.Role, d.Class)))).ToList();
        foreach (var g in all.GroupBy(x => x.Class).OrderByDescending(g => g.Count()))
            sb.AppendLine($"| {g.Key} | " + string.Join(" | ", Roles.Select(r => g.Count(x => x.Role == r))) + $" | {g.Count()} | {g.Select(x => x.Slug).Distinct().Count()} |");

        sb.AppendLine().AppendLine("## Alignment").AppendLine();
        var parts = compared.SelectMany(s => s.Arrangements).ToList();
        foreach (var g in parts.GroupBy(a => a.AlignmentMethod))
            sb.AppendLine($"- {g.Key}: {g.Count()} arrangements, mean similarity {g.Average(a => a.MeanSimilarity):0.00}, pairs at the first-note offset {Pct(g.Average(a => a.AtFirstNoteOffset))}");
        sb.AppendLine($"- tuning or capo differs from the chart: {parts.Count(a => a.TuningDiffers)} of {parts.Count}");
        var moveR = compared.Where(s => s.Arrangements.Any(a => a.MoveRPhrases > 0)).ToList();
        sb.AppendLine($"- answer-key songs using EOF moveR# phrases: {moveR.Count} of {compared.Count} ({moveR.Sum(s => s.Arrangements.Sum(a => a.MoveRPhrases))} phrase iterations, moved to the Nth note by the importer's RsXmlPhraseMover)");

        sb.AppendLine().AppendLine("## Worst first").AppendLine();
        sb.AppendLine("| # | song | focus | arrangements | mean exact F1 | worst role | detail |").AppendLine("|---|---|---|---|---|---|---|");
        var rank = 0;
        foreach (var s in songs.OrderBy(s => s.Error == null ? 1 : 0).ThenBy(s => s.Score))
        {
            var worst = s.Arrangements.MinBy(a => a.Counts.ExactF1);
            sb.AppendLine($"| {++rank} | {s.Artist} - {s.Title} | {(s.Focus ? "yes" : "")} | {s.Arrangements.Count} | {s.Score:0.000} | " +
                $"{(worst == null ? s.Error : $"{worst.Role} {worst.Counts.ExactF1:0.000}")} | songs/{s.Slug}.md |");
        }

        return sb.ToString();
    }

    public static void WriteCsv(string output, List<SongResult> songs)
    {
        var sb = new StringBuilder("slug,id,focus,role,chart_file,tab_track,align,offset,at_offset,mean_sim,tab_measures,chart_measures,aligned,"
            + "tab_onsets,chart_onsets,matched_onsets,tab_sounds,chart_sounds,exact,pitch,chord_onsets,chord_shape_same,chord_named,chord_name_same,"
            + "fhp_sounds,fhp_same,fingered_chords,fingering_same,link_pairs,link_pairs_chart_equal,chart_repeat_pairs,chart_repeat_pairs_linked,tuning_differs\n");
        foreach (var s in songs)
        {
            foreach (var a in s.Arrangements)
            {
                var c = a.Counts;
                sb.AppendLine(string.Join(",", Csv(s.Slug), s.Id, s.Focus, a.Role, Csv(a.ChartFile), a.TabTrack, a.AlignmentMethod, a.FirstNoteOffset,
                    a.AtFirstNoteOffset.ToString("0.000"), a.MeanSimilarity.ToString("0.000"), a.TabMeasures, a.ChartMeasures, a.AlignedMeasures,
                    c.TabOnsets, c.ChartOnsets, c.MatchedOnsets, c.TabSounds, c.ChartSounds, c.ExactSounds, c.PitchSounds, c.ChordOnsets,
                    c.ChordShapeSame, c.ChordNamedBoth, c.ChordNameSame, c.FhpSounds, c.FhpSame, c.FingeredChords, c.FingeringSame,
                    c.LinkPairs, c.LinkPairsChartEqual, c.ChartRepeatPairs, c.ChartRepeatPairsLinked, a.TuningDiffers));
            }
        }

        File.WriteAllText(Path.Combine(output, "arrangements.csv"), sb.ToString(), new UTF8Encoding(false));
    }

    private static void Aggregate(StringBuilder sb, string title, List<SongResult> songs)
    {
        sb.AppendLine($"### {title} ({songs.Count} songs)").AppendLine();
        sb.AppendLine("| role | parts | onset F1 | exact F1 | pitch F1 | chord shape | chord name | FHP | fingering exact | fingering per string | link precision | repeat recall |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var role in Roles.Append("all"))
        {
            var parts = songs.SelectMany(s => s.Arrangements).Where(a => role == "all" || a.Role == role).ToList();
            if (parts.Count == 0) continue;

            var c = Sum(parts);
            sb.AppendLine($"| {role} | {parts.Count} | {c.OnsetF1:0.000} | {c.ExactF1:0.000} | {c.PitchF1:0.000} | {Pct(c.ChordShapeSame, c.ChordOnsets)} | " +
                $"{Pct(c.ChordNameSame, c.ChordNamedBoth)} | {Pct(c.FhpSame, c.FhpSounds)} | {Pct(c.FingeringSame, c.FingeredChords)} | " +
                $"{Pct(c.FingeredStringsSame, c.FingeredStrings)} | {Pct(c.LinkPairsChartEqual, c.LinkPairs)} | {Pct(c.ChartRepeatPairsLinked, c.ChartRepeatPairs)} |");
        }

        sb.AppendLine();
    }

    private static ComparisonCounts Sum(IEnumerable<ArrangementComparison> parts)
    {
        var total = new ComparisonCounts();
        foreach (var a in parts) total.Add(a.Counts);
        return total;
    }

    private static string Measure(int m) => m < 0 ? "-" : (m + 1).ToString(CultureInfo.InvariantCulture);

    private static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:0.0}% ({a}/{b})";

    private static string Pct(double share) => $"{100.0 * share:0.0}%";

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
