using System.Globalization;
using System.Text.Json;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tools.RigDataset;
using DotCharter.Tools.RigDataset.Separate;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.Dataset;

/// <summary>
///     Stage B — mix into a bed, separate, keep the guitar stem under the ORIGINAL
///     label — driven end to end over a FAKE separator.
///     <para>
///         The real separator is a 110 MB ONNX model and seconds of CPU per clip, so
///         a test that used it would test the model rather than the stage. Everything
///         this class asserts is the stage's own logic: which rows are selected, that
///         a resumed pass does not redo work, that the guitar-to-mix level is drawn
///         in the window and RECORDED, and that a separated row still carries the rig
///         it was rendered from.
///     </para>
///     <para>
///         No engine, no audio device, no collection: the fake separator is pure
///         managed code and the WAV IO is <c>JuceSharp.AudioIo</c>, which does not
///         open one.
///     </para>
/// </summary>
public sealed class RigSeparateStageTests
{
    private const int Rate = 48_000;
    private const int Frames = 4_800; // 0.1 s — this proves the stage, not the model
    private const int Seed = 20260913;

    /// <summary>Counts how many times it was asked, and returns a signal that is
    /// obviously NOT the input, so "did the separated stem actually get stored?" is
    /// answerable from the WAV.</summary>
    private sealed class FakeSeparator : IGuitarSeparator
    {
        private readonly bool _fail;
        public int Calls;
        public readonly List<float> InputRms = [];

        public FakeSeparator(bool fail = false) => _fail = fail;

        public string Backend => "fake";

        public Task<float[]?> IsolateGuitarAsync(float[] mono, int sampleRate, CancellationToken ct)
        {
            Calls++;
            double acc = 0;
            foreach (var v in mono) acc += (double)v * v;
            InputRms.Add((float)Math.Sqrt(acc / Math.Max(1, mono.Length)));
            if (_fail) return Task.FromResult<float[]?>(null);

            var outp = new float[mono.Length];
            for (var i = 0; i < outp.Length; i++) outp[i] = 0.2f * MathF.Sin(i * 0.031f);
            return Task.FromResult<float[]?>(outp);
        }
    }

    // ------------------------------------------------------------------ setup

    private static string MakeCorpus(string root, int clips)
    {
        var manifest = new List<string>(clips);
        for (var i = 0; i < clips; i++)
        {
            var rel = DatasetManifest.ClipPathFor(i);
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var x = new float[Frames];
            for (var n = 0; n < Frames; n++) x[n] = 0.3f * MathF.Sin(n * (0.01f + i * 0.001f));
            using (var w = new WavWriter(full, Rate, 1, WavSampleFormat.Pcm16)) w.Write(x);

            var row = new RigDatasetRow(rel, "di/fake/tone.wav",
                new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float> { ["drive"] = 0.5f })]),
                ["norm:-18.0dBFS", "tilt:+0.0dB"], Rate, Frames / (float)Rate, 1234 + i);
            manifest.Add(JsonSerializer.Serialize(row));
        }

        var path = Path.Combine(root, DatasetManifest.FileName);
        File.WriteAllLines(path, manifest);
        return path;
    }

    private static string MakeBeds(string root)
    {
        var beds = Path.Combine(root, "beds");
        Directory.CreateDirectory(beds);
        for (var b = 0; b < 3; b++)
        {
            var x = new float[Frames * 4];
            for (var n = 0; n < x.Length; n++) x[n] = 0.25f * MathF.Sin(n * (0.004f + b * 0.002f));
            using var w = new WavWriter(Path.Combine(beds, $"bed_{b:D3}.wav"), Rate, 1, WavSampleFormat.Pcm16);
            w.Write(x);
        }

        return beds;
    }

    private static SeparateOptions Options(string root, double fraction) => new()
    {
        Root = root,
        BedsRoot = Path.Combine(root, "beds"),
        StemModelPath = null,
        Fraction = fraction,
        Seed = Seed,
        Threads = 1,
        ProgressEvery = 1000
    };

    private static async Task<(int Code, FakeSeparator Fake)> RunAsync(string root, double fraction,
        FakeSeparator? fake = null)
    {
        var f = fake ?? new FakeSeparator();
        var code = await new SeparateCommand(Options(root, fraction), (_, _) => f)
            .RunAsync(CancellationToken.None);
        DatasetManifest.Merge(root);
        return (code, f);
    }

    private static Dictionary<int, RigDatasetRow> ReadMerged(string root)
        => SeparateCommand.ReadRows(Path.Combine(root, DatasetManifest.FileName));

    // ------------------------------------------------------------------ tests

    [Fact]
    public void SelectionIsDeterministic_AndRaisingTheFractionOnlyEverAddsRows()
    {
        var at20 = Enumerable.Range(0, 2_000).Where(i => SeparateCommand.IsSelected(Seed, i, 0.20)).ToHashSet();
        var at40 = Enumerable.Range(0, 2_000).Where(i => SeparateCommand.IsSelected(Seed, i, 0.40)).ToHashSet();

        Assert.True(at20.IsSubsetOf(at40),
            $"{at20.Except(at40).Count()} rows selected at 20 % are NOT selected at 40 % — raising the "
            + "fraction would re-separate a different set and the corpus would stop being reproducible.");

        // …and the fraction means what it says, within sampling noise on 2,000 draws.
        Assert.InRange(at40.Count / 2_000d, 0.36, 0.44);
        Assert.InRange(at20.Count / 2_000d, 0.17, 0.23);

        Assert.DoesNotContain(Enumerable.Range(0, 500), i => SeparateCommand.IsSelected(Seed, i, 0));
        Assert.Equal(500, Enumerable.Range(0, 500).Count(i => SeparateCommand.IsSelected(Seed, i, 1)));
    }

    [Fact]
    public async Task EverySeparatedRow_KeepsItsRig_AndRecordsTheBedTheLevelAndTheModel()
    {
        var root = NewRoot();
        MakeCorpus(root, 60);
        MakeBeds(root);

        var (code, fake) = await RunAsync(root, 1.0);
        Assert.Equal(0, code);
        Assert.Equal(60, fake.Calls);

        foreach (var (index, row) in ReadMerged(root))
        {
            Assert.StartsWith(SeparateCommand.SeparatedDir + "/", row.ClipPath, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, row.ClipPath.Replace('/', Path.DirectorySeparatorChar))),
                $"row {index} points at {row.ClipPath}, which was never written.");

            // The LABEL is untouched. This is the whole premise of the stage.
            Assert.Single(row.Rig.Modules);
            Assert.Equal("overdrive", row.Rig.Modules[0].ModuleTypeId);
            Assert.Equal(0.5f, row.Rig.Modules[0].Params["drive"]);

            var mix = Assert.Single(row.Augmentations, a => a.StartsWith("mix:", StringComparison.Ordinal));
            var db = float.Parse(mix["mix:".Length..^2], CultureInfo.InvariantCulture);
            Assert.InRange(db, -12f, 3f);

            Assert.Contains(row.Augmentations, a => a.StartsWith("bed:", StringComparison.Ordinal));
            Assert.Contains("separated:htdemucs_6s", row.Augmentations);
            // The render's own tags survive: a separated row is an ADDITION to the
            // history, never a replacement of it.
            Assert.Contains("tilt:+0.0dB", row.Augmentations);
        }
    }

    [Fact]
    public async Task TheGuitarToMixLevelSpansItsWholeWindow_AndTheTagIsCultureInvariant()
    {
        var root = NewRoot();
        MakeCorpus(root, 400);
        MakeBeds(root);
        await RunAsync(root, 1.0);

        var levels = ReadMerged(root).Values
            .SelectMany(r => r.Augmentations)
            .Where(a => a.StartsWith("mix:", StringComparison.Ordinal))
            .Select(a => float.Parse(a["mix:".Length..^2], CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(400, levels.Length);
        Assert.All(levels, l => Assert.InRange(l, -12f, 3f));
        Assert.True(levels.Min() < -9f && levels.Max() > 0f,
            $"the guitar-to-mix draw only reached [{levels.Min():F1}, {levels.Max():F1}] dB over 400 rows — "
            + "it is not spanning the −12…+3 window the stage claims.");

        // On a German box `$"{db:F1}"` writes "-1,2"; a manifest whose tags depend on
        // the renderer's locale is one two machines cannot compare. Same bug as the
        // tilt tag, same guard.
        Assert.All(ReadMerged(root).Values.SelectMany(r => r.Augmentations),
            a => Assert.DoesNotContain(',', a));
    }

    [Fact]
    public async Task AResumedPass_DoesNotReSeparateARowItAlreadyDid()
    {
        var root = NewRoot();
        MakeCorpus(root, 120);
        MakeBeds(root);

        var first = new FakeSeparator();
        await RunAsync(root, 0.40, first);
        var afterFirst = ReadMerged(root);
        var separatedFirst = afterFirst.Count(kv => Augmentation.IsSeparated(kv.Value.Augmentations));
        Assert.True(first.Calls > 0 && first.Calls == separatedFirst);

        // The SECOND pass, same options, on the same corpus.
        var second = new FakeSeparator();
        await RunAsync(root, 0.40, second);
        Assert.True(second.Calls == 0,
            $"a resumed pass separated {second.Calls} rows that were already done — over 40,000 rows that "
            + "is hours of GPU redone, and the clip is overwritten with a stem OF a stem.");

        var afterSecond = ReadMerged(root);
        Assert.Equal(afterFirst.Count, afterSecond.Count);
        foreach (var (i, row) in afterFirst)
            Assert.Equal(row.ClipPath, afterSecond[i].ClipPath);
    }

    [Fact]
    public async Task RaisingTheFractionSeparatesOnlyTheNewRows()
    {
        var root = NewRoot();
        MakeCorpus(root, 300);
        MakeBeds(root);

        var low = new FakeSeparator();
        await RunAsync(root, 0.20, low);
        var high = new FakeSeparator();
        await RunAsync(root, 0.40, high);

        var expectedNew = Enumerable.Range(0, 300)
            .Count(i => SeparateCommand.IsSelected(Seed, i, 0.40)
                        && !SeparateCommand.IsSelected(Seed, i, 0.20));
        Assert.Equal(expectedNew, high.Calls);
        Assert.Equal(low.Calls + high.Calls,
            ReadMerged(root).Count(kv => Augmentation.IsSeparated(kv.Value.Augmentations)));
    }

    [Fact]
    public async Task AFailedSeparation_IsRecordedAndNeverRetriedForEver()
    {
        var root = NewRoot();
        MakeCorpus(root, 60);
        MakeBeds(root);

        var failing = new FakeSeparator(fail: true);
        await RunAsync(root, 1.0, failing);
        Assert.Equal(60, failing.Calls);

        var rows = ReadMerged(root);
        Assert.All(rows.Values, r => Assert.Contains("separated:failed", r.Augmentations));
        // A failed row keeps the CLEAN clip — it is still a usable training pair.
        Assert.All(rows.Values, r => Assert.StartsWith("clips/", r.ClipPath, StringComparison.Ordinal));

        var again = new FakeSeparator(fail: true);
        await RunAsync(root, 1.0, again);
        Assert.True(again.Calls == 0,
            $"a row whose separation FAILED was retried {again.Calls} times — a corpus with one "
            + "unseparable clip would then never finish a pass.");
    }

    [Fact]
    public async Task WithNoBeds_ItStillRuns_AndSaysSoOnEveryRow()
    {
        var root = NewRoot();
        MakeCorpus(root, 20);
        // No MakeBeds: this is the state the v1 corpus was actually in.

        var options = new SeparateOptions
        {
            Root = root, BedsRoot = null, Fraction = 1.0, Seed = Seed, Threads = 1, ProgressEvery = 1000
        };
        var fake = new FakeSeparator();
        Assert.Equal(0, await new SeparateCommand(options, (_, _) => fake).RunAsync(CancellationToken.None));
        DatasetManifest.Merge(root);

        Assert.All(ReadMerged(root).Values, r =>
        {
            Assert.Contains("separated:htdemucs_6s:no-bed", r.Augmentations);
            Assert.DoesNotContain(r.Augmentations, a => a.StartsWith("bed:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task TheStopFile_EndsThePassCleanlyWithTheManifestIntact()
    {
        var root = NewRoot();
        MakeCorpus(root, 40);
        MakeBeds(root);
        var stop = Path.Combine(root, "STOP");
        File.WriteAllText(stop, "");

        var fake = new FakeSeparator();
        var options = new SeparateOptions
        {
            Root = root, BedsRoot = Path.Combine(root, "beds"), Fraction = 1.0, Seed = Seed,
            Threads = 1, ProgressEvery = 1000, StopFile = stop
        };
        Assert.Equal(0, await new SeparateCommand(options, (_, _) => fake).RunAsync(CancellationToken.None));
        Assert.Equal(0, fake.Calls);

        DatasetManifest.Merge(root);
        Assert.Equal(40, ReadMerged(root).Count);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-sep-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);
        return root;
    }
}
