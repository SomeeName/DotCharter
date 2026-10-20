// .NET 10 file-based program: scores the AI chart pass (#497) against hand-finished charts,
// next to the algorithm it starts from.
//
//   For each selected corpus arrangement: strip the charter's anchors and fingerings and
//   regenerate them with the production algorithm (exactly ChartEval's "auto" mode: suggested
//   fingers, the walk with free grips, the fingering follower), score that; then run the AI pass
//   (--job joint = FHP + fingering, fhp = FHP only, fingering = fingering only) over the whole
//   level through a REAL provider, apply what survives the validator, and score again. Reports
//   tokens and time per song.
//
//   It spends the user's model usage. Keep the selection small and HELD-OUT.
//
//   dotnet run tools/chart-eval/AiChartEval.cs -- --corpus <corpus.json> --list
//   dotnet run tools/chart-eval/AiChartEval.cs -- --corpus <corpus.json> --pick "<song>|<xml file>" [--pick ...]
//       [--job joint|fhp|fingering] [--model claude-sonnet-5-5] [--provider claudecli|codexcli]
//       [--effort default|low|medium|high] [--concurrency 3] [--out <dir>]
//
//   --effort is the Settings ▸ AI ▸ Reasoning level (#603), passed through the provider exactly as
//   the app passes it (Claude Code --effort, Codex -c model_reasoning_effort). Default sends none.
//
// Nothing it writes contains a prompt or a reply: --out gets the per-arrangement numbers and the
// rejection reasons only.
#:project ../../DotCharter.Services/DotCharter.Services.csproj
#:property PublishAot=false
#:property Nullable=enable

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Fingering;
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
var corpusPath = Get("--corpus");
if (corpusPath == null)
{
    Console.Error.WriteLine("usage: --corpus <corpus.json> (--list | --pick \"<song>|<xml file>\" ...) [--model id] [--provider claudecli|codexcli] [--effort default|low|medium|high] [--concurrency n] [--out dir]");
    return 2;
}

var corpus = JsonDocument.Parse(File.ReadAllText(corpusPath)).RootElement;
static bool IsHoldout(string song) => SHA1.HashData(Encoding.UTF8.GetBytes(song.ToLowerInvariant()))[0] % 5 == 0;

if (args.Contains("--list"))
{
    foreach (var song in corpus.EnumerateArray())
    {
        var name = song.GetProperty("song").GetString()!;
        if (!IsHoldout(name)) continue;
        foreach (var a in song.GetProperty("arrangements").EnumerateArray())
        {
            var path = a.GetProperty("xml").GetString()!;
            try
            {
                var arr = new XmlChartSerializer().Load(path).Arrangements[0];
                var lv = arr.Levels[^1];
                Console.WriteLine($"{a.GetProperty("kind").GetString(),-6} levels {arr.Levels.Count} notes {lv.Notes.Count,5} chords {lv.Chords.Count,4}  {name}|{Path.GetFileName(path)}");
            }
            catch (Exception ex) { Console.WriteLine($"unreadable {name}|{Path.GetFileName(path)}: {ex.GetType().Name}"); }
        }
    }

    return 0;
}

var picks = args.Select((v, i) => (v, i)).Where(x => x.v == "--pick" && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();
var modelId = Get("--model") ?? "claude-sonnet-5-5";
var provider = (Get("--provider") ?? "claudecli").ToLowerInvariant() == "codexcli" ? AiProviderKind.CodexCli : AiProviderKind.ClaudeCli;
var concurrency = int.Parse(Get("--concurrency") ?? "3", CultureInfo.InvariantCulture);
var outDir = Get("--out");
var job = (Get("--job") ?? "joint").ToLowerInvariant() switch
{
    "joint" => AiChartJob.FhpAndFingering,
    "fhp" => AiChartJob.FhpOnly,
    "fingering" => AiChartJob.FingeringOnly,
    var other => throw new ArgumentException($"--job {other}: joint, fhp or fingering")
};
var effort = (Get("--effort") ?? "default").ToLowerInvariant() switch
{
    "default" => AiReasoningEffort.Default,
    "low" => AiReasoningEffort.Low,
    "medium" => AiReasoningEffort.Medium,
    "high" => AiReasoningEffort.High,
    var other => throw new ArgumentException($"--effort {other}: default, low, medium or high")
};
if (outDir != null) Directory.CreateDirectory(outDir);

var factory = new ToneDesignModelFactory(new InMemoryAiSecretStore());
var model = factory.Create(new AiProviderSettings(provider, modelId, null, UseLibraryTones: false, WebResearch: false, Effort: effort));
var fingering = new FingeringGenerationService();
var rows = new List<string>();

foreach (var pick in picks)
{
    var parts = pick.Split('|');
    var (songName, file) = (parts[0], parts.Length > 1 ? parts[1] : "");
    string? xml = null, kind = null;
    foreach (var song in corpus.EnumerateArray())
    {
        if (!string.Equals(song.GetProperty("song").GetString(), songName, StringComparison.OrdinalIgnoreCase)) continue;
        // Held-out songs only, unless --allow-train says this is a TUNING run (report it as one).
        if (!IsHoldout(songName) && !args.Contains("--allow-train")) { Console.WriteLine($"REFUSED {pick}: not a held-out song (--allow-train for a tuning run)"); break; }
        foreach (var a in song.GetProperty("arrangements").EnumerateArray())
        {
            var p = a.GetProperty("xml").GetString()!;
            if (file.Length > 0 && !string.Equals(Path.GetFileName(p), file, StringComparison.OrdinalIgnoreCase)) continue;
            xml = p; kind = a.GetProperty("kind").GetString();
            break;
        }
    }

    if (xml == null || kind == null) { Console.WriteLine($"not found: {pick}"); continue; }

    var project = new XmlChartSerializer().Load(xml);
    var arr = project.Arrangements[0];
    if (arr.Levels.Count != 1) { Console.WriteLine($"skipped {pick}: {arr.Levels.Count} levels (DD)"); continue; }
    var level = arr.Levels[0];
    var beats = project.Beats;
    var strings = arr.Tuning.Length > 0 ? arr.Tuning.Length : arr.StringCount;
    var bass = kind == "bass";

    // The reference: what the charter shipped, per chord INDEX (the AI pass repoints chords,
    // it never reorders them) and the anchors.
    var refById = arr.ChordTemplates.Where(t => t.Id >= 0).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Clone());
    var refByChord = level.Chords.Select(c => refById.GetValueOrDefault(c.TemplateId)).ToList();
    var refAnchors = level.Anchors.Select(a => a.Clone()).OrderBy(a => a.Position).ToList();

    // The algorithm: ChartEval's "auto" — suggested fingers from frets alone, anchors walked from them
    // with free grips (#494), and the follower's variants where it re-fingered a free chord.
    for (var i = 0; i < arr.ChordTemplates.Count; i++)
    {
        var t = arr.ChordTemplates[i];
        var bare = new ChordTemplate { Id = t.Id, Name = t.Name, Arpeggio = t.Arpeggio, ForceArpeggioInRs = t.ForceArpeggioInRs };
        foreach (var (k, v) in t.Frets) bare.Frets[k] = v;
        ChordTemplateFingerSetter.SetSuggestedFingers(bare, strings);
        arr.ChordTemplates[i] = bare;
    }

    level.Anchors.Clear();
    var probe = new ChartLevel { DifficultyIndex = level.DifficultyIndex };
    foreach (var n in level.Notes) probe.Notes.Add(n);
    foreach (var c in level.Chords) probe.Chords.Add(c);
    foreach (var h in level.HandShapes) probe.HandShapes.Add(h);
    var freeGrip = ChordFingeringFollower.FreeGripResolver(arr);
    var generated = new FretAnchorGenerationService()
        .GenerateAnchors(probe, chordTemplates: arr.ChordTemplates.ToList(), capo: arr.Capo, bassArrangement: bass,
            freeGrip: freeGrip, tuning: arr)
        .OrderBy(a => a.Position).ToList();
    foreach (var a in generated) level.Anchors.Add(a);
    ChordFingeringFollower.Plan(arr, level, generated, freeGrip, fingering).ApplyTo(arr);

    var algo = Score(arr, level, refByChord, refAnchors);

    var facts = new AiChartSongFacts(arr, bass, null);
    var pass = new AiChartPass(fingering, logger: null, concurrency);
    // --until-sound N: a smoke run over the first N sounds only (scores still cover the whole level).
    FractionalPosition? until = null;
    if (Get("--until-sound") is { } u)
    {
        var all = AiChartChunker.Sounds(level, beats);
        until = all[Math.Min(all.Count - 1, int.Parse(u, CultureInfo.InvariantCulture))].Position;
    }

    var result = await pass.RunAsync(new AiChartPassRequest(job, ChordNamingStyle.Eof, facts, level, beats, To: until),
        model, new Progress<(int Done, int Total)>(p => Console.Error.Write($"\r  {songName}: {p.Done}/{p.Total} chunks   ")), CancellationToken.None);
    Console.Error.WriteLine();
    var action = AiChartApplier.Build(arr, level, result.Accepted, fingering, out var summary);
    action?.Execute();
    var ai = Score(arr, level, refByChord, refAnchors);

    var codes = string.Join(" ", result.Rejected.GroupBy(r => r.Code).Select(g => $"{g.Key}x{g.Count()}"));
    var line = $"{job,-15} {effort,-7} {Trunc(songName, 32),-32} {Trunc(Path.GetFileNameWithoutExtension(xml), 22),-22} {kind,-6} " +
               $"FHP {Pct(algo.FhpSame, algo.FhpSounds)} -> {Pct(ai.FhpSame, ai.FhpSounds)} of {ai.FhpSounds,5} | " +
               $"changes {algo.ChangesGen}->{ai.ChangesGen} (ref {ai.ChangesRef}) | " +
               $"fingering {Pct(algo.ChordsExact, algo.Chords)} -> {Pct(ai.ChordsExact, ai.Chords)} of {ai.Chords,4} | " +
               $"chunks {result.Accepted.Count}/{result.ChunkCount} [{codes}] | " +
               $"{result.InputTokens} in {result.OutputTokens} out | {result.Elapsed.TotalSeconds:F0} s";
    Console.WriteLine(line);
    rows.Add(line);

    if (outDir != null)
    {
        var json = new Dictionary<string, object?>
        {
            ["song"] = songName, ["xml"] = Path.GetFileName(xml), ["kind"] = kind, ["job"] = job.ToString(), ["model"] = result.ModelId, ["effort"] = effort.ToString(),
            ["algo"] = algo.ToJson(), ["ai"] = ai.ToJson(),
            ["chunks"] = result.ChunkCount, ["accepted"] = result.Accepted.Count,
            ["rejections"] = result.Rejected.Select(r => new { r.ChunkIndex, r.Code, r.Reason }).ToList(),
            ["applied"] = summary, ["inputTokens"] = result.InputTokens, ["outputTokens"] = result.OutputTokens,
            ["seconds"] = result.Elapsed.TotalSeconds,
        };
        File.WriteAllText(Path.Combine(outDir, $"{job}__{songName}__{Path.GetFileNameWithoutExtension(xml)}.json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    }
}

Console.WriteLine();
foreach (var r in rows) Console.WriteLine(r);
(model as IDisposable)?.Dispose();
return 0;

static Score Score(Arrangement arr, ChartLevel level, List<ChordTemplate?> refByChord, List<FretAnchor> reference)
{
    var s = new Score();
    var byId = arr.ChordTemplates.Where(t => t.Id >= 0).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
    for (var i = 0; i < level.Chords.Count; i++)
    {
        var refT = refByChord[i];
        if (refT == null) continue;
        var fretted = refT.Frets.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        if (fretted.Count == 0 || fretted.Any(st => !refT.Fingers.TryGetValue(st, out var f) || f < 0)) continue;
        if (!byId.TryGetValue(level.Chords[i].TemplateId, out var now)) continue;
        s.Chords++;
        if (fretted.All(st => now.Fingers.TryGetValue(st, out var g) && g == refT.Fingers[st])) s.ChordsExact++;
    }

    var generated = level.Anchors.OrderBy(a => a.Position).ToList();
    if (reference.Count == 0) return s;
    var sounds = level.Notes.Where(n => n.Fret > arr.Capo).Select(n => n.Position)
        .Concat(level.Chords.Where(c => c.ChordNotes.Any(n => n.Fret > arr.Capo)).Select(c => c.Position))
        .OrderBy(p => p).ToList();
    int? prevRef = null, prevGen = null;
    foreach (var pos in sounds)
    {
        var r = Governing(reference, pos);
        var g = Governing(generated, pos);
        if (r == null) continue;
        s.FhpSounds++;
        if (g == r) s.FhpSame++;
        if (prevRef != null && r != prevRef) s.ChangesRef++;
        if (prevGen != null && g != prevGen) s.ChangesGen++;
        prevRef = r; prevGen = g;
    }

    return s;
}

static int? Governing(List<FretAnchor> anchors, FractionalPosition pos)
{
    int? fret = null;
    foreach (var a in anchors)
    {
        if (a.Position > pos) break;
        fret = a.Fret;
    }

    return fret;
}

static string Pct(int a, int b) => b == 0 ? "   -  " : $"{100.0 * a / b,5:0.0}%";
static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

internal sealed class Score
{
    public int FhpSounds, FhpSame, ChangesRef, ChangesGen, Chords, ChordsExact;

    public Dictionary<string, int> ToJson() => new()
    {
        ["fhpSounds"] = FhpSounds, ["fhpSame"] = FhpSame, ["changesRef"] = ChangesRef, ["changesGen"] = ChangesGen,
        ["chords"] = Chords, ["chordsExact"] = ChordsExact,
    };
}
