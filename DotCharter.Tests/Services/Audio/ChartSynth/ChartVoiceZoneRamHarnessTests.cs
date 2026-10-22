using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The chart synth's RAM with a REAL chart loaded (#417): every voice
///     selection against real arrangements of the TestData corpus, on the JUCE
///     null device, through the same order of calls the app makes (instrument,
///     voice, notes, enable). It is the MEASUREMENT behind the issue's
///     before/after table, not a gate: it needs the real voice packs (named by
///     <c>DOTCHARTER_VOICE_RAM_PACKS</c>, a directory of <c>*.dcvp</c>) and the
///     corpus, and SKIPS without either. Run it once on the old DLL and once on
///     the new one; each run appends a Markdown table to
///     <c>%TEMP%/dotcharter_voice_ram.md</c>.
/// </summary>
[Collection("NativeEngine")]
public sealed class ChartVoiceZoneRamHarnessTests : IDisposable
{
    private const string PacksVar = "DOTCHARTER_VOICE_RAM_PACKS";
    private const string VoiceDirVar = "DOTCHARTER_VOICE_DIR";
    private const double MiB = 1024.0 * 1024.0;
    private readonly string? _outerVoiceDir = Environment.GetEnvironmentVariable(VoiceDirVar);

    public void Dispose() => Environment.SetEnvironmentVariable(VoiceDirVar, _outerVoiceDir);

    private static long Private()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64;
    }

    /// <summary>The level the editor plays: the one with the most notes.</summary>
    private static ChartUploadCase? CaseFor(string song, ChartProject project, Arrangement arrangement)
    {
        var level = arrangement.Levels.OrderByDescending(l => l.Notes.Count).FirstOrDefault();
        if (level == null || level.Notes.Count < 100) return null;
        var upload = ChartNoteEventBuilder.Build(arrangement, level.Notes, level.Chords, project.Beats, level.HandShapes,
            project.Bpm, project.SynthPlayStyle);
        return upload.Notes.Length == 0 ? null : new ChartUploadCase($"{song} / {arrangement.Name}", upload);
    }

    private sealed record ChartUploadCase(string Name, ChartNoteUpload Upload);

    /// <summary>TestData/RsCharter, found by walking up (a worktree inside the repo finds the main checkout's).</summary>
    private static string? FindCorpus()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "RsCharter");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    [Fact]
    public void RealCharts_HoldOnlyTheZonesTheyPlay()
    {
        var packs = Environment.GetEnvironmentVariable(PacksVar);
        Assert.SkipWhen(string.IsNullOrEmpty(packs) || !File.Exists(Path.Combine(packs, "strat-bridge.dcvp")),
            $"{PacksVar} does not name a directory with the real voice packs");
        var root = FindCorpus();
        Assert.SkipWhen(root == null, "No RsCharter corpus (TestData/ is git-ignored).");
        Environment.SetEnvironmentVariable(VoiceDirVar, packs);

        var serializer = new RscpChartSerializer();
        var guitar = new List<ChartUploadCase>();
        var bass = new List<ChartUploadCase>();
        foreach (var file in Directory.EnumerateFiles(root!, "project.rscp", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (guitar.Count >= 3 && bass.Count >= 3) break;
            if (file.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            var song = Path.GetFileName(Path.GetDirectoryName(file)) ?? file;
            var project = serializer.Load(file);
            // One guitar part and one bass part per song, so the charts differ.
            bool tookGuitar = false, tookBass = false;
            foreach (var a in project.Arrangements)
            {
                var isBass = a.ArrangementType == ArrangementType.Bass;
                var list = isBass ? bass : guitar;
                if (list.Count >= 3 || a.ArrangementType == ArrangementType.Vocal || (isBass ? tookBass : tookGuitar)) continue;
                if (CaseFor(song, project, a) is not { } c) continue;
                list.Add(c);
                if (isBass) tookBass = true;
                else tookGuitar = true;
            }
        }

        Assert.NotEmpty(guitar);
        Assert.NotEmpty(bass);

        (string Name, JuceChartVoice Voice, JuceChartInstrument Inst, List<ChartUploadCase> Charts)[] selections =
        [
            ("Strat HSS bridge (strat-bridge + arts)", JuceChartVoice.Guitar,
                new JuceChartInstrument(JuceChartInstrumentModel.StratHss, JuceChartPickup.Bridge, false), guitar),
            ("Strat SSS neck (strat-neck + arts)", JuceChartVoice.Guitar,
                new JuceChartInstrument(JuceChartInstrumentModel.StratSss, JuceChartPickup.Neck, false), guitar),
            ("Gretsch neck+bridge (guitar + arts)", JuceChartVoice.Guitar,
                new JuceChartInstrument(JuceChartInstrumentModel.SemiHollow, JuceChartPickup.NeckBridge, false), guitar),
            ("Jazz bass both (bass)", JuceChartVoice.Bass,
                new JuceChartInstrument(JuceChartInstrumentModel.JazzBass, JuceChartPickup.NeckBridge, false), bass),
        ];

        using var engine = new JuceAudioEngine(new JuceEngineConfig
        {
            PreferredDriver = AudioDriver.Wasapi,
            OutputTypeName = "Null",
            InputTypeName = "Null",
            SampleRate = 48_000,
            BufferFrames = 480,
            OutputChannels = 2,
            InputChannels = 1,
            EnableNullDevice = true,
            EnableConsoleLog = false,
        });
        var report = new StringBuilder();
        var label = Environment.GetEnvironmentVariable("DOTCHARTER_VOICE_RAM_LABEL") ?? "";
        report.AppendLine(CultureInfo.InvariantCulture, $"### {DateTime.Now:yyyy-MM-dd HH:mm} {label} — juce_sharp {JuceAudioEngine.NativeVersion}");
        report.AppendLine("| Selection | Chart | Notes | Pack held (MiB) | Private delta (MiB) | Enable (ms) |");
        report.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var (name, voice, inst, charts) in selections)
        {
            foreach (var chart in charts)
            {
                var before = Private();
                Assert.True(engine.TrySetChartInstrument(0, inst));
                Assert.True(engine.TrySetChartVoice(0, voice));
                engine.SetChartNotes(0, chart.Upload.Notes, chart.Upload.Bends);
                var sw = Stopwatch.StartNew();
                engine.SetChartSourceEnabled(0, true);
                var enableMs = sw.ElapsedMilliseconds;
                Assert.True(engine.TryGetChartSourceStatus(0, out var st));
                Assert.True(st.IsSampled, $"{name}: the real pack did not load");
                var on = Private();
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {name} | {chart.Name} | {chart.Upload.Notes.Length} | {st.PackBytes / MiB:F1} | {(on - before) / MiB:F1} | {enableMs} |");
                engine.SetChartSourceEnabled(0, false);
                var deadline = Environment.TickCount64 + 60_000;   // a deadlock detector, not a budget
                while (JuceAudioEngine.ChartVoicePackBytes != 0 && Environment.TickCount64 < deadline) Thread.Sleep(10);
                Assert.Equal(0, JuceAudioEngine.ChartVoicePackBytes);
                engine.SetChartNotes(0, [], []);
            }
        }

        var text = report.ToString();
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "dotcharter_voice_ram.md"), text + Environment.NewLine);
    }
}
