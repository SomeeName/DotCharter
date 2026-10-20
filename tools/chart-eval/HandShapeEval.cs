// .NET 10 file-based program: scores the hand-shape SUGGESTER (#491, HandShapeSuggester) against
// hand-finished charts. Every RS-XML in --corpus keeps its notes, chords and FHP anchors; its hand
// shapes are stripped, the suggester runs, and its proposals are compared with the charter's bars.
//
//   dotnet run tools/chart-eval/HandShapeEval.cs -- --corpus <corpus.json> [--split train|holdout|all]
//       [--kind guitar|bass|all] [--no-anchors] [--sweep] [--min-sounds n] [--min-strings n]
//       [--max-spread n] [--max-gap beats] [--fixture <out.json>] [--detail <out.tsv>]
//
// Scores (README.md § Hand-shape suggestions):
//   notes  - every single note: under a charter's bar? under a proposal? precision = TP / (TP + FP),
//            recall = TP / (TP + FN). The headline number.
//   bars   - a proposal MATCHES a charter's bar that covers a single note when the two cover the
//            same sounds by Jaccard >= 0.5; precision over proposals, recall over those bars.
#:project ../../DotCharter.Services/DotCharter.Services.csproj
#:property PublishAot=false
#:property Nullable=enable

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Parsing.Xml;

try
{
    using var self = Process.GetCurrentProcess();
    self.PriorityClass = ProcessPriorityClass.Idle;
    if (OperatingSystem.IsWindows() && Environment.ProcessorCount >= 16) self.ProcessorAffinity = (IntPtr)0xFF00;
}
catch (Exception) { /* best effort */ }

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string? Get(string k) { var i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Has(string k) => args.Contains(k);

var corpusPath = Get("--corpus");
if (corpusPath == null)
{
    Console.Error.WriteLine("usage: --corpus <corpus.json> [--split train|holdout|all] [--kind guitar|bass|all] [--no-anchors] [--sweep] [--min-sounds n] [--min-strings n] [--max-spread n] [--max-gap beats] [--fixture out.json] [--detail out.tsv]");
    return 2;
}

var split = Get("--split") ?? "train";
var kindFilter = Get("--kind") ?? "guitar";
var defaults = HandShapeSuggestOptions.Default;
var options = defaults with
{
    MinSounds = int.TryParse(Get("--min-sounds"), out var ms) ? ms : defaults.MinSounds,
    MinStrings = int.TryParse(Get("--min-strings"), out var mst) ? mst : defaults.MinStrings,
    MinStringsAlone = int.TryParse(Get("--min-strings-alone"), out var msa) ? msa : defaults.MinStringsAlone,
    MaxFretSpread = int.TryParse(Get("--max-spread"), out var sp) ? sp : defaults.MaxFretSpread,
    MaxGap = double.TryParse(Get("--max-gap"), CultureInfo.InvariantCulture, out var g) ? g : defaults.MaxGap,
};

var charts = new List<EvalChart>();
var seen = new HashSet<string>();
var corpus = JsonDocument.Parse(File.ReadAllText(corpusPath)).RootElement;
foreach (var song in corpus.EnumerateArray())
{
    var name = song.GetProperty("song").GetString()!;
    var holdout = IsHoldout(name);
    if (split == "train" && holdout || split == "holdout" && !holdout) continue;
    foreach (var a in song.GetProperty("arrangements").EnumerateArray())
    {
        var kind = a.GetProperty("kind").GetString()!;
        if (kindFilter != "all" && kind != kindFilter) continue;
        var path = a.GetProperty("xml").GetString()!;
        ChartProject project;
        try { project = new XmlChartSerializer().Load(path); }
        catch (Exception) { continue; }
        var arr = project.Arrangements.FirstOrDefault();
        if (arr == null || arr.Levels.Count != 1) continue;
        var level = arr.Levels[0];
        if (!seen.Add(name + "|" + Signature(level))) continue;
        var reference = level.HandShapes.ToList();
        level.HandShapes.Clear();
        // A template only the charter's BARS use is the answer key (the grip they minted for the
        // arpeggio): a chart the suggester meets has the chords' templates and nothing else.
        var chordTemplateIds = level.Chords.Select(ch => ch.TemplateId).ToHashSet();
        foreach (var t in arr.ChordTemplates.Where(t => !chordTemplateIds.Contains(t.Id)).ToList()) arr.ChordTemplates.Remove(t);
        if (Has("--no-anchors")) level.Anchors.Clear();
        charts.Add(new EvalChart(name, Path.GetFileName(path), kind, holdout, arr, level, reference));
    }
}

Console.WriteLine($"{charts.Count} arrangements ({split}, {kindFilter}{(Has("--no-anchors") ? ", anchors stripped" : "")})");

if (Has("--sweep"))
{
    foreach (var minSounds in new[] { 3, 4 })
    foreach (var (minStrings, alone) in new[] { (2, 4), (3, 3), (3, 4), (3, 5) })
    foreach (var spread in new[] { 2, 3 })
    foreach (var gap in new[] { 1.0, 2.0 })
    foreach (var oneChord in new[] { false, true })
    {
        var o = options with
        {
            MinSounds = minSounds, MinStrings = minStrings, MinStringsAlone = alone, MaxFretSpread = spread, MaxGap = gap,
            OneChordShape = oneChord,
        };
        var total = new Score();
        foreach (var c in charts) total.Add(Evaluate(c, o, null));
        Console.WriteLine($"sounds>={minSounds} strings>={minStrings}/{alone} spread<={spread} gap<={gap,-3} oneChord={(oneChord ? 1 : 0)} {total.Line()}");
    }

    return 0;
}

var detail = Get("--detail") is { } detailPath ? new StreamWriter(detailPath) : null;
detail?.WriteLine("song\tarrangement\twhat\tstart\tend\tsounds\tnotes\tlabel\tstrings\tspread\tdistinct\tmaxrepeat\tbeats\topen\tsustained\texisting\tmuted");
var all = new Score();
foreach (var c in charts)
{
    var s = Evaluate(c, options, detail);
    all.Add(s);
    Console.WriteLine($"{(c.Holdout ? "H" : " ")} {Trunc(c.Song, 40),-40} {Trunc(c.File, 24),-24} {s.Line()}");
}

detail?.Dispose();
Console.WriteLine();
Console.WriteLine($"options: sounds>={options.MinSounds} strings>={options.MinStrings}/{options.MinStringsAlone} spread<={options.MaxFretSpread} gap<={options.MaxGap}");
Console.WriteLine($"TOTAL {split} {kindFilter} {all.Line()}");
Console.WriteLine("missed notes under a charter's bar, by the first rule the bar's own sounds break:");
foreach (var (why, n) in all.Why.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {n,6}  {why}");

if (Get("--fixture") is { } fixturePath) Fixture.Write(fixturePath, charts.Where(c => !c.Holdout).ToList());
return 0;

static Score Evaluate(EvalChart c, HandShapeSuggestOptions options, StreamWriter? detail)
{
    var proposals = HandShapeSuggester.Suggest(c.Arrangement, c.Level, options);
    var score = new Score { Proposals = proposals.Count };

    foreach (var note in c.Level.Notes)
    {
        var inRef = c.Reference.Any(h => h.Covers(note.Position));
        var inSug = proposals.Any(p => p.Covers(note.Position));
        if (inRef && inSug) score.Tp++;
        else if (inSug) score.Fp++;
        else if (inRef) score.Fn++;
    }

    var positions = c.Level.Notes.Select(n => n.Position).Concat(c.Level.Chords.Select(ch => ch.Position))
        .Distinct().OrderBy(p => p).ToList();
    var notePositions = c.Level.Notes.Select(n => n.Position).ToHashSet();
    HashSet<FractionalPosition> Held(FractionalPosition start, FractionalPosition end) =>
        positions.Where(p => p.IsWithin(start, end)).ToHashSet();

    var targets = c.Reference.Select(h => Held(h.Position, h.EndPosition))
        .Where(set => set.Any(notePositions.Contains)).ToList();
    score.Targets = targets.Count;
    var matchedTargets = new HashSet<int>();
    foreach (var p in proposals)
    {
        var held = Held(p.Position, p.EndPosition);
        var matched = false;
        for (var t = 0; t < targets.Count; t++)
        {
            var inter = targets[t].Count(held.Contains);
            if (inter == 0) continue;
            var union = targets[t].Count + held.Count - inter;
            if (inter * 2 < union) continue;
            matched = true;
            matchedTargets.Add(t);
        }

        if (matched) score.MatchedProposals++;
        if (detail != null)
        {
            var inNotes = c.Level.Notes.Where(n => p.Covers(n.Position)).ToList();
            var label = inNotes.Count == 0 ? 0 : (double)inNotes.Count(n => c.Reference.Any(h => h.Covers(n.Position))) / inNotes.Count;
            var fretted = p.Frets.Values.Where(f => f > c.Arrangement.Capo).ToList();
            var spread = fretted.Count == 0 ? 0 : fretted.Max() - fretted.Min();
            var hits = inNotes.Select(n => n.StringIndex).Concat(c.Level.Chords.Where(ch => p.Covers(ch.Position)).SelectMany(ch => ch.ChordNotes.Select(cn => cn.StringIndex))).ToList();
            var maxRepeat = hits.Count == 0 ? 0 : hits.GroupBy(x => x).Max(gr => gr.Count());
            var len = p.EndPosition - p.Position;
            var beats = len.BeatId + len.Fraction.DoubleValue;
            var open = p.Frets.Values.Any(f => f <= c.Arrangement.Capo) ? 1 : 0;
            var sustained = inNotes.Count == 0 ? 0 : (double)inNotes.Count(n => n.Sustain > FractionalPosition.Zero) / inNotes.Count;
            var palm = inNotes.Count == 0 ? 0 : (double)inNotes.Count(n => n.Mute == MuteType.Palm) / inNotes.Count;
            detail.WriteLine($"{c.Song}\t{c.File}\t{(matched ? "match" : "extra")}\t{p.Position}\t{p.EndPosition}\t{p.SoundCount}\t{p.NoteCount}\t{label:0.00}\t{p.Frets.Count}\t{spread}\t{hits.Distinct().Count()}\t{maxRepeat}\t{beats:0.00}\t{open}\t{sustained:0.00}\t{(p.Existing != null ? 1 : 0)}\t{palm:0.00}");
        }
    }

    score.MatchedTargets = matchedTargets.Count;

    // Why a charter's bar over single notes was missed: the first rule its own sounds break.
    foreach (var h in c.Reference)
    {
        var notes = c.Level.Notes.Where(n => h.Covers(n.Position)).ToList();
        if (notes.Count == 0) continue;
        var missed = notes.Count(n => !proposals.Any(p => p.Covers(n.Position)));
        if (missed * 2 < notes.Count) continue;
        score.Why[Why(c, h, options)] = score.Why.GetValueOrDefault(Why(c, h, options)) + missed;
    }
    return score;
}

static string Why(EvalChart c, HandShape h, HandShapeSuggestOptions options)
{
    var grip = new Dictionary<int, int>();
    var conflict = false;
    var slides = false;
    var sounds = new SortedSet<FractionalPosition>();
    var chordStrikes = 0;
    foreach (var n in c.Level.Notes.Where(n => h.Covers(n.Position)))
    {
        sounds.Add(n.Position);
        if (n.SlideTo.HasValue || n.UnpitchedSlide) slides = true;
        if (n.Mute == MuteType.Full) continue;
        if (grip.TryGetValue(n.StringIndex, out var f) && f != n.Fret) conflict = true;
        grip[n.StringIndex] = n.Fret;
    }

    foreach (var ch in c.Level.Chords.Where(ch => h.Covers(ch.Position)))
    {
        sounds.Add(ch.Position);
        chordStrikes++;
        foreach (var cn in ch.ChordNotes)
        {
            if (cn.SlideTo.HasValue || cn.UnpitchedSlide) slides = true;
            if (cn.Mute == MuteType.Full) continue;
            if (grip.TryGetValue(cn.StringIndex, out var f) && f != cn.Fret) conflict = true;
            grip[cn.StringIndex] = cn.Fret;
        }
    }

    if (conflict) return "string at two frets";
    var fretted = grip.Values.Where(f => f > c.Arrangement.Capo).ToList();
    if (fretted.Count > 0 && fretted.Max() - fretted.Min() > options.MaxFretSpread) return "fret spread";
    if (sounds.Count < options.MinSounds) return $"fewer than {options.MinSounds} sounds";
    if (grip.Count < options.MinStrings) return $"fewer than {options.MinStrings} strings";
    if (grip.Count < options.MinStringsAlone && HandShapeSuggester.ExistingTemplate(c.Arrangement, grip) == null)
        return $"{grip.Count} strings, no containing template";
    if (slides) return "slide inside";
    if (c.Level.Anchors.Any(a => a.Position > h.Position && a.Position < h.EndPosition)) return "anchor inside";
    var list = sounds.ToList();
    var gap = new FractionalPosition(new Fraction((long)Math.Round(options.MaxGap * 48), 48));
    for (var k = 1; k < list.Count; k++)
    {
        if (list[k] > list[k - 1] + gap) return "gap";
    }

    return "boundary (greedy walk / neighbouring sounds)";
}

static string Signature(ChartLevel level)
{
    var sb = new StringBuilder();
    sb.Append(level.Notes.Count).Append('/').Append(level.Chords.Count).Append('/');
    foreach (var n in level.Notes.OrderBy(n => n.Position).Take(40)) sb.Append(n.StringIndex).Append(n.Fret).Append(';');
    return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
}

// The same split as ChartEval.cs: one song in five is held out from tuning.
static bool IsHoldout(string song) => SHA1.HashData(Encoding.UTF8.GetBytes(song.ToLowerInvariant()))[0] % 5 == 0;

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

internal sealed record EvalChart(string Song, string File, string Kind, bool Holdout, Arrangement Arrangement,
    ChartLevel Level, List<HandShape> Reference);

internal sealed class Score
{
    public int Tp, Fp, Fn, Proposals, MatchedProposals, Targets, MatchedTargets;
    public Dictionary<string, int> Why { get; } = [];

    public void Add(Score o)
    {
        foreach (var (k, v) in o.Why) Why[k] = Why.GetValueOrDefault(k) + v;
        Tp += o.Tp; Fp += o.Fp; Fn += o.Fn;
        Proposals += o.Proposals; MatchedProposals += o.MatchedProposals;
        Targets += o.Targets; MatchedTargets += o.MatchedTargets;
    }

    private static string Pct(int a, int b) => b == 0 ? "  -  " : $"{100.0 * a / b,5:0.0}";

    public string Line() =>
        $"notes P {Pct(Tp, Tp + Fp)} R {Pct(Tp, Tp + Fn)} (tp {Tp} fp {Fp} fn {Fn}) | " +
        $"bars P {Pct(MatchedProposals, Proposals)} R {Pct(MatchedTargets, Targets)} ({Proposals} proposed, {Targets} charted)";
}

// --fixture: windows of TRAIN charts for the default gate's floor (HandShapeSuggesterCorpusFloorTests).
// Frets, techniques the detector reads, sustains, the charter's anchors and bars; positions relative
// to the window's first sound; no names. Regenerate, never hand-edit.
internal static class Fixture
{
    private const int Length = 64;
    private const int Windows = 24;

    public static void Write(string path, List<EvalChart> charts)
    {
        var candidates = new List<(string Hash, Dictionary<string, object> Window)>();
        foreach (var c in charts)
        {
            var positions = c.Level.Notes.Select(n => n.Position).Concat(c.Level.Chords.Select(ch => ch.Position))
                .Distinct().OrderBy(p => p).ToList();
            if (positions.Count < Length * 2) continue;
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(c.Song + "|" + c.File)));
            // Open where a charter's bar over single notes begins, so the window holds the thing scored.
            var notePositions = c.Level.Notes.Select(n => n.Position).ToHashSet();
            var opens = c.Reference.Where(h => notePositions.Any(p => h.Covers(p))).Select(h => h.Position).ToHashSet();
            var start = (int)(Convert.ToUInt32(hash[..8], 16) % (uint)(positions.Count - Length));
            while (start < positions.Count - Length && !opens.Contains(positions[start])) start++;
            if (start >= positions.Count - Length) continue;
            var first = positions[start];
            var last = positions[start + Length - 1];
            var origin = new FractionalPosition(first.BeatId);
            string Rel(FractionalPosition p) => (p - origin).ToString();

            var templates = c.Arrangement.ChordTemplates.Where(t => t.Id >= 0).GroupBy(t => t.Id).ToDictionary(gr => gr.Key, gr => gr.First());
            var notes = c.Level.Notes.Where(n => n.Position >= first && n.Position <= last)
                .Select(n => $"{Rel(n.Position)}|{n.StringIndex}:{n.Fret}{Flags(n.Mute, n.SlideTo.HasValue || n.UnpitchedSlide)}|{n.Sustain}")
                .ToList();
            var chords = new List<string>();
            foreach (var ch in c.Level.Chords.Where(ch => ch.Position >= first && ch.Position <= last))
            {
                var parts = ch.ChordNotes.Count > 0
                    ? ch.ChordNotes.Select(cn => $"{cn.StringIndex}:{cn.Fret}{Flags(cn.Mute, cn.SlideTo.HasValue || cn.UnpitchedSlide)}/{cn.Sustain}")
                    : templates.TryGetValue(ch.TemplateId, out var t) ? t.Frets.Select(kv => $"{kv.Key}:{kv.Value}/0 0/1") : [];
                chords.Add($"{Rel(ch.Position)}|{string.Join(",", parts)}");
            }

            var anchors = c.Level.Anchors.Where(a => a.Position > first && a.Position <= last).Select(a => Rel(a.Position)).ToList();
            var bars = c.Reference.Where(h => h.EndPosition > first && h.Position <= last)
                .Select(h => $"{Rel(FractionalPosition.Max(h.Position, first))}|{Rel(h.EndPosition)}").ToList();
            candidates.Add((hash, new Dictionary<string, object>
            {
                ["strings"] = c.Arrangement.StringCount, ["capo"] = c.Arrangement.Capo,
                ["notes"] = notes, ["chords"] = chords, ["anchors"] = anchors, ["bars"] = bars,
            }));
        }

        var windows = candidates.OrderBy(w => w.Hash, StringComparer.Ordinal).Take(Windows).Select(w => w.Window).ToList();
        var doc = new Dictionary<string, object>
        {
            ["about"] = "tools/chart-eval/HandShapeEval.cs --fixture (#491): windows of the owner's finished guitar charts " +
                        "(train split), frets + mutes + slides + sustains + reference anchors and hand-shape bars, " +
                        "positions relative to the window. Regenerate, never hand-edit.",
            ["windows"] = windows,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc) + "\n");
        Console.WriteLine($"fixture: {windows.Count} windows of {Length} sound positions -> {path}");
    }

    private static string Flags(MuteType mute, bool slides) => (mute == MuteType.Full ? "x" : "") + (slides ? "s" : "");
}
