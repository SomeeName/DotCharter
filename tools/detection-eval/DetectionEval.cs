// .NET 10 file-based program: the Live Play note-detection accuracy eval.
// Drives the production pipeline (GuitarInputAnalyzer -> LivePlaySessionService.Offer for the
// classic path, LiveCqtVerifier pull-verification for the CQT path) over
//   (a) Guitar-TECHS DI recordings, judged against their hex-pickup MIDI ground truth, and
//   (b) real RS-XML charts rendered by a plucked-string synth (--charts <dir>).
// Runner.Run mirrors LivePlayViewModel.TickCore (DotCharter.UI/ViewModels/Editor/
// LivePlayViewModel.Tick.cs), provisional onsets (#643) included — keep the two in step. Usage and the measured baseline:
// tools/detection-eval/README.md.
#:project ../../DotCharter.Services/DotCharter.Services.csproj
#:property PublishAot=false
#:property Nullable=enable

using System.Text;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Domain.Events;

using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Parsing.Xml;

var argsList = args.ToList();

// Stay out of the way of whoever is using this machine: at most HALF the logical cores,
// never cores 0-3 (games and the desktop live there), idle priority. A 12-thread run once
// made the owner's PC unusable. --dop is clamped to the cores we are allowed.
var allowedCores = Polite.Apply();
string Opt(string name, string def) { var i = argsList.IndexOf(name); return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : def; }
// The Guitar-TECHS extract (directinput/*.wav + midi/*.mid per set); see the README.
var gtRoot = Opt("--gt", Environment.GetEnvironmentVariable("DOTCHARTER_GUITARTECHS_ROOT")
    ?? Path.GetFullPath(Path.Combine("..", "DotCharter-local", "datasets", "rig", "_raw", "guitar-techs", "_extracted")));
var modes = Opt("--modes", "classic,cqt").Split(',');
var filter = Opt("--filter", "");
var csvPath = Opt("--csv", "");
var offsetOverride = Opt("--offset", "");
var maxFiles = int.Parse(Opt("--max", "1000"));
var skewMs = double.Parse(Opt("--skew", "0"), System.Globalization.CultureInfo.InvariantCulture);
var synthGt = Opt("--synthgt", "0") == "1";
var audioVariant = Opt("--audio", "real");
var wrongMode = Opt("--wrong", "0");      // 1 = a semitone off, oct = an octave DOWN
var wrongGt = wrongMode == "1";
var quick = argsList.Contains("--quick");
Runner.NoEarly = argsList.Contains("--noearly");
Runner.NoProvisional = argsList.Contains("--noprovisional");
Runner.Forgiving = argsList.Contains("--forgiving");
ChartBuild.StrumMs = double.Parse(Opt("--strum", "60"), System.Globalization.CultureInfo.InvariantCulture);
XCfg.Parse(Opt);
if (modes.Contains("cqtx")) Console.WriteLine("cqtx: " + XCfg.Describe());

// #645: the contour judge's bends, slides and vibrato — Guitar-TECHS real DI (or rendered charts).
if (argsList.Contains("--techniques"))
{
    TechniqueEval.Run(gtRoot, Opt("--charts", ""), filter, Polite.Dop(Opt("--dop", "0"), allowedCores), quick);
    return;
}

if (Opt("--charts", "") is { Length: > 0 } chartsDir)
{
    ChartEval.Audio = audioVariant;
    ChartEval.Quick = quick;
    ChartEval.Run(chartsDir, modes, filter, Polite.Dop(Opt("--dop", "0"), allowedCores), csvPath, wrongGt, maxFiles);
    return;
}

if (!Directory.Exists(gtRoot))
{
    Console.Error.WriteLine($"Guitar-TECHS not found at {gtRoot} — pass --gt <dir> or set DOTCHARTER_GUITARTECHS_ROOT (see README).");
    return;
}

var cases = new List<EvalCase>();
foreach (var set in Directory.GetDirectories(gtRoot).OrderBy(d => d))
{
    var setName = Path.GetFileName(set);
    var midiDir = Path.Combine(set, "midi");
    var diDir = Path.Combine(set, "audio", "directinput");
    if (!Directory.Exists(diDir)) continue;
    foreach (var wav in Directory.GetFiles(diDir, "*.wav").OrderBy(f => f))
    {
        var stem = Path.GetFileNameWithoutExtension(wav).Replace("directinput_", "");
        var mid = Path.Combine(midiDir, "midi_" + stem + ".mid");
        if (!File.Exists(mid)) { Console.Error.WriteLine($"no midi for {wav}"); continue; }
        var name = setName + "/" + stem;
        if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        cases.Add(new EvalCase(name, setName.EndsWith("techniques") ? "tech:" + stem : setName.Split('_')[1], wav, mid));
    }
}
// --quick: the first file of every set — one of each kind of material, ~5 minutes.
if (quick) cases = cases.GroupBy(c => c.Name.Split('/')[0]).Select(g => g.First()).ToList();
cases = cases.Take(maxFiles).ToList();

var csv = csvPath.Length > 0 ? new StreamWriter(csvPath) : null;
csv?.WriteLine("mode,case,category,timeMs,kind,notes,midis,verdict,minVel,minDurMs,prevGapMs,nextGapMs");

var totals = new Dictionary<string, Tally>();
Tally T(string k) => totals.TryGetValue(k, out var t) ? t : totals[k] = new Tally();
var gate = new object();
var outLines = new SortedDictionary<string, List<string>>();
var dop = Polite.Dop(Opt("--dop", "0"), allowedCores);

Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = dop }, c =>
{
    var (audio, sr) = Wav.ReadMono(c.Wav);
    var notes = Smf.ReadGuitarTechs(c.Mid);
    var clean = GroundTruth.Clean(notes);

    // MIDI vs audio alignment: measure the median onset offset with the production
    // onset detector itself (what the latency calibration does for a real player).
    var lagMs = offsetOverride.Length > 0 ? double.Parse(offsetOverride, System.Globalization.CultureInfo.InvariantCulture) : Align.EstimateLagMs(audio, sr, clean);
    var shifted = clean.Select(n => n with { OnMs = n.OnMs + lagMs + skewMs, OffMs = n.OffMs + lagMs + skewMs }).ToList();
    if (wrongMode == "oct")
    {
        // The player plays everything an octave DOWN: the chart asks for the octave above what
        // is heard. The exploit the sub-octave veto exists to refuse.
        shifted = shifted.Select(n => n with { Midi = n.Midi + 12 }).ToList();
    }
    if (wrongGt)
    {
        var wr = new Random(99);
        int[] op = { 40, 45, 50, 55, 59, 64 };
        shifted = shifted.Select(n =>
        {
            var d = wr.Next(2) == 0 ? 1 : -1;
            var fret = n.Midi - op[n.StringIndex] + d;
            if (fret < 0 || fret > 24) d = -d;
            return n with { Midi = n.Midi + d };
        }).ToList();
    }
    var chart = ChartBuild.FromNotes(shifted);
    // Ground-truth detail per charted onset, for telling detector misses from answer-key
    // noise: the quietest and shortest MIDI note in it, and the gaps to its neighbours.
    var onsets = shifted.Select(n => n.OnMs).Distinct().OrderBy(x => x).ToArray();
    (int Vel, double Dur, double Prev, double Next) Truth(double ms)
    {
        var near = shifted.Where(n => Math.Abs(n.OnMs - ms) <= ChartBuild.StrumMs + 1).ToList();
        var i = Array.BinarySearch(onsets, ms); if (i < 0) i = ~i;
        var prev = onsets.Where(o => o < ms - ChartBuild.StrumMs).DefaultIfEmpty(double.NaN).Max();
        var next = onsets.Where(o => o > ms + ChartBuild.StrumMs).DefaultIfEmpty(double.NaN).Min();
        return near.Count == 0 ? (-1, -1, double.NaN, double.NaN)
            : (near.Min(n => n.Velocity), near.Min(n => n.OffMs - n.OnMs), ms - prev, next - ms);
    }
    if (audioVariant == "idle") audio = Idle.Make(audio.Length, sr, 7);
    var idleFromMs = double.MaxValue;
    if (audioVariant == "halfidle")
    {
        // The player stops half way: real DI, then only idle noise.
        var half = audio.Length / 2;
        var idle = Idle.Make(audio.Length - half, sr, 11);
        Array.Copy(idle, 0, audio, half, idle.Length);
        idleFromMs = half * 1000.0 / sr + 1500;   // let the last notes ring out first
    }
    if (audioVariant == "gainjump")
    {
        // Idle throughout, but the input gain goes up 12 dB half way.
        audio = Idle.Make(audio.Length, sr, 13);
        for (var k = audio.Length / 2; k < audio.Length; k++) audio[k] *= 4f;
    }
    if (synthGt)
    {
        var probe = new LivePlaySessionService();
        probe.Start(chart.Arrangement, chart.Level, chart.Beats, new LivePlaySessionOptions { CountInBeats = 0 });
        audio = Synth.Render(probe.Matcher!.Expected.ToList(), sr, false, 1234, false);
    }
    var lines = new List<string>();

    // "union" = a sound counts when classic OR cqt credited it (both must run first).
    var perMode = new Dictionary<string, (IReadOnlyList<ExpectedSound> S, bool[] Hit)>();
    foreach (var mode in modes)
    {
        IReadOnlyList<ExpectedSound> sounds;
        bool[] hitAt;
        if (mode == "union")
        {
            var a = perMode["classic"]; var b = perMode["cqt"];
            sounds = b.S;
            hitAt = a.Hit.Zip(b.Hit, (x, y) => x || y).ToArray();
        }
        else
        {
            sounds = Runner.Run(audio, sr, chart, mode);
            hitAt = sounds.Select(x => x.Verdict is LivePlayVerdict.Hit or LivePlayVerdict.HitWrongTechnique).ToArray();
            perMode[mode] = (sounds, hitAt);
        }
        var hits = hitAt.Count(h => h);
        var line = $"{mode,-8} {c.Name,-34} lag {lagMs,6:0} ms  {hits,4}/{sounds.Count,-4} = {Pct(hits, sounds.Count)}";
        lines.Add(line);
        lock (gate)
        {
            Console.WriteLine(line);
            for (var si = 0; si < sounds.Count; si++)
            {
                var s = sounds[si];
                var hit = hitAt[si];
                var kind = s.MidiNotes.Length switch { 1 => "single", 2 => "dyad", _ => $"chord{s.MidiNotes.Length}" };
                var reg = s.MidiNotes.Min() switch { < 40 => "sub-E2", < 52 => "E2-E3", < 64 => "E3-E4", < 76 => "E4-E5", _ => "E5+" };
                foreach (var key in new[] { $"{mode}|ALL", $"{mode}|set:{c.Category}", $"{mode}|kind:{kind}", $"{mode}|low:{reg}" })
                    T(key).Add(hit);
                if (idleFromMs < double.MaxValue) T($"{mode}|halfidle:" + (s.TimeMs >= idleFromMs ? "idle-part" : "played-part")).Add(hit);
                if (audioVariant == "gainjump") T($"{mode}|gainjump:" + (s.TimeMs >= audio.Length * 500.0 / sr ? "after" : "before")).Add(hit);
                if (csv is not null)
                {
                    var tr = Truth(s.TimeMs);
                    csv.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{mode},{c.Name},{c.Category},{s.TimeMs:0},{kind},{s.MidiNotes.Length},{string.Join(' ', s.MidiNotes)},{s.Verdict},{tr.Vel},{tr.Dur:0},{tr.Prev:0},{tr.Next:0}"));
                }
            }
            outLines[c.Name] = lines;
        }
    }
});
Console.WriteLine();
foreach (var (_, ls) in outLines) foreach (var l in ls) Console.WriteLine(l);
csv?.Dispose();

foreach (var g in Runner.HitDelays.GroupBy(d => d.Mode))
{
    var a = g.Select(d => d.Ms).OrderBy(x => x).ToArray();
    Console.WriteLine($"{g.Key}|hit credited after the note: median {a[a.Length / 2]:0} ms, p90 {a[(int)(a.Length * 0.9)]:0} ms, max {a[^1]:0} ms (n={a.Length})");
}
// #643: when the timing cue (tier flash, EARLY/LATE) was SHOWN, and when the finalized onset graded.
foreach (var (label, list) in new[] { ("timing cue shown", Runner.CueDelays), ("finalized grade", Runner.GradeDelays) })
{
    foreach (var g in list.GroupBy(d => d.Mode))
    {
        var a = g.Select(d => d.Ms).OrderBy(x => x).ToArray();
        Console.WriteLine($"{g.Key}|{label} after the note: median {a[a.Length / 2]:0} ms, p90 {a[(int)(a.Length * 0.9)]:0} ms (n={a.Length})");
    }
}
foreach (var g in Runner.GradeStages.GroupBy(d => d.Mode))
    Console.WriteLine($"{g.Key}|grades: " + string.Join(", ", g.GroupBy(d => d.Stage).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}")));
foreach (var g in Runner.CueMoves.GroupBy(d => (d.Mode, d.Stage)))
{
    var a = g.Select(d => Math.Abs(d.MoveMs)).OrderBy(x => x).ToArray();
    Console.WriteLine($"{g.Key.Mode}|{g.Key.Stage}: finalized attack moved |median| {a[a.Length / 2]:0} ms, p90 {a[(int)(a.Length * 0.9)]:0} ms; " +
                      $"tier changed {g.Count(d => d.TierChanged)}, moved > 15 ms {g.Count(d => Math.Abs(d.MoveMs) > 15)} (n={a.Length})");
}
foreach (var (mode, t) in Runner.Tiers.OrderBy(kv => kv.Key))
    Console.WriteLine($"{mode}|tiers: perfect {t[0]}, great {t[1]}, good {t[2]}, untimed hits {t[3]}, score {t[4]}");
Console.WriteLine();
foreach (var (k, t) in totals.OrderBy(kv => kv.Key))
    Console.WriteLine($"{k,-28} {t.Hits,5}/{t.Total,-5} = {Pct(t.Hits, t.Total)}");

static string Pct(int a, int b) => b == 0 ? "  n/a" : $"{100.0 * a / b,5:0.0}%";

sealed class Tally { public int Hits, Total; public void Add(bool hit) { Total++; if (hit) Hits++; } }
record EvalCase(string Name, string Category, string Wav, string Mid);
record MidiNote(double OnMs, double OffMs, int Midi, int Velocity, int StringIndex);

static class Runner
{
    public const int TickSamples = 1600;

    /// <summary>--noearly: credit CQT hits only when the window closes (the pre-0.57.4 path).</summary>
    public static bool NoEarly;

    /// <summary>--forgiving: the Lenient setting — the forgiving CQT and the Lenient hit windows.</summary>
    public static bool Forgiving;

    /// <summary>--techniques: the contour judge on (#645), as the setting turns it on in the app.</summary>
    public static bool JudgeTechniques;

    /// <summary>--noprovisional: do not pump the analyzer's provisional onsets (#643) — the
    /// timing cue then waits for the finalized onset, as before. Stats must not differ.</summary>
    public static bool NoProvisional;

    // #643: when each graded hit's timing cue was first SHOWN (a provisional cue, or the finalized
    // grade) and when its finalized grade came, both after the charted note; every grade's stage;
    // per mode the tier counters [perfect, great, good, untimed hits, score] summed over cases.
    public static readonly List<(string Mode, double Ms)> CueDelays = new();
    public static readonly List<(string Mode, double Ms)> GradeDelays = new();
    public static readonly List<(string Mode, LivePlayGradeStage Stage)> GradeStages = new();
    public static readonly Dictionary<string, long[]> Tiers = new();
    // #643: per finalized grade of a cued hit, how far the finalized attack moved from the
    // provisional one, and whether the tier changed.
    public static readonly List<(string Mode, LivePlayGradeStage Stage, double MoveMs, bool TierChanged)> CueMoves = new();

    // How long after its charted time each hit was CREDITED (only hits the tick loop surfaced:
    // pull-verified CQT hits; the classic path's pushed hits are credited on Offer).
    public static readonly List<(string Mode, double Ms)> HitDelays = new();

    // The onset/pitch analyzer depends on the audio alone, so it runs ONCE per buffer and
    // every mode (and the alignment) replays its events: per tick, in order — the same
    // events the live tick loop would Offer. This is most of what a multi-mode run used to
    // repeat.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<float[], List<LiveDetectionEvent>[]> Events = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<float[], List<LiveOnsetNotice>[]> Notices = new();

    public static List<LiveDetectionEvent>[] Detect(float[] audio, int sr) => Events.GetValue(audio, a =>
    {
        var analyzer = new GuitarInputAnalyzer(sr);
        var total = a.Length + sr * 3;
        var ticks = new List<LiveDetectionEvent>[(total + TickSamples - 1) / TickSamples];
        var notices = new List<LiveOnsetNotice>[ticks.Length];
        var block = new float[TickSamples];
        for (int off = 0, k = 0; off < total; off += TickSamples, k++)
        {
            var n = Math.Min(TickSamples, total - off);
            Array.Clear(block);
            if (off < a.Length) Array.Copy(a, off, block, 0, Math.Min(n, a.Length - off));
            ticks[k] = [.. analyzer.Push(block.AsSpan(0, n), (ulong)off)];
            notices[k] = [.. analyzer.OnsetNotices];
        }
        Notices.AddOrUpdate(a, notices);
        return ticks;
    });

    /// <summary>The provisional onsets (#643) of the same analyzer pass, per tick.</summary>
    public static List<LiveOnsetNotice>[] DetectNotices(float[] audio, int sr)
    {
        Detect(audio, sr);
        return Notices.TryGetValue(audio, out var n) ? n : throw new InvalidOperationException("no analyzer pass");
    }

    /// <summary>The LivePlayViewModel.TickCore composition, offline: stream position
    /// maps 1:1 onto transport time (speed 1, zero latency).</summary>
    public static IReadOnlyList<ExpectedSound> Run(float[] audio, int sr, Chart chart, string mode)
    {
        var detected = Detect(audio, sr);
        var notices = DetectNotices(audio, sr);
        var cqt = mode == "cqt" ? LiveCqtVerifier.CreateStreaming(sr, forgiving: Forgiving) : null;
        var xv = mode == "cqtx" ? new XVerifier(sr) : null;
        var session = new LivePlaySessionService();
        session.Start(chart.Arrangement, chart.Level, chart.Beats, new LivePlaySessionOptions
        {
            HitWindow = Forgiving ? LiveHitWindowPreset.Lenient : LiveHitWindowPreset.Normal,
            ChordStrictness = LiveChordVoicingStrictness.Standard,
            StringAwareVerification = false,
            ChartVerify = cqt is not null ? cqt.Verify : xv is not null ? xv.Verify : null,
            ChartVerifyEarly = cqt is not null && !NoEarly ? cqt.VerifyEarly : null,
            JudgeContour = JudgeTechniques && cqt is not null ? cqt.JudgeTechnique : null,
            CountInBeats = 0,
        });
        session.SyncTo(0);
        var extra = new List<LivePlayEvent>();
        var graded = new List<LivePlayGrade>();
        var cued = new HashSet<ExpectedSound>();
        var cues = new Dictionary<ExpectedSound, LivePlayGrade>();
        var tail = sr * 3;
        var total = audio.Length + tail;
        var block = new float[TickSamples];
        for (var off = 0; off < total; off += TickSamples)
        {
            var n = Math.Min(TickSamples, total - off);
            Array.Clear(block);
            if (off < audio.Length) Array.Copy(audio, off, block, 0, Math.Min(n, audio.Length - off));
            var span = block.AsSpan(0, n);
            var playheadMs = (off + n) * 1000.0 / sr;
            var events = detected[off / TickSamples];
            if (cqt is not null)
            {
                cqt.Push(span, off);
                cqt.SetAnchor(off, off * 1000.0 / sr, 1.0);
            }
            xv?.Push(span, off);
            void Graded(LivePlayGrade g)
            {
                lock (GradeStages)
                {
                    GradeStages.Add((mode, g.Stage));
                    if (g.Stage != LivePlayGradeStage.Provisional) GradeDelays.Add((mode, playheadMs - g.Sound.TimeMs));
                    if (cued.Add(g.Sound)) CueDelays.Add((mode, playheadMs - g.Sound.TimeMs));
                    if (g.Stage == LivePlayGradeStage.Provisional) cues[g.Sound] = g;
                    else if (cues.TryGetValue(g.Sound, out var cue))
                        CueMoves.Add((mode, g.Stage, g.AttackAtMs - cue.AttackAtMs, g.Tier != cue.Tier));
                }
            }
            // LivePlayViewModel.TickCore order: provisional onsets first, then the events.
            if (!NoProvisional)
            {
                foreach (var notice in notices[off / TickSamples])
                {
                    session.OfferOnsetNotice(notice, notice.StreamSeconds * 1000.0, graded);
                    foreach (var g in graded) Graded(g);
                }
            }
            foreach (var ev in events)
            {
                var detectedMs = ev.StreamSeconds * 1000.0;
                session.Offer(ev.Pitches, ev.Chroma, detectedMs, ev.Techniques, ev.SpectralCentroidHz, extra, graded,
                    ev.DetectorSamplePosition);
                foreach (var g in graded) Graded(g);
            }
            var tick = session.HandleTick(playheadMs, true);
            foreach (var e in tick.Events)
                if (e.Verdict is LivePlayVerdict.Hit or LivePlayVerdict.HitWrongTechnique)
                    lock (HitDelays) HitDelays.Add((mode, playheadMs - e.Sound.TimeMs));
            foreach (var g in tick.Grades) Graded(g);
        }
        var stats = session.Matcher!.Stats;
        lock (Tiers)
        {
            if (!Tiers.TryGetValue(mode, out var t)) Tiers[mode] = t = new long[5];
            t[0] += stats.PerfectCount;
            t[1] += stats.GreatCount;
            t[2] += stats.GoodCount;
            t[3] += stats.Hits - stats.PerfectCount - stats.GreatCount - stats.GoodCount;
            t[4] += stats.Score;
        }
        return session.Matcher.Expected.ToList();
    }
}

record Chart(Arrangement Arrangement, ChartLevel Level, List<BeatMarker> Beats);

static class ChartBuild
{
    public static readonly int[] Open = { 40, 45, 50, 55, 59, 64 };
    const double MsPerBeat = 500;

    /// <summary>Notes on different strings that start within this many ms are one strum
    /// (--strum, default 60). A slow strum spreads further, and a split strum becomes a dyad
    /// plus a stray single the chart would never contain.</summary>
    public static double StrumMs = 60;

    public static Chart FromNotes(List<MidiNote> notes)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        var lastMs = notes.Count == 0 ? 0 : notes.Max(n => n.OffMs);
        var beats = Enumerable.Range(0, (int)(lastMs / MsPerBeat) + 16)
            .Select(i => new BeatMarker { Time = i * MsPerBeat, Bpm = 120, IsMeasure = i % 4 == 0 }).ToList();

        // Group near-simultaneous onsets on DIFFERENT strings into a chord (a strum spreads).
        var sorted = notes.OrderBy(n => n.OnMs).ToList();
        var i = 0;
        while (i < sorted.Count)
        {
            var group = new List<MidiNote> { sorted[i] };
            var j = i + 1;
            while (j < sorted.Count && sorted[j].OnMs - sorted[i].OnMs <= StrumMs &&
                   group.All(g => g.StringIndex != sorted[j].StringIndex))
                group.Add(sorted[j++]);
            i = j;
            var t = group.Min(g => g.OnMs);
            var pos = Pos(t);
            if (group.Count == 1)
            {
                var g = group[0];
                level.Notes.Add(new GuitarNote { Position = pos, StringIndex = g.StringIndex, Fret = g.Midi - Open[g.StringIndex], Sustain = Dur(g.OffMs - g.OnMs) });
            }
            else
            {
                var chord = new Chord { Position = pos };
                foreach (var g in group.OrderBy(g => g.StringIndex))
                    chord.ChordNotes.Add(new ChordNote { StringIndex = g.StringIndex, Fret = g.Midi - Open[g.StringIndex], Sustain = Dur(g.OffMs - g.OnMs) });
                level.Chords.Add(chord);
            }
        }
        return new Chart(arr, level, beats);
    }

    public static FractionalPosition Pos(double ms)
    {
        var beat = (int)(ms / MsPerBeat);
        var rem = (long)Math.Round((ms - beat * MsPerBeat) * 1000);
        return new FractionalPosition(beat, new Fraction(rem, (long)(MsPerBeat * 1000)));
    }

    public static FractionalPosition Dur(double ms) => new(new Fraction((long)Math.Round(ms * 1000), (long)(MsPerBeat * 1000)));
}

static class GroundTruth
{
    /// <summary>Drops the hex MIDI converter's ghost notes (a few ms long or barely
    /// any velocity) and anything outside a guitar's fret range.</summary>
    public static List<MidiNote> Clean(List<MidiNote> notes)
    {
        int[] open = { 40, 45, 50, 55, 59, 64 };
        return notes.Where(n => n.OffMs - n.OnMs >= 60 && n.Velocity >= 25 &&
                                n.StringIndex is >= 0 and < 6 &&
                                n.Midi - open[n.StringIndex] is >= 0 and <= 24).ToList();
    }
}

static class Align
{
    /// <summary>Median lag of the MIDI onsets behind the analyzer's own onsets.</summary>
    public static double EstimateLagMs(float[] audio, int sr, List<MidiNote> notes)
    {
        var onsets = new List<double>();
        foreach (var tick in Runner.Detect(audio, sr))
            foreach (var ev in tick)
                if (!ev.IsLegato) onsets.Add(ev.StreamSeconds * 1000.0);
        var deltas = new List<double>();
        foreach (var t in notes.Select(n => n.OnMs).Distinct())
        {
            var best = double.MaxValue;
            foreach (var o in onsets) if (Math.Abs(o - t) < Math.Abs(best)) best = o - t;
            if (Math.Abs(best) < 250) deltas.Add(best);
        }
        if (deltas.Count == 0) return 0;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }
}

static class Wav
{
    public static (float[] Samples, int SampleRate) ReadMono(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException(path);
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException(path);
        int fmt = 1, ch = 1, sr = 48000, bits = 16;
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4));
            var len = br.ReadInt32();
            if (id == "fmt ")
            {
                fmt = br.ReadInt16(); ch = br.ReadInt16(); sr = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                br.BaseStream.Position += len - 16;
                if (fmt == -2) fmt = 1; // extensible — assume PCM
            }
            else if (id == "data")
            {
                var bytes = br.ReadBytes(len);
                var bps = bits / 8;
                var frames = bytes.Length / (bps * ch);
                var outp = new float[frames];
                for (var f = 0; f < frames; f++)
                {
                    double acc = 0;
                    for (var c = 0; c < ch; c++)
                    {
                        var o = (f * ch + c) * bps;
                        acc += bits switch
                        {
                            16 => BitConverter.ToInt16(bytes, o) / 32768.0,
                            24 => ((bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16)) << 8 >> 8) / 8388608.0,
                            32 when fmt == 3 => BitConverter.ToSingle(bytes, o),
                            32 => BitConverter.ToInt32(bytes, o) / 2147483648.0,
                            _ => throw new NotSupportedException($"{bits}-bit"),
                        };
                    }
                    outp[f] = (float)(acc / ch);
                }
                return (outp, sr);
            }
            else br.BaseStream.Position += len + (len & 1);
        }
        throw new InvalidDataException("no data chunk: " + path);
    }
}

static class Smf
{
    /// <summary>Guitar-TECHS layout: tracks named e/B/G/D/A/E, one per string.</summary>
    public static List<MidiNote> ReadGuitarTechs(string path)
    {
        var d = File.ReadAllBytes(path);
        int div = (d[12] << 8) | d[13];
        int ntr = (d[10] << 8) | d[11];
        var i = 14;
        var tempo = new List<(long Tick, int UsPerQ)>();
        var raw = new List<(long On, long Off, int Midi, int Vel, int Str)>();
        for (var tr = 0; tr < ntr; tr++)
        {
            var len = (d[i + 4] << 24) | (d[i + 5] << 16) | (d[i + 6] << 8) | d[i + 7];
            var j = i + 8; var end = j + len; i = end;
            long t = 0; int run = 0; string name = "";
            var open = new Dictionary<int, (long On, int Vel)>();
            while (j < end)
            {
                t += Vlq(d, ref j);
                int s = d[j];
                if (s == 0xFF)
                {
                    int typ = d[j + 1]; j += 2; var ln = (int)Vlq(d, ref j);
                    if (typ == 0x03) name = Encoding.ASCII.GetString(d, j, ln);
                    if (typ == 0x51) tempo.Add((t, (d[j] << 16) | (d[j + 1] << 8) | d[j + 2]));
                    j += ln; continue;
                }
                if (s is 0xF0 or 0xF7) { j++; var ln = (int)Vlq(d, ref j); j += ln; continue; }
                if ((s & 0x80) != 0) { run = s; j++; }
                var hi = run & 0xF0;
                var n = hi is 0xC0 or 0xD0 ? 1 : 2;
                if (hi is 0x80 or 0x90)
                {
                    int note = d[j], vel = d[j + 1];
                    var str = name switch { "e" => 5, "B" => 4, "G" => 3, "D" => 2, "A" => 1, "E" => 0, _ => -1 };
                    if (hi == 0x90 && vel > 0) open[note] = (t, vel);
                    else if (open.Remove(note, out var o)) raw.Add((o.On, t, note, o.Vel, str));
                }
                j += n;
            }
        }
        tempo.Sort();
        if (tempo.Count == 0 || tempo[0].Tick > 0) tempo.Insert(0, (0, 500000));
        double Ms(long tick)
        {
            double ms = 0; long prev = 0; var us = tempo[0].UsPerQ;
            foreach (var (tk, u) in tempo)
            {
                if (tk >= tick) break;
                ms += (tk - prev) * us / 1000.0 / div; prev = tk; us = u;
            }
            return ms + (tick - prev) * us / 1000.0 / div;
        }
        return raw.Select(r => new MidiNote(Ms(r.On), Ms(r.Off), r.Midi, r.Vel, r.Str)).OrderBy(n => n.OnMs).ToList();
    }

    static long Vlq(byte[] d, ref int j)
    {
        long v = 0;
        while (true) { var b = d[j++]; v = (v << 7) | (uint)(b & 0x7F); if (b < 0x80) return v; }
    }
}

static class ChartEval
{
    public static string Audio = "real";
    public static bool Quick;

    public static void Run(string dir, string[] modes, string filter, int dop, string csvPath, bool wrong, int max)
    {
        var files = Directory.GetFiles(dir, "*RS2.xml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".debug.", StringComparison.OrdinalIgnoreCase))
            .Where(f => filter.Length == 0 || f.Replace(Path.DirectorySeparatorChar, '/').Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f).ToList();
        // The same chart is often saved under two names (Lead_RS2 + PART REAL_GUITAR_RS2).
        var seen = new HashSet<string>();
        files = files.Where(f => seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f))))).Take(max).ToList();
        // --quick: every 8th chart, a spread of guitar, bass and tunings.
        if (Quick) files = files.Where((_, i) => i % 8 == 0).ToList();

        var gate = new object();
        var totals = new SortedDictionary<string, Tally>();
        void Add(string k, bool hit) { if (!totals.TryGetValue(k, out var t)) totals[k] = t = new Tally(); t.Add(hit); }
        var csv = csvPath.Length > 0 ? new StreamWriter(csvPath) : null;
        csv?.WriteLine("mode,file,arr,tuning,timeMs,ioiMs,kind,midis,tech,verdict");
        var perSong = new SortedDictionary<string, string>();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = dop }, file =>
        {
            ChartProject project;
            try { project = new XmlChartSerializer().Load(file); }
            catch (Exception ex) { Console.Error.WriteLine($"load failed {file}: {ex.Message}"); return; }
            var arr = project.Arrangements.FirstOrDefault();
            if (arr == null || arr.Levels.Count == 0 || project.Beats.Count == 0) return;
            var beats = project.Beats.ToList();
            var isBass = arr.ArrangementType == ArrangementType.Bass;
            var tuning = string.Join(",", Enumerable.Range(0, 6).Select(i => DotCharter.Core.Utilities.TuningOffsetResolution.For(arr, i)));
            var name = Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileNameWithoutExtension(file);

            // What the matcher expects (hardest level), then render exactly that.
            var probe = new LivePlaySessionService();
            probe.Start(arr, null, beats, new LivePlaySessionOptions { CountInBeats = 0 });
            var expected = probe.Matcher!.Expected.ToList();
            if (expected.Count == 0) return;
            const int sr = 48000;
            var audio = Synth.Render(expected, sr, isBass, seed: StableHash(name), wrong);
            if (Audio == "idle") audio = Idle.Make(audio.Length, sr, StableHash(name));

            foreach (var mode in modes)
            {
                var sounds = Runner.Run(audio, sr, new Chart(arr, null!, beats), mode);
                var hits = sounds.Count(s => s.Verdict is LivePlayVerdict.Hit or LivePlayVerdict.HitWrongTechnique);
                lock (gate)
                {
                    var line = $"{mode,-8} {name,-60} {arr.ArrangementType,-6} [{tuning}] {hits,5}/{sounds.Count,-5} = {Pct(hits, sounds.Count)}";
                    Console.WriteLine(line);
                    perSong[name + "|" + mode] = line;
                    double prevMs = -10000;
                    foreach (var s in sounds.OrderBy(x => x.TimeMs))
                    {
                        var ioi = s.TimeMs - prevMs; prevMs = s.TimeMs;
                        var hit = s.Verdict is LivePlayVerdict.Hit or LivePlayVerdict.HitWrongTechnique;
                        var kind = s.MidiNotes.Length switch { 0 => "mute", 1 => "single", 2 => "dyad", _ => $"chord{s.MidiNotes.Length}" };
                        var low = s.MidiNotes.Length == 0 ? 0 : s.MidiNotes.Min();
                        var reg = low switch { < 28 => "<E1", < 40 => "E1-E2", < 52 => "E2-E3", < 64 => "E3-E4", _ => "E4+" };
                        var tech = Tech(s);
                        var inst = isBass ? "bass" : "gtr";
                        Add($"{mode}|ALL", hit);
                        Add($"{mode}|inst:{inst}", hit);
                        Add($"{mode}|{inst}|kind:{kind}", hit);
                        Add($"{mode}|{inst}|low:{reg}", hit);
                        Add($"{mode}|{inst}|tech:{tech}", hit);
                        Add($"{mode}|{inst}|tuning:{tuning.Split(',')[0]}", hit);
                        Add($"{mode}|{inst}|ioi:" + (ioi < 100 ? "<100" : ioi < 150 ? "100-150" : ioi < 250 ? "150-250" : ioi < 500 ? "250-500" : ">500"), hit);
                        csv?.WriteLine($"{mode},{name},{arr.ArrangementType},{tuning.Replace(',', ' ')},{s.TimeMs:0},{ioi:0},{kind},{string.Join(' ', s.MidiNotes)},{tech},{s.Verdict}");
                    }
                }
            }
        });
        csv?.Dispose();
        Console.WriteLine();
        foreach (var (_, l) in perSong) Console.WriteLine(l);
        Console.WriteLine();
        foreach (var (k, t) in totals) Console.WriteLine($"{k,-34} {t.Hits,6}/{t.Total,-6} = {Pct(t.Hits, t.Total)}");
    }

    static int StableHash(string s) { var h = 17; foreach (var ch in s) h = h * 31 + ch; return h; }

    static string Tech(ExpectedSound s) =>
        s.FullMute ? "fullmute" : s.Harmonic ? "harmonic" : s.PinchHarmonic ? "pinch" : s.SlapOrPop ? "slap" :
        s.SlideToMidi != null || s.SlideSemis != 0 || s.UnpitchedSlide ? "slide" : s.MaxBendSemis > 0 ? "bend" :
        s.Legato ? "legato" : s.PalmMute ? "palmmute" : s.Tremolo ? "tremolo" : "plain";

    static string Pct(int a, int b) => b == 0 ? "  n/a" : $"{100.0 * a / b,5:0.0}%";
}

/// <summary>A plucked-string DI renderer: Karplus-Strong with a fractional-delay
/// allpass (so the pitch is exact), a pick-position comb and a string-dependent decay.
/// Chords are strummed low to high; each sound rings until the next one starts.</summary>
static class Synth
{
    public static float[] Render(List<ExpectedSound> sounds, int sr, bool bass, int seed, bool wrong)
    {
        var rng = new Random(seed);
        var endMs = sounds.Max(s => s.TimeMs) + 3000;
        var buf = new float[(int)(endMs / 1000.0 * sr)];
        var ordered = sounds.OrderBy(s => s.TimeMs).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var nextMs = i + 1 < ordered.Count ? ordered[i + 1].TimeMs : s.TimeMs + 2500;
            var durMs = Math.Clamp(nextMs - s.TimeMs - 5, 60, 3000);
            var gain = (float)(0.35 * (0.7 + 0.6 * rng.NextDouble()));
            if (s.FullMute || s.MidiNotes.Length == 0)
            {
                Noise(buf, (int)(s.TimeMs / 1000 * sr), sr, gain * 0.6f, rng);
                continue;
            }
            var notes = s.MidiNotes.OrderBy(m => m).ToArray();
            var spreadMs = notes.Length > 1 ? 6 + rng.NextDouble() * 8 : 0;
            for (var k = 0; k < notes.Length; k++)
            {
                var midi = notes[k] + (wrong ? (rng.Next(2) == 0 ? 1 : -1) : 0);
                var startMs = s.TimeMs + k * spreadMs;
                var endGlide = s.SlideToMidi ?? (s.SlideSemis != 0 ? midi + s.SlideSemis : (int?)null);
                Pluck(buf, sr, startMs, durMs - k * spreadMs, midi, endGlide, s.MaxBendSemis, s.PalmMute, bass,
                    gain / (float)Math.Sqrt(notes.Length), rng);
            }
        }
        return buf;
    }

    static void Noise(float[] buf, int at, int sr, float gain, Random rng)
    {
        var len = sr / 25;
        double lp = 0;
        for (var i = 0; i < len && at + i < buf.Length; i++)
        {
            lp += 0.3 * ((rng.NextDouble() * 2 - 1) - lp);
            buf[at + i] += (float)(lp * gain * Math.Exp(-i / (sr * 0.008)));
        }
    }

    static void Pluck(float[] buf, int sr, double startMs, double durMs, int midi, int? glideTo, int bendSemis,
        bool palmMute, bool bass, float gain, Random rng)
    {
        var f0 = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
        var start = (int)(startMs / 1000 * sr);
        var len = (int)(durMs / 1000 * sr);
        var lowest = Math.Min(midi, glideTo ?? midi) - 1;
        var maxN = (int)(sr / (440.0 * Math.Pow(2, (lowest - 69) / 12.0)) + 4);
        var size = maxN * 2 + 8;
        var line = new double[size];
        // Excitation: filtered noise with a pick-position comb.
        var n0 = sr / f0;
        var excite = (int)n0;
        var pickPos = Math.Max(1, (int)(n0 * (0.12 + 0.1 * rng.NextDouble())));
        var tmp = new double[excite];
        double lp = 0;
        var bright = palmMute ? 0.15 : bass ? 0.35 : 0.55;
        for (var i = 0; i < excite; i++) { lp += bright * ((rng.NextDouble() * 2 - 1) - lp); tmp[i] = lp; }
        for (var i = 0; i < excite; i++) line[i] = tmp[i] - (i >= pickPos ? tmp[i - pickPos] : 0);
        var t60 = palmMute ? 0.25 : bass ? 4.0 - (midi - 28) * 0.05 : 3.2 - (midi - 40) * 0.05;
        t60 = Math.Max(palmMute ? 0.15 : 0.8, t60);
        var w = excite;
        double apState = 0, prevIn = 0, dc = 0, dcPrev = 0;
        var fade = Math.Min(len, (int)(0.01 * sr));
        for (var i = 0; i < len; i++)
        {
            var t = i / (double)len;
            var semis = 0.0;
            if (glideTo is { } g) semis = (g - midi) * Math.Clamp((t - 0.3) / 0.5, 0, 1);
            if (bendSemis > 0) semis += bendSemis * Math.Clamp(t / 0.25, 0, 1);
            var f = f0 * Math.Pow(2, semis / 12);
            var n = sr / f - 0.5;
            var d = (int)Math.Floor(n);
            var frac = n - d;
            if (frac < 0.1) { d--; frac += 1; }
            var c = (1 - frac) / (1 + frac);
            var loss = Math.Pow(10, -3.0 / (t60 * f));
            var a = line[(w - d + size) % size];
            var b = line[(w - d - 1 + size) % size];
            var avg = 0.5 * (a + b) * loss;
            var y = c * avg + prevIn - c * apState;
            prevIn = avg; apState = y;
            line[w] = y;
            w = (w + 1) % size;
            var o = y - dcPrev + 0.995 * dc; dcPrev = y; dc = o;
            var env = i >= len - fade ? (len - i) / (double)fade : 1.0;
            var idx = start + i;
            if (idx >= 0 && idx < buf.Length) buf[idx] += (float)(o * gain * env);
        }
    }
}

/// <summary>Mode <c>cqtx</c>: the CQT verifier with every knob on the command line, for
/// trying a setting before it goes into <c>LiveCqtConfig</c>. The defaults ARE the shipping
/// configuration, so <c>--modes cqt,cqtx</c> with no knobs must print two identical columns
/// (that is the harness's own sanity check). Offline identity mapping: chart ms == capture ms.</summary>
static class XCfg
{
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    public static int MinMidi = JuceSharp.Dsp.LiveCqtConfig.MinMidi;
    public static int BinCount = JuceSharp.Dsp.LiveCqtConfig.BinCount;
    public static double Before = 0.04, After = 0.14;
    public static double HarmonicPeakHz = JuceSharp.Dsp.LiveCqtConfig.HarmonicPeakBelowHz;
    public static double Coverage = 0.5;
    public static float Margin = JuceSharp.Dsp.LiveCqtConfig.StreamingNoiseMargin;
    public static double Veto = JuceSharp.Dsp.LiveCqtConfig.SubOctaveVeto;
    public static bool Quality = true;
    public static int MaxKernel = JuceSharp.Dsp.LiveCqtConfig.MaxKernelSamples;
    public static float RelFloor = JuceSharp.Dsp.LiveCqtConfig.RelFloor;
    public static double IdleSeconds = JuceSharp.Dsp.LiveCqtConfig.IdleSteadySeconds;
    public static float IdleRatio = JuceSharp.Dsp.LiveCqtConfig.IdleSteadyRatio;
    public static double IdleRise = JuceSharp.Dsp.LiveCqtConfig.IdleFloorRiseDbPerSecond;
    public static bool Overtones = true;
    public static double ChordAfter = JuceSharp.Dsp.LiveCqtConfig.ChordPostWindowAfterSeconds;
    public static bool Ring = true;
    public static bool Why;
    public static int Quarter;          // 1 = also accept the quarter-tone bins either side; 2 = only where that bin is the local peak
    public static double VetoRatio = -1; // override the sub-octave veto ratio (JuceChartVerifier.SubOctaveVeto)

    public static void Parse(Func<string, string, string> opt)
    {
        MinMidi = int.Parse(opt("--minmidi", MinMidi.ToString(Inv)), Inv);
        BinCount = int.Parse(opt("--bins", (JuceSharp.Dsp.LiveCqtConfig.BinCount + (JuceSharp.Dsp.LiveCqtConfig.MinMidi - MinMidi) * 2).ToString(Inv)), Inv);
        var w = opt("--post", "0.04,0.14").Split(',');
        Before = double.Parse(w[0], Inv);
        After = double.Parse(w[1], Inv);
        HarmonicPeakHz = double.Parse(opt("--harmpeak", HarmonicPeakHz.ToString(Inv)), Inv);
        Coverage = double.Parse(opt("--coverage", "0.5"), Inv);
        Margin = float.Parse(opt("--margin", Margin.ToString(Inv)), Inv);
        Veto = double.Parse(opt("--veto", Veto.ToString(Inv)), Inv);
        Quality = opt("--quality", "1") == "1";
        MaxKernel = int.Parse(opt("--kernel", MaxKernel.ToString(Inv)), Inv);
        RelFloor = float.Parse(opt("--relfloor", RelFloor.ToString(Inv)), Inv);
        IdleSeconds = double.Parse(opt("--idlesec", IdleSeconds.ToString(Inv)), Inv);
        IdleRatio = float.Parse(opt("--idleratio", IdleRatio.ToString(Inv)), Inv);
        IdleRise = double.Parse(opt("--idlerise", IdleRise.ToString(Inv)), Inv);
        Overtones = opt("--overtones", "1") == "1";
        ChordAfter = double.Parse(opt("--chordpost", ChordAfter.ToString(Inv)), Inv);
        Ring = opt("--ring", "1") == "1";
        Why = opt("--why", "0") == "1";
        Quarter = int.Parse(opt("--quarter", "0"));
    }

    public static string Describe() => string.Create(Inv,
        $"minmidi={MinMidi} bins={BinCount} post=({Before},{After}) harmpeak={HarmonicPeakHz} coverage={Coverage} " +
        $"margin={Margin} veto={Veto} quality={Quality} kernel={MaxKernel} relfloor={RelFloor} " +
        $"idlesec={IdleSeconds} idleratio={IdleRatio} idlerise={IdleRise} overtones={Overtones} chordpost={ChordAfter} ring={Ring} quarter={Quarter}");
}

sealed class XVerifier
{
    static readonly int[] OvertoneSemis = { 12, 19, 24, 28 };
    readonly JuceSharp.Dsp.JuceConstantQ _cqt;
    readonly JuceSharp.Dsp.JuceChartVerifier _v, _vChord;
    readonly double[] _credited = Enumerable.Repeat(double.NegativeInfinity, 128).ToArray();
    readonly HashSet<int> _tones = new(), _ok = new();

    public XVerifier(int sr)
    {
        _cqt = new JuceSharp.Dsp.JuceConstantQ(sr, hop: JuceSharp.Dsp.LiveCqtConfig.Hop, fMinMidi: XCfg.MinMidi,
            binCount: XCfg.BinCount, relFloor: XCfg.RelFloor, maxKernelSamples: XCfg.MaxKernel,
            noiseHistoryFrames: JuceSharp.Dsp.LiveCqtConfig.NoiseHistoryFrames(sr),
            subOctaveBins: JuceSharp.Dsp.LiveCqtConfig.SubOctaveBins,
            idleSteadyFrames: (int)(XCfg.IdleSeconds * sr / JuceSharp.Dsp.LiveCqtConfig.Hop),
            idleSteadyRatio: XCfg.IdleRatio, idleFloorRiseDbPerSecond: XCfg.IdleRise);
        _v = new JuceSharp.Dsp.JuceChartVerifier(_cqt)
        {
            NoiseMargin = XCfg.Margin,
            SubOctaveVeto = XCfg.Veto,
            PostWindow = (XCfg.Before, XCfg.After),
            HarmonicPeakBelowHz = XCfg.HarmonicPeakHz,
        };
        _vChord = new JuceSharp.Dsp.JuceChartVerifier(_cqt)
        {
            NoiseMargin = XCfg.Margin,
            SubOctaveVeto = XCfg.Veto,
            PostWindow = (XCfg.Before, XCfg.ChordAfter),
            HarmonicPeakBelowHz = XCfg.HarmonicPeakHz,
        };
    }

    public void Push(ReadOnlySpan<float> s, long pos) => _cqt.Push(s, pos);

    bool Tone(JuceSharp.Dsp.JuceChartVerifier v, int m, double t, bool allowHarmonic, bool veto, bool harmonic)
    {
        if (v.Verify(m, t, allowHarmonic, veto).Hit) return true;
        if (XCfg.Quarter == 1 && (v.Verify(m - 0.5, t, allowHarmonic, veto).Hit || v.Verify(m + 0.5, t, allowHarmonic, veto).Hit)) return true;
        if (XCfg.Quarter == 2 && (Detuned(v, m - 0.5, t, allowHarmonic, veto) || Detuned(v, m + 0.5, t, allowHarmonic, veto))) return true;
        if (!harmonic || !XCfg.Overtones) return false;
        foreach (var semis in OvertoneSemis)
            if (v.Verify(m + semis, t, false, false).Hit) return true;
        return false;
    }

    // A detuned note: the quarter-tone bin confirms AND is a strict local maximum against its
    // immediate quarter-tone neighbours at its loudest frame — a note a semitone off peaks on
    // its own bin, leaving this one on its slope.
    bool Detuned(JuceSharp.Dsp.JuceChartVerifier v, double q, double t, bool allowHarmonic, bool veto)
    {
        if (!v.Verify(q, t, allowHarmonic, veto).Hit) return false;
        var b = _cqt.BinForMidi(q);
        long f0 = _cqt.FrameOfSeconds(t - v.PostWindow.Before), f1 = _cqt.FrameOfSeconds(t + v.PostWindow.After), fi = -1;
        float best = 0;
        for (var f = f0; f <= f1; f++) { var mg = _cqt.Magnitude(b, f); if (mg > best || fi < 0) { best = mg; fi = f; } }
        return best > _cqt.Magnitude(b - 1, fi) && best > _cqt.Magnitude(b + 1, fi);
    }

    public ChartVerifyOutcome Verify(ExpectedSound sound)
    {
        var o = VerifyCore(sound);
        if (XCfg.Why && !o.Confirmed && sound.MidiNotes.Length > 0) Explain(sound);
        return o;
    }

    // What the CQT actually hears at a missed note: the strongest peaked bins above their
    // floor, 60 ms after the charted time, and the presence ratio the gate compares.
    void Explain(ExpectedSound sound)
    {
        var t = sound.TimeMs / 1000.0;
        var f = _cqt.FrameOfSeconds(t + 0.06);
        var peaks = new List<(double Midi, float Mag)>();
        for (var b = 1; b < _cqt.BinCount - 1; b++)
        {
            var m = _cqt.Magnitude(b, f);
            if (m > _cqt.Floor(b) && m >= _cqt.Magnitude(b - 1, f) && m >= _cqt.Magnitude(b + 1, f))
                peaks.Add((_cqt.FMinMidi + b / (double)_cqt.Step, m));
        }
        var top = peaks.OrderByDescending(p => p.Mag).Take(4).ToList();
        var max = top.Count > 0 ? top[0].Mag : 1f;
        float presence = 0;
        for (var g = _cqt.FrameOfSeconds(t - XCfg.Before); g <= _cqt.FrameOfSeconds(t + XCfg.After); g++)
            presence = Math.Max(presence, _cqt.FrameEnergy(g));
        var ratio = presence / Math.Max(1e-12f, _cqt.NoiseFloorEnergy());
        lock (Console.Out)
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"WHY {sound.TimeMs / 1000:0.00}s chart [{string.Join(' ', sound.MidiNotes)}] hears [{string.Join(' ', top.Select(p => $"{p.Midi:0.#}:{p.Mag / max:0.00}"))}] presence/floor {ratio:0.0}"));
    }

    ChartVerifyOutcome VerifyCore(ExpectedSound sound)
    {
        var notes = sound.MidiNotes;
        if (notes.Length == 0) return new ChartVerifyOutcome(false, 0f);
        var t = sound.TimeMs / 1000.0;
        var allowHarmonic = !sound.PalmMute && !sound.FullMute;
        var harmonic = sound.Harmonic || sound.PinchHarmonic;
        if (notes.Length == 1)
        {
            var below = notes[0] - 12;
            var ringing = XCfg.Ring && below >= 0 && sound.TimeMs - _credited[below] is > 0 and <= LiveCqtVerifier.RingOnMs;
            var hit = Tone(_v, notes[0], t, allowHarmonic, !ringing, harmonic);
            if (hit && notes[0] < 128) _credited[notes[0]] = sound.TimeMs;
            return new ChartVerifyOutcome(hit, 1f);
        }
        _tones.Clear(); _ok.Clear();
        foreach (var m in notes) _tones.Add(m);
        foreach (var m in _tones)
            if (Tone(_vChord, m, t, allowHarmonic, false, harmonic)) _ok.Add(m);
        var outcome = ChordQuality.Judge(_tones, _ok, XCfg.Coverage, XCfg.Quality);
        if (outcome.Confirmed) foreach (var m in _ok) if (m < 128) _credited[m] = sound.TimeMs;
        return outcome;
    }
}

static class Idle
{
    /// <summary>What an idle guitar input sounds like: pickup hiss plus mains hum with
    /// harmonics, slowly wandering in level — nothing played.</summary>
    public static float[] Make(int length, int sr, int seed)
    {
        var rng = new Random(seed);
        var buf = new float[length];
        double lp = 0;
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)sr;
            var wander = 1.0 + 0.3 * Math.Sin(2 * Math.PI * 0.05 * t);
            lp += 0.5 * ((rng.NextDouble() * 2 - 1) - lp);
            var hum = 0.0;
            for (var h = 1; h <= 6; h++) hum += Math.Sin(2 * Math.PI * 50 * h * t + h) / h;
            buf[i] = (float)((0.0008 * lp + 0.0012 * hum) * wander);
        }
        return buf;
    }
}


static class Polite
{
    public static int Apply()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        try { me.PriorityClass = System.Diagnostics.ProcessPriorityClass.Idle; } catch (Exception) { }
        // Already launched off cores 0-3 (e.g. `start /affinity FF0`): keep that set as it is.
        var current = (long)me.ProcessorAffinity;
        if ((current & 0xF) == 0 && current != 0)
        {
            var n = System.Numerics.BitOperations.PopCount((ulong)current);
            Console.Error.WriteLine($"detection-eval: the {n} cores this process was given (none of 0-3), idle priority");
            return n;
        }
        var cores = Environment.ProcessorCount;
        var use = Math.Max(1, Math.Min(cores / 2, cores - 4));
        if (cores > 4)
        {
            long mask = 0;
            for (var c = 4; c < 4 + use && c < 63; c++) mask |= 1L << c;
            try { me.ProcessorAffinity = (nint)mask; } catch (Exception) { }
        }
        Console.Error.WriteLine($"detection-eval: {use} of {cores} cores (from core 4), idle priority");
        return use;
    }

    public static int Dop(string requested, int allowed)
    {
        var n = int.TryParse(requested, out var d) && d > 0 ? d : allowed;
        return Math.Clamp(n, 1, allowed);
    }
}

/// <summary>
///     --techniques (#645): how often the contour judge rules a bend, a slide or a vibrato met,
///     not met or unknown. Guitar-TECHS real DI: <c>Bendings</c> and <c>Vibrato</c> are techniques
///     that were REALLY played (the hex MIDI writes a bend as a run of notes stepping up one
///     string, so each run is charted back as one bent note); <c>singlenotes</c> and
///     <c>scales</c> are plain notes charted three times with a technique the player did NOT play
///     (a full-step bend, a vibrato, a two-fret slide) — the false-accept side. With
///     <c>--charts</c>: real charts rendered by <see cref="TechniqueSynth" />, once with their
///     bends, slides and vibrato and once without.
/// </summary>
static class TechniqueEval
{
    sealed class Counts { public int Credited, Missed, Met, NotMet, Unknown; }

    static readonly SortedDictionary<string, Counts> Totals = new();

    static void Add(string key, ExpectedSound s)
    {
        lock (Totals)
        {
            if (!Totals.TryGetValue(key, out var c)) Totals[key] = c = new Counts();
            if (s.Verdict is not (LivePlayVerdict.Hit or LivePlayVerdict.HitWrongTechnique)) { c.Missed++; return; }
            c.Credited++;
            switch (s.ContourVerdict)
            {
                case TechniqueContourVerdict.Met: c.Met++; break;
                case TechniqueContourVerdict.NotMet: c.NotMet++; break;
                default: c.Unknown++; break;
            }
        }
    }

    static IEnumerable<string> Techniques(ExpectedSound s)
    {
        if ((s.TechniqueContour.Techniques & ContourTechniques.Bend) != 0) yield return "bend";
        if ((s.TechniqueContour.Techniques & ContourTechniques.Slide) != 0) yield return "slide";
        if ((s.TechniqueContour.Techniques & ContourTechniques.Vibrato) != 0) yield return "vibrato";
    }

    public static void Run(string gtRoot, string chartsDir, string filter, int dop, bool quick)
    {
        Runner.JudgeTechniques = true;
        if (chartsDir.Length > 0) RunCharts(chartsDir, filter, dop, quick);
        else RunGuitarTechs(gtRoot, filter, dop);

        Console.WriteLine();
        Console.WriteLine($"{"set|played?|technique",-40} {"credited",8} {"missed",7} {"met",7} {"not met",8} {"unknown",8}  full-hit");
        foreach (var (k, c) in Totals)
        {
            var full = c.Credited == 0 ? "n/a" : $"{100.0 * (c.Met + c.Unknown) / c.Credited:0.0}%";
            Console.WriteLine($"{k,-40} {c.Credited,8} {c.Missed,7} {Pct(c.Met, c.Credited),7} {Pct(c.NotMet, c.Credited),8} {Pct(c.Unknown, c.Credited),8}  {full}");
        }
    }

    static string Pct(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:0.0}%";

    // ── Guitar-TECHS ──────────────────────────────────────────────────────

    static void RunGuitarTechs(string gtRoot, string filter, int dop)
    {
        var cases = new List<(string Name, string Kind, string Wav, string Mid)>();
        foreach (var set in Directory.GetDirectories(gtRoot).OrderBy(d => d))
        {
            var setName = Path.GetFileName(set);
            var diDir = Path.Combine(set, "audio", "directinput");
            if (!Directory.Exists(diDir)) continue;
            foreach (var wav in Directory.GetFiles(diDir, "*.wav").OrderBy(f => f))
            {
                var stem = Path.GetFileNameWithoutExtension(wav).Replace("directinput_", "");
                var mid = Path.Combine(set, "midi", "midi_" + stem + ".mid");
                var kind = setName.EndsWith("techniques") ? stem switch { "Bendings" => "bend", "Vibrato" => "vibrato", _ => "" }
                    : setName.EndsWith("singlenotes") || setName.EndsWith("scales") ? "control" : "";
                var name = setName + "/" + stem;
                if (kind.Length == 0 || !File.Exists(mid)) continue;
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                cases.Add((name, kind, wav, mid));
            }
        }

        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = dop }, c =>
        {
            var (audio, sr) = Wav.ReadMono(c.Wav);
            // Relaxed clean: a bend's in-between steps are short, and P2's bends are quiet.
            var open = ChartBuild.Open;
            var notes = Smf.ReadGuitarTechs(c.Mid).Where(n => n.OffMs - n.OnMs >= 30 && n.Velocity >= 8 &&
                n.StringIndex is >= 0 and < 6 && n.Midi - open[n.StringIndex] is >= 0 and <= 24).ToList();
            var lag = Align.EstimateLagMs(audio, sr, notes.Where(n => n.OffMs - n.OnMs >= 60).ToList());
            var shifted = notes.Select(n => n with { OnMs = n.OnMs + lag, OffMs = n.OffMs + lag }).ToList();
            var set = c.Name.Split('/')[0];
            var charts = c.Kind switch
            {
                "bend" => new List<(string, Chart)> { ("played", Chains(shifted, maxStep: 3, below: false, bend: true)) },
                "vibrato" => [("played", Chains(shifted, maxStep: 1, below: true, bend: false))],
                _ => [("unplayed", Controls(shifted, "bend")), ("unplayed", Controls(shifted, "vibrato")),
                    ("unplayed", Controls(shifted, "slide"))]
            };
            foreach (var (played, chart) in charts)
            {
                var sounds = Runner.Run(audio, sr, chart, "cqt");
                foreach (var s in sounds)
                    foreach (var t in Techniques(s)) Add($"{set}|{played}|{t}", s);
            }
            lock (Totals) Console.WriteLine($"{c.Name}: lag {lag:0} ms, {charts.Count} chart(s)");
        });
    }

    /// <summary>A run of notes on one string that follow each other without a gap is ONE
    /// played note whose pitch moved: a bend (steps up from the first note) or a vibrato wide
    /// enough to cross a semitone (steps either way; the fretted note is the lowest).</summary>
    static Chart Chains(List<MidiNote> notes, int maxStep, bool below, bool bend)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        foreach (var str in notes.GroupBy(n => n.StringIndex))
        {
            var run = str.OrderBy(n => n.OnMs).ToList();
            var i = 0;
            while (i < run.Count)
            {
                var chain = new List<MidiNote> { run[i] };
                var j = i + 1;
                while (j < run.Count && run[j].OnMs - chain[^1].OffMs <= 40 && run[j].OnMs > chain[^1].OnMs &&
                       Math.Abs(run[j].Midi - run[i].Midi) <= maxStep && (below || run[j].Midi >= run[i].Midi))
                    chain.Add(run[j++]);
                i = j;
                var baseMidi = chain.Min(n => n.Midi);
                var start = chain[0].OnMs;
                var end = chain.Max(n => n.OffMs);
                var note = new GuitarNote
                {
                    Position = ChartBuild.Pos(start), StringIndex = chain[0].StringIndex,
                    Fret = baseMidi - ChartBuild.Open[chain[0].StringIndex], Sustain = ChartBuild.Dur(end - start)
                };
                if (bend && chain.Any(n => n.Midi > baseMidi))
                    foreach (var n in chain.Skip(1)) note.BendValues.Add(new BendValue(ChartBuild.Pos(n.OnMs), n.Midi - baseMidi));
                if (!bend) note.Vibrato = true;
                level.Notes.Add(note);
            }
        }
        return WithBeats(arr, level, notes);
    }

    /// <summary>Plain notes charted with a technique the player did not play.</summary>
    static Chart Controls(List<MidiNote> notes, string technique)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        foreach (var n in notes)
        {
            var fret = n.Midi - ChartBuild.Open[n.StringIndex];
            var dur = n.OffMs - n.OnMs;
            var note = new GuitarNote { Position = ChartBuild.Pos(n.OnMs), StringIndex = n.StringIndex, Fret = fret, Sustain = ChartBuild.Dur(dur) };
            switch (technique)
            {
                case "bend":
                    note.BendValues.Add(new BendValue(ChartBuild.Pos(n.OnMs + Math.Min(250, dur / 2)), 2));
                    break;
                case "vibrato":
                    note.Vibrato = true;
                    break;
                default:
                    // A slide over at most 700 ms, two frets up (down near the top).
                    note.SlideTo = fret <= 22 ? fret + 2 : fret - 2;
                    note.Sustain = ChartBuild.Dur(Math.Min(700, dur));
                    break;
            }
            level.Notes.Add(note);
        }
        return WithBeats(arr, level, notes);
    }

    static Chart WithBeats(Arrangement arr, ChartLevel level, List<MidiNote> notes)
    {
        var lastMs = notes.Count == 0 ? 0 : notes.Max(n => n.OffMs);
        var beats = Enumerable.Range(0, (int)(lastMs / 500) + 16)
            .Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 }).ToList();
        return new Chart(arr, level, beats);
    }

    // ── rendered charts ──────────────────────────────────────────────────

    static void RunCharts(string dir, string filter, int dop, bool quick)
    {
        var files = Directory.GetFiles(dir, "*RS2.xml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".debug.", StringComparison.OrdinalIgnoreCase))
            .Where(f => filter.Length == 0 || f.Replace(Path.DirectorySeparatorChar, '/').Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f).ToList();
        var seen = new HashSet<string>();
        files = files.Where(f => seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f))))).ToList();
        if (quick) files = files.Where((_, i) => i % 8 == 0).ToList();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = dop }, file =>
        {
            ChartProject project;
            try { project = new XmlChartSerializer().Load(file); }
            catch (Exception) { return; }
            var arr = project.Arrangements.FirstOrDefault();
            if (arr == null || arr.Levels.Count == 0 || project.Beats.Count == 0) return;
            var beats = project.Beats.ToList();
            var probe = new LivePlaySessionService();
            probe.Start(arr, null, beats, new LivePlaySessionOptions { CountInBeats = 0 });
            var expected = probe.Matcher!.Expected.ToList();
            if (!expected.Any(s => !s.TechniqueContour.IsEmpty)) return;
            const int sr = 48000;
            var bass = arr.ArrangementType == ArrangementType.Bass;
            foreach (var played in new[] { true, false })
            {
                var audio = TechniqueSynth.Render(expected, sr, bass, played);
                var sounds = Runner.Run(audio, sr, new Chart(arr, null!, beats), "cqt");
                foreach (var s in sounds)
                    foreach (var t in Techniques(s)) Add($"charts|{(played ? "played" : "unplayed")}|{t}", s);
            }
            lock (Totals) Console.WriteLine(Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file));
        });
    }
}

/// <summary>The rendered-chart tone for --techniques: one additive tone per charted pitch whose
/// pitch follows the chart's own bend (reaching its step at the charted peak), slide (arriving
/// within its window) and vibrato (5.5 Hz, ±30 cents) — or, with <c>played</c> off, none of
/// them. Only single notes move. Synthetic: a relative check, never the shipping number.</summary>
static class TechniqueSynth
{
    public static float[] Render(List<ExpectedSound> sounds, int sr, bool bass, bool played)
    {
        var buf = new float[(int)((sounds.Max(s => s.TimeMs) + 3000) / 1000.0 * sr)];
        var ordered = sounds.OrderBy(s => s.TimeMs).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var j = i + 1;
            while (j < ordered.Count && ordered[j].TimeMs - s.TimeMs < 2) j++;
            var nextMs = j < ordered.Count ? ordered[j].TimeMs : s.TimeMs + 2500;
            var durMs = Math.Clamp(nextMs - s.TimeMs - 5, 60, 3000);
            foreach (var m in s.MidiNotes)
                Tone(buf, sr, s, m, durMs, bass, played && s.MidiNotes.Length == 1 ? s.TechniqueContour : default);
        }
        return buf;
    }

    static void Tone(float[] buf, int sr, ExpectedSound s, int midi, double durMs, bool bass, TechniqueContourExpectation c)
    {
        var f0 = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
        var start = (int)(s.TimeMs / 1000 * sr);
        var len = (int)(durMs / 1000 * sr);
        double phase = 0;
        var decay = bass ? 1.8 : 1.2;
        var gain = 0.3 / Math.Sqrt(Math.Max(1, s.MidiNotes.Length));
        for (var i = 0; i < len && start + i < buf.Length; i++)
        {
            var t = i * 1000.0 / sr;
            var cents = 0.0;
            if ((c.Techniques & ContourTechniques.Bend) != 0)
                cents += c.BendSemis * 100 * Math.Clamp(t / Math.Max(60, c.BendPeakAfterMs), 0, 1);
            if ((c.Techniques & ContourTechniques.Slide) != 0 && c.WindowAfterMs > 0)
                cents += c.SlideSemis * 100 * Math.Clamp((t - 0.2 * c.WindowAfterMs) / (0.7 * c.WindowAfterMs), 0, 1);
            if ((c.Techniques & ContourTechniques.Vibrato) != 0 && t > 80)
                cents += 30 * Math.Sin(2 * Math.PI * 5.5 * (t - 80) / 1000);
            phase += 2 * Math.PI * f0 * Math.Pow(2, cents / 1200) / sr;
            var env = Math.Exp(-t / 1000 / decay) * Math.Min(1, t / 3) * Math.Min(1, (len - i) / (0.005 * sr));
            buf[start + i] += (float)(gain * env * (0.55 * Math.Sin(phase) + 0.3 * Math.Sin(2 * phase) + 0.15 * Math.Sin(3 * phase)));
        }
    }
}
