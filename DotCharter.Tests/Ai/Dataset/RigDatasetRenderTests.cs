using System.Text.Json;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tools.RigDataset;
using JuceSharp;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.Dataset;

/// <summary>
///     A real 20-clip render through the real engine, into a temp directory.
///     <para>
///         <b>Requires the native engine's <c>Null</c> device</b>
///         (<c>DCA_ENABLE_NULL_DEVICE=1</c>) and SKIPS without it — deliberately, and
///         the tool's own fallback to the DEFAULT device is switched OFF here
///         (<c>RequireNullDevice</c>). A test host that dies holding the real sound
///         card holds it EXCLUSIVELY until someone kills the process; one survived 32
///         hours that way. A skip is cheap; that is not.
///     </para>
/// </summary>
// NativeEngine, not a collection of its own: this class opens a device-less engine,
// and DCA_ENABLE_NULL_DEVICE is process-wide, so running it beside the serialized
// native collection is how a sibling silently gets — or loses — the null device.
// "RigDatasetRender" was declared by no CollectionDefinition at all, so it serialized
// nothing (NativeEngineCollectionLintTests caught it).
[Collection("NativeEngine")]
public sealed class RigDatasetRenderTests
{
    private const int Clips = 20;
    private const int Rate = 48_000;
    private const float Seconds = 1f; // a fifth of a real clip: this proves the pipe, not the corpus

    /// <summary>A synthetic DI pool — a plucked-ish decaying tone per file, so the
    /// silence/level rejects do not fire and the tone has harmonics for a drive to
    /// bite on. Built here rather than read from TestData so the test runs in a
    /// worktree that has no corpus.</summary>
    private static string MakeDiPool(string root, int files = 6)
    {
        var di = Path.Combine(root, "di", "synthetic");
        Directory.CreateDirectory(di);
        var frames = (int)(Seconds * Rate) * 3;
        for (var f = 0; f < files; f++)
        {
            var x = new float[frames];
            var hz = 82.4 * Math.Pow(2, f / 12.0);
            for (var i = 0; i < frames; i++)
            {
                var t = i / (double)Rate;
                var env = Math.Exp(-1.2 * (t % 1.0));
                x[i] = (float)(0.25 * env * (Math.Sin(2 * Math.PI * hz * t)
                                              + 0.5 * Math.Sin(4 * Math.PI * hz * t)
                                              + 0.25 * Math.Sin(6 * Math.PI * hz * t)));
            }

            using var w = new WavWriter(Path.Combine(di, $"tone{f}.wav"), Rate, 1, WavSampleFormat.Pcm16);
            w.Write(x);
        }

        return Path.Combine(root, "di");
    }

    private static RenderOptions Options(string root, string diRoot, int seed) => new()
    {
        OutRoot = root,
        DiRoot = diRoot,
        Clips = Clips,
        Seed = seed,
        Threads = 1,
        Seconds = Seconds,
        SampleRate = Rate,
        BleedFraction = 0,
        SeparateFraction = 0,
        ProgressEvery = 1000,
        RequireNullDevice = true
    };

    private static async Task<string[]> RenderAsync(string root, int seed)
    {
        var di = MakeDiPool(root);
        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        try
        {
            using var probe = RenderCommand.OpenDevicelessEngine(Rate, requireNull: true);
        }
        catch (JuceAudioException ex)
        {
            Assert.Skip($"No device-less JUCE engine on this machine ({ex.Message}); "
                        + "the dataset render cannot run without one.");
        }
        catch (DllNotFoundException ex)
        {
            Assert.Skip($"juce_sharp.dll is not next to the test binary ({ex.Message}).");
        }

        var code = await new RenderCommand(Options(root, di, seed)).RunAsync(CancellationToken.None);
        Assert.Equal(0, code);
        return File.ReadAllLines(Path.Combine(root, DatasetManifest.FileName));
    }

    private static RigDatasetRow Parse(string line) =>
        JsonSerializer.Deserialize<RigDatasetRow>(line)
        ?? throw new InvalidOperationException("a manifest line did not deserialize");

    // --------------------------------------------------------------------------

    [Fact]
    public async Task ATwentyClipRender_ProducesValidRowsWhoseRigsValidateAndWhoseAudioIsFiniteAndLevelled()
    {
        var root = Directory.CreateTempSubdirectory("rigdataset-render").FullName;
        try
        {
            var lines = await RenderAsync(root, seed: 31337);
            Assert.True(lines.Length >= Clips * 0.8,
                $"only {lines.Length}/{Clips} clips survived the reject rules — the render is "
                + "throwing away most of what it draws.");

            var known = DcRigTargetCatalog.ModuleTypeIds.ToHashSet(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var row = Parse(line);

                // --- the row ---
                Assert.Equal(Rate, row.SampleRate);
                Assert.Equal(Seconds, row.Seconds);
                Assert.NotEmpty(row.Augmentations);
                Assert.StartsWith("di/", row.DiPath, StringComparison.Ordinal);
                Assert.StartsWith("clips/", row.ClipPath, StringComparison.Ordinal);

                // --- the rig validates against the catalogue ---
                Assert.NotEmpty(row.Rig.Modules);
                foreach (var mod in row.Rig.Modules)
                {
                    Assert.Contains(mod.ModuleTypeId, known);
                    foreach (var (paramId, value) in mod.Params)
                    {
                        Assert.True(DcRigTargetCatalog.TryGetParam(mod.ModuleTypeId, paramId, out var spec),
                            $"{mod.ModuleTypeId}.{paramId} is not a knob the catalogue has");
                        Assert.InRange(value, spec.Min, spec.Max);
                    }
                }

                // --- the audio ---
                var wav = Path.Combine(root, row.ClipPath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(wav), $"{row.ClipPath} is in the manifest and not on disk");
                var audio = WavIo.Read(wav);
                Assert.Equal(1, audio.Channels);
                Assert.Equal(Rate, audio.SampleRate);
                Assert.Equal((int)(Seconds * Rate), audio.FrameCount);
                Assert.All(audio.Interleaved, v => Assert.True(float.IsFinite(v)));

                var rmsDb = Augmentation.Db(Augmentation.Rms(audio.Interleaved));
                Assert.InRange(rmsDb, Augmentation.TargetRmsDbfs - 6f, Augmentation.TargetRmsDbfs + 6f);
                Assert.True(Augmentation.Peak(audio.Interleaved) <= 1f,
                    "a rendered clip is over full scale — 16-bit PCM would have wrapped it");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheSameSeed_ReproducesTheManifestByteForByte()
    {
        var a = Directory.CreateTempSubdirectory("rigdataset-repro-a").FullName;
        var b = Directory.CreateTempSubdirectory("rigdataset-repro-b").FullName;
        try
        {
            var first = await RenderAsync(a, seed: 4242);
            var second = await RenderAsync(b, seed: 4242);
            Assert.Equal(first.Length, second.Length);
            for (var i = 0; i < first.Length; i++)
                Assert.Equal(first[i], second[i]);
        }
        finally
        {
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public async Task ARenderIsResumable_AndAResumedRunMatchesAnUninterruptedOne()
    {
        var whole = Directory.CreateTempSubdirectory("rigdataset-whole").FullName;
        var part = Directory.CreateTempSubdirectory("rigdataset-part").FullName;
        try
        {
            var reference = await RenderAsync(whole, seed: 777);

            // Render half, then finish. The second pass must skip what the first did
            // and must not renumber anything: the INDEX is part of the seed.
            var di = MakeDiPool(part);
            var half = Options(part, di, 777);
            await new RenderCommand(new RenderOptions
            {
                OutRoot = part, DiRoot = di, Clips = Clips / 2, Seed = 777, Threads = 1,
                Seconds = Seconds, SampleRate = Rate, BleedFraction = 0, SeparateFraction = 0,
                ProgressEvery = 1000, RequireNullDevice = true
            }).RunAsync(CancellationToken.None);
            var afterHalf = File.ReadAllLines(Path.Combine(part, DatasetManifest.FileName)).Length;
            Assert.InRange(afterHalf, 1, Clips / 2);

            await new RenderCommand(half).RunAsync(CancellationToken.None);
            var resumed = File.ReadAllLines(Path.Combine(part, DatasetManifest.FileName));

            Assert.Equal(reference.Length, resumed.Length);
            for (var i = 0; i < reference.Length; i++)
                Assert.Equal(reference[i], resumed[i]);
        }
        finally
        {
            Directory.Delete(whole, recursive: true);
            Directory.Delete(part, recursive: true);
        }
    }

    [Fact]
    public async Task EveryRowRecordsWhatWasDoneToIt()
    {
        var root = Directory.CreateTempSubdirectory("rigdataset-aug").FullName;
        try
        {
            foreach (var line in await RenderAsync(root, seed: 5150))
            {
                var row = Parse(line);
                // Level normalisation always happens and is always recorded, so an
                // unexplained level in the corpus is impossible by construction.
                Assert.Contains(row.Augmentations, a => a.StartsWith("norm:", StringComparison.Ordinal));
                Assert.Contains(row.Augmentations, a => a.StartsWith("tilt:", StringComparison.Ordinal));
                // No beds in a temp dir: the SKIP is recorded rather than silent.
                Assert.Contains("bleed:skipped-no-beds", row.Augmentations);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------- pure parts, no engine needed

    [Fact]
    public void TheClipSeedDependsOnTheRunSeedTheIndexAndTheAttempt()
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < 2_000; i++) seen.Add(RenderCommand.SeedFor(7, i, 0));
        Assert.True(seen.Count > 1_990, $"{2000 - seen.Count} of 2,000 clip seeds collided");

        Assert.NotEqual(RenderCommand.SeedFor(7, 5, 0), RenderCommand.SeedFor(8, 5, 0));
        Assert.NotEqual(RenderCommand.SeedFor(7, 5, 0), RenderCommand.SeedFor(7, 6, 0));
        Assert.NotEqual(RenderCommand.SeedFor(7, 5, 0), RenderCommand.SeedFor(7, 5, 1));
        Assert.Equal(RenderCommand.SeedFor(7, 5, 0), RenderCommand.SeedFor(7, 5, 0));
    }

    [Fact]
    public void NormalisationHitsItsTarget_AndTheTiltIsBounded()
    {
        var x = new float[4_800];
        var rng = new Random(1);
        for (var i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1) * 0.01f;

        Augmentation.Normalise(x, new Random(2), -18f, 0f);
        Assert.Equal(-18f, Augmentation.Db(Augmentation.Rms(x)), 0.1f);

        var before = Augmentation.Rms(x);
        var tag = Augmentation.Tilt(x, 48_000, new Random(3), 3f);
        // "tilt:0.0dB" when the draw was under the 0.05 dB no-op threshold, otherwise
        // signed — both are a recorded decision, which is the point of the tag.
        Assert.Matches(@"^tilt:[+-]?\d+\.\d+dB$", tag);
        // A ±3 dB tilt about the pivot cannot move broadband RMS by more than that.
        Assert.InRange(Augmentation.Db(Augmentation.Rms(x)) - Augmentation.Db(before), -3.5f, 3.5f);
    }

    [Fact]
    public void AnIdenticalRenderIsRejected_AndADifferentOneIsNot()
    {
        var dry = new float[2_400];
        for (var i = 0; i < dry.Length; i++) dry[i] = MathF.Sin(i * 0.03f);
        var same = (float[])dry.Clone();
        var scaled = dry.Select(v => v * 0.5f).ToArray();
        var driven = dry.Select(v => MathF.Tanh(v * 6f)).ToArray();

        Assert.True(Augmentation.IsIndistinguishableFrom(same, dry));
        // A pure LEVEL change is still the same rig doing nothing.
        Assert.True(Augmentation.IsIndistinguishableFrom(scaled, dry));
        Assert.False(Augmentation.IsIndistinguishableFrom(driven, dry));
    }

    [Fact]
    public void TheManifestMergeIsSortedByClipIndexWhateverOrderTheShardsFinishedIn()
    {
        var root = Directory.CreateTempSubdirectory("rigdataset-merge").FullName;
        try
        {
            var rig = new DcRigPreset([new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = 1f })]);
            foreach (var shard in new[] { 1, 0 })
            {
                using var m = new DatasetManifest(Path.Combine(root, DatasetManifest.ShardFileName(shard)));
                for (var i = shard; i < 10; i += 2)
                {
                    m.Add(i, new RigDatasetRow(DatasetManifest.ClipPathFor(i), "di/x.wav", rig, ["norm:-18.0dBFS"],
                        48_000, 5f, i));
                }
            }

            Assert.Equal(10, DatasetManifest.Merge(root));
            var lines = File.ReadAllLines(Path.Combine(root, DatasetManifest.FileName));
            var indices = lines.Select(l => DatasetManifest.IndexOfClipPath(Parse(l).ClipPath)).ToArray();
            Assert.Equal(Enumerable.Range(0, 10), indices);
            Assert.Empty(Directory.GetFiles(root, "manifest.shard*.jsonl"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
