using System.Globalization;
using System.Text.Json;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tools.RigDataset;
using JuceSharp;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.Dataset;

/// <summary>
///     The <c>eval-render</c> subcommand: "does the estimator's guess SOUND close?"
///     <para>
///         Three of these four classes of assertion need no audio at all — the
///         summary arithmetic, the predictions parsing and the generic baseline are
///         pure — and that is deliberate: they are the parts a wrong answer would be
///         INVISIBLE in, because a median over the wrong subset and a median over the
///         right one are both plausible numbers.
///     </para>
///     <para>
///         The end-to-end smoke needs the JUCE <c>Null</c> device and SKIPS without
///         it, exactly as <c>RigDatasetRenderTests</c> does and for the same reason: a
///         test host that dies holding the real sound card holds it EXCLUSIVELY.
///     </para>
/// </summary>
// Pure arithmetic, but it shares this FILE with EvalRenderSmokeTests, which arms
// DCA_ENABLE_NULL_DEVICE; the native-engine collection lint reads files, so the
// sibling joins the serialized collection rather than reading as a parallel engine.
[Collection("NativeEngine")]
public sealed class EvalRenderMathTests
{
    // ------------------------------------------------------------------ the rows

    private static EvalRenderRow Row(int index, double distance, double floor, double generic,
        double confidence = double.NaN, double truth = double.NaN, double refined = double.NaN,
        double plausibility = 0.0, string? error = null) =>
        new(index, $"clips/{index}.wav", $"di/{index}.wav",
            distance, floor,
            double.IsFinite(floor) && floor > 0 ? distance / floor : double.NaN,
            generic, truth, refined, plausibility, confidence, "clip", error);

    // --------------------------------------------------------- summary arithmetic

    [Fact]
    public void TheSummaryMediansMeansAndFractionsAreComputedOverTheRowsThatCarryThem()
    {
        // distances 1, 2, 3, 4 -> median 2.5, mean 2.5.
        // floors 1, 1, 2, NaN  -> d/floor 1.0, 2.0, 1.5 and one unknown.
        var rows = new[]
        {
            Row(0, 1.0, 1.0, generic: 2.0),
            Row(1, 2.0, 1.0, generic: 1.0),
            Row(2, 3.0, 2.0, generic: 9.0),
            Row(3, 4.0, double.NaN, generic: 9.0)
        };

        var s = EvalRenderStats.Compute(rows);

        Assert.Equal(4, s.Rows);
        Assert.Equal(2.5, s.MedianDistance, 9);
        Assert.Equal(2.5, s.MeanDistance, 9);

        // The floor-normalised column is folded over the THREE rows that have a
        // floor, never over four with a NaN in it — one short clip must not erase
        // the column for the rows that were measurable.
        Assert.Equal(3, s.FloorKnown);
        Assert.Equal(1.5, s.MedianDistanceOverFloor, 9);
        Assert.Equal(1.0 / 3.0, s.FractionUnder1p5, 9); // only 1.0; 1.5 is NOT under 1.5
        Assert.Equal(2.0 / 3.0, s.FractionUnder2, 9);   // 1.0 and 1.5; 2.0 is NOT under 2.0
        Assert.Equal(1.0, s.FractionUnder3, 9);

        Assert.Equal(5.5, s.MedianGenericDistance, 9);
        Assert.Equal(0.75, s.FractionBeatsGeneric, 9); // rows 0, 2, 3 beat generic; row 1 does not
    }

    [Fact]
    public void ASkippedRowIsNotAnAggregate_AndAnEmptySetIsStatedRatherThanFaked()
    {
        var rows = new[]
        {
            Row(0, 1.0, 1.0, generic: 2.0),
            Row(1, double.NaN, double.NaN, double.NaN, error: "the engine refused the predicted rig")
        };

        var s = EvalRenderStats.Compute(rows);
        Assert.Equal(1, s.Rows);
        Assert.Equal(1.0, s.MedianDistance, 9);

        var empty = EvalRenderStats.Compute(Array.Empty<EvalRenderRow>());
        Assert.Equal(0, empty.Rows);
        Assert.True(double.IsNaN(empty.MedianDistance));
        Assert.True(double.IsNaN(empty.FractionBeatsGeneric));
    }

    [Fact]
    public void TheTruthAndRefinedColumnsAreFoldedOnlyWhenTheyWereMeasured()
    {
        var rows = new[]
        {
            Row(0, 1.0, 1.0, generic: 2.0, refined: 0.5),
            Row(1, 2.0, 1.0, generic: 2.0, refined: 3.0),
            Row(2, 3.0, 1.0, generic: 2.0) // no refinement on this row
        };

        var s = EvalRenderStats.Compute(rows);
        Assert.Equal(1.75, s.MedianRefinedDistance, 9);
        Assert.Equal(0.5, s.FractionRefinedBeatsPredicted, 9); // one of the two refined rows improved
        Assert.True(double.IsNaN(s.MedianTruthDistance));
    }

    [Fact]
    public void TheMedianAveragesTheTwoMiddleValuesAndIsNaNForNothing()
    {
        Assert.Equal(2.0, EvalRenderStats.Median([3.0, 1.0, 2.0]), 9);
        Assert.Equal(2.5, EvalRenderStats.Median([4.0, 1.0, 3.0, 2.0]), 9);
        Assert.True(double.IsNaN(EvalRenderStats.Median([])));
        Assert.True(double.IsNaN(EvalRenderStats.Median([double.NaN])));
    }

    // ------------------------------------------------------------------ selection

    [Fact]
    public void TheSubsampleIsDeterministicForASeed_SortedAndWithoutRepeats()
    {
        var a = EvalRenderSelection.Subsample(1000, 200, 20260914);
        var b = EvalRenderSelection.Subsample(1000, 200, 20260914);
        var c = EvalRenderSelection.Subsample(1000, 200, 20260915);

        Assert.Equal(200, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(200, a.Distinct().Count());
        Assert.Equal(a.OrderBy(i => i).ToArray(), a);
        Assert.All(a, i => Assert.InRange(i, 0, 999));

        // 0 or "more than there are" keeps everything, in order.
        Assert.Equal(Enumerable.Range(0, 7), EvalRenderSelection.Subsample(7, 0, 1));
        Assert.Equal(Enumerable.Range(0, 7), EvalRenderSelection.Subsample(7, 99, 1));
    }

    [Fact]
    public void TopConfident_ExcludesRowsWithNoConfidenceRatherThanSortingThemToOneEnd()
    {
        var rows = new[]
        {
            Row(0, 1.0, 1.0, 2.0, confidence: 0.2),
            Row(1, 1.0, 1.0, 2.0, confidence: 0.9),
            Row(2, 1.0, 1.0, 2.0), // no confidence column
            Row(3, 1.0, 1.0, 2.0, confidence: 0.5)
        };

        var top = EvalRenderSelection.TopConfident(rows, 2);
        Assert.Equal([1, 3], top.Select(r => r.Index));

        // A file with NO confidence at all has no top-N — reporting the first N rows
        // as if the model had chosen them is the failure this guards.
        var none = EvalRenderSelection.TopConfident([Row(0, 1, 1, 2), Row(1, 1, 1, 2)], 2);
        Assert.Empty(none);
    }

    // -------------------------------------------------------------------- parsing

    /// <summary>The exact shape the trainer emits, with a real
    /// <see cref="DcRigPreset" /> serialisation inside it.</summary>
    private static readonly string RealLine = """
        {"clip": "clips/00000/00000042.wav", "di": "di/guitar-techs/P2_chords_00023.wav",
         "seconds": [12.5, 5.0],
         "truth": {"Modules": [{"ModuleTypeId": "amp", "Params": {"gain": 0.5, "master": 0.7},
                                "Bypassed": false, "StringParams": null, "Branch": 0,
                                "Sidechain": 0, "ControlAssignments": []}]},
         "predicted": {"Modules": [{"ModuleTypeId": "overdrive", "Params": {"drive": 0.3}},
                                   {"ModuleTypeId": "cab", "Params": {}}]},
         "confidence": 0.8125}
        """.ReplaceLineEndings(" ");

    [Fact]
    public void ThePredictionsShapeIsParsedIncludingTheSpanAndBothRigs()
    {
        var row = EvalRenderPredictions.Parse(RealLine, out var error);
        Assert.Null(error);
        Assert.NotNull(row);

        Assert.Equal("clips/00000/00000042.wav", row.ClipPath);
        Assert.Equal("di/guitar-techs/P2_chords_00023.wav", row.DiPath);
        Assert.Equal(12.5, row.StartSeconds, 9);
        Assert.Equal(5.0, row.LengthSeconds, 9);
        Assert.Equal(0.8125, row.Confidence, 9);

        var truth = Assert.Single(row.Truth.Modules);
        Assert.Equal("amp", truth.ModuleTypeId);
        Assert.Equal(0.5f, truth.Params["gain"]);

        Assert.Equal(["overdrive", "cab"], row.Predicted.Modules.Select(m => m.ModuleTypeId));
        Assert.Equal(0.3f, row.Predicted.Modules[0].Params["drive"]);
    }

    [Fact]
    public void ARowWithNoPredictedRigIsREFUSED_NotScoredAgainstAnEmptyOne()
    {
        // The failure this exists to stop: a misspelt key does not throw, it arrives
        // as null, and an empty rig produces a perfectly plausible distance.
        const string noPrediction = """
            {"clip": "c.wav", "di": "d.wav", "truth": {"Modules": []}, "predcited": {"Modules": []}}
            """;
        Assert.Null(EvalRenderPredictions.Parse(noPrediction, out var why));
        Assert.Contains("'predicted'", why);

        Assert.Null(EvalRenderPredictions.Parse("{\"clip\": \"c.wav\"}", out var why2));
        Assert.Contains("truth", why2);

        Assert.Null(EvalRenderPredictions.Parse("not json at all", out var why3));
        Assert.Contains("not JSON", why3);
    }

    [Fact]
    public void AWholeFileIsParsedAndItsRefusalsAreCountedRatherThanThrown()
    {
        var rows = EvalRenderPredictions.ParseAll(
            [RealLine, "", "{ broken", RealLine], out var errors);
        Assert.Equal(2, rows.Count);
        var error = Assert.Single(errors);
        Assert.StartsWith("line 3:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpanFallsBackWhenTheFileDoesNotCarryOne()
    {
        // No "seconds" at all: start 0, and length 0 meaning "the whole DI".
        var row = EvalRenderPredictions.Parse(
            """{"clip":"c.wav","di":"d.wav","truth":{"Modules":[]},"predicted":{"Modules":[]}}""",
            out var error);
        Assert.Null(error);
        Assert.NotNull(row);
        Assert.Equal(0, row.StartSeconds);
        Assert.Equal(0, row.LengthSeconds);
        Assert.True(double.IsNaN(row.Confidence));

        // A bare number is the LENGTH, an object is start/length.
        var bare = EvalRenderPredictions.Parse(
            """{"di":"d.wav","seconds":5,"truth":{"Modules":[]},"predicted":{"Modules":[]}}""", out _);
        Assert.Equal(5.0, bare!.LengthSeconds, 9);

        var obj = EvalRenderPredictions.Parse(
            """{"di":"d.wav","seconds":{"start":3,"length":4},"truth":{"Modules":[]},"predicted":{"Modules":[]}}""",
            out _);
        Assert.Equal(3.0, obj!.StartSeconds, 9);
        Assert.Equal(4.0, obj.LengthSeconds, 9);
    }

    // ------------------------------------------------------- the generic baseline

    [Fact]
    public void TheGenericBaselineIsARealCatalogueChainTheEngineCouldBuild()
    {
        var rig = EvalRenderCommand.GenericRig();
        Assert.Equal(["overdrive", "amp", "cab"], rig.Modules.Select(m => m.ModuleTypeId));

        // Every module id has to be one the DSP catalogue actually has, or the
        // "control" column is a render of nothing and every prediction "beats" it.
        var known = DcRigTargetCatalog.ModuleTypeIds.ToHashSet(StringComparer.Ordinal);
        foreach (var module in rig.Modules)
        {
            Assert.Contains(module.ModuleTypeId, known);
            // No knobs written out: the chain IS whatever the catalogue calls default
            // today, so it cannot drift when a default is retuned.
            Assert.Empty(module.Params);
            Assert.False(module.Bypassed);
        }

        // It is a plausible board — a control that the plausibility prior marks down
        // would make the "beats generic" column measure two things at once.
        Assert.Equal(0.0, RigPlausibility.Penalty(rig), 9);
    }

    // ----------------------------------------------------------- the self-floor

    [Fact]
    public void TheSelfFloorIsNaNRatherThanZeroWhenTheClipCannotHoldTwoWindows()
    {
        var similarity = new ToneSimilarity();
        var short_ = new MonoAudio(new float[48_000 * 3], 48_000);

        // 0 is a LEGAL floor value meaning "perfectly self-similar", so a short clip
        // must not report it — a consumer dividing by it gets a wrong answer for a
        // right-looking reason.
        Assert.True(double.IsNaN(EvalRenderCommand.SelfFloor(short_, similarity, 2.0)));
        Assert.True(double.IsNaN(EvalRenderCommand.SelfFloor(short_, similarity, 0.0)));

        var long_ = new MonoAudio(Tone(48_000 * 5, 110), 48_000);
        var floor = EvalRenderCommand.SelfFloor(long_, similarity, 2.0);
        Assert.True(double.IsFinite(floor), "a 5 s clip holds two disjoint 2 s windows");
        Assert.True(floor >= 0);
    }

    internal static float[] Tone(int frames, double hz, int rate = 48_000)
    {
        var x = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)rate;
            var env = Math.Exp(-1.2 * (t % 1.0));
            x[i] = (float)(0.25 * env * (Math.Sin(2 * Math.PI * hz * t)
                                          + 0.5 * Math.Sin(4 * Math.PI * hz * t)
                                          + 0.25 * Math.Sin(6 * Math.PI * hz * t)));
        }

        return x;
    }
}

/// <summary>
///     The end-to-end smoke: a real predictions file, a real DI pool, the real
///     offline renderer, three rows.
///     <para>
///         <b>It is the non-vacuity proof of the whole harness.</b> A prediction
///         IDENTICAL to the truth must measure exactly 0 (the renderer is
///         deterministic), and a prediction equal to the generic control must measure
///         exactly the generic column. If either drifted, every aggregate this
///         command produces would be scoring something other than the rigs.
///     </para>
/// </summary>
// NativeEngine, not a collection of its own: this class opens a device-less engine
// and DCA_ENABLE_NULL_DEVICE is process-wide, so it must serialize with every other
// class that does — the same rule RigDatasetRenderTests records.
[Collection("NativeEngine")]
public sealed class EvalRenderSmokeTests
{
    private const int Rate = 48_000;
    private const double Seconds = 1.0;

    private static void SkipWithoutEngine()
    {
        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        try
        {
            using var probe = RenderCommand.OpenDevicelessEngine(Rate, requireNull: true);
        }
        catch (JuceAudioException ex)
        {
            Assert.Skip($"No device-less JUCE engine on this machine ({ex.Message}); "
                        + "the render eval cannot run without one.");
        }
        catch (DllNotFoundException ex)
        {
            Assert.Skip($"juce_sharp.dll is not next to the test binary ({ex.Message}).");
        }
    }

    private static string MakeDi(string root, int files = 3)
    {
        var dir = Path.Combine(root, "di");
        Directory.CreateDirectory(dir);
        for (var f = 0; f < files; f++)
        {
            using (var w = new WavWriter(Path.Combine(dir, $"tone{f}.wav"), Rate, 1, WavSampleFormat.Pcm16))
                w.Write(EvalRenderMathTests.Tone((int)(Seconds * Rate) * 2, 82.4 * Math.Pow(2, f / 12.0)));
        }

        return dir;
    }

    private static DcRigPreset Rig(params string[] typeIds) =>
        new([.. typeIds.Select(t => new DcRigModule(t, new Dictionary<string, float>()))]);

    private static string Line(string clip, string di, DcRigPreset truth, DcRigPreset predicted, double confidence)
    {
        var json = new JsonSerializerOptions();
        return "{" + string.Join(",",
            $"\"clip\":{JsonSerializer.Serialize(clip)}",
            $"\"di\":{JsonSerializer.Serialize(di)}",
            $"\"seconds\":[0,{Seconds.ToString(CultureInfo.InvariantCulture)}]",
            $"\"truth\":{JsonSerializer.Serialize(truth, json)}",
            $"\"predicted\":{JsonSerializer.Serialize(predicted, json)}",
            $"\"confidence\":{confidence.ToString(CultureInfo.InvariantCulture)}") + "}";
    }

    private static async Task<(EvalRenderRow[] Rows, EvalRenderSummary Summary)> RunAsync(
        string root, string predictions, bool targetIsRender)
    {
        var outPath = Path.Combine(root, "render-eval.jsonl");
        var summaryPath = Path.Combine(root, "render-eval.summary.json");
        var code = await new EvalRenderCommand(new EvalRenderOptions(
            PredictionsPath: predictions, Root: root, OutPath: outPath, SummaryPath: summaryPath,
            Limit: 0, Seed: 7, Threads: 1, SelfWindowSeconds: 0.4,
            TargetIsRender: targetIsRender, TopConfident: 2, Refine: false,
            RefineRenders: 0, RefineLibrary: 0, RefineSeconds: 0,
            ProgressEvery: 0, BelowNormalPriority: false)).RunAsync(CancellationToken.None);
        Assert.Equal(0, code);

        var json = new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        var rows = File.ReadAllLines(outPath)
            .Select(l => JsonSerializer.Deserialize<EvalRenderRow>(l, json)!).ToArray();
        var summary = JsonSerializer.Deserialize<EvalRenderSummary>(File.ReadAllText(summaryPath), json)!;
        return (rows, summary);
    }

    [Fact]
    public async Task AgainstARerenderedTruth_AnIdenticalPredictionMeasuresExactlyZeroAndGenericMeasuresTheControl()
    {
        SkipWithoutEngine();
        var root = Directory.CreateTempSubdirectory("evalrender-render").FullName;
        try
        {
            MakeDi(root);
            // NOT overdrive -> amp -> cab: that IS the generic control, and a truth
            // rig equal to the control makes "beats generic" untestable on this row.
            var truth = Rig("bigmuff", "amp", "cab");
            var predictions = Path.Combine(root, "predictions-test.jsonl");
            File.WriteAllLines(predictions,
            [
                // 0: the prediction IS the truth.
                Line("clips/0.wav", "di/tone0.wav", truth, truth, 0.9),
                // 1: the prediction IS the generic control.
                Line("clips/1.wav", "di/tone1.wav", truth, EvalRenderCommand.GenericRig(), 0.5),
                // 2: a genuinely different rig.
                Line("clips/2.wav", "di/tone2.wav", truth, Rig("chorus", "delay"), 0.1)
            ]);

            var (rows, summary) = await RunAsync(root, predictions, targetIsRender: true);

            Assert.Equal(3, rows.Length);
            Assert.All(rows, r => Assert.Null(r.Error));
            Assert.All(rows, r => Assert.Equal("render", r.TargetSource));
            Assert.Equal(3, summary.Scored);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(ToneFeatureExtractor.ExtractorId, summary.Extractor);

            // The renderer is deterministic, so the same rig over the same DI is the
            // same audio and the distance is EXACTLY 0. Nothing else in this harness
            // can produce that number by accident.
            Assert.Equal(0.0, rows[0].Distance, 12);
            Assert.Equal(0.0, rows[0].TruthDistance, 12);
            Assert.True(rows[0].Distance < rows[0].GenericDistance,
                "the truth must beat the generic control on a row whose prediction IS the truth");

            // …and a prediction that IS the control measures the control.
            Assert.Equal(rows[1].GenericDistance, rows[1].Distance, 12);
            Assert.False(rows[1].Distance < rows[1].GenericDistance);

            Assert.True(rows[2].Distance > 0, "a different rig cannot measure zero");

            // The floor is real on a 1 s clip with a 0.4 s window, and the
            // normalisation is the division it claims to be.
            Assert.All(rows, r => Assert.True(double.IsFinite(r.SelfFloor)));
            Assert.Equal(rows[2].Distance / rows[2].SelfFloor, rows[2].DistanceOverFloor, 9);

            Assert.Equal(2, summary.TopConfidentN);
            Assert.Equal(2, summary.TopConfident.Rows);
            Assert.Contains("1.56", summary.Notes, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AgainstTheWetClipOnDisk_TheClipIsREUSEDAndAPerfectPredictionLandsFarBelowAWrongOne()
    {
        SkipWithoutEngine();
        var root = Directory.CreateTempSubdirectory("evalrender-clip").FullName;
        try
        {
            var diDir = MakeDi(root, files: 2);
            var truth = Rig("bigmuff", "amp", "cab");

            // Write the wet clips the way the corpus does: the truth render, mono
            // 16-bit PCM. This is the leg that exercises TryReadClip and the level
            // match — the render arrives at LevelMatch's target, a clip off disk does
            // not until the command puts it there.
            Directory.CreateDirectory(Path.Combine(root, "clips"));
            Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
            using (var host = new NullDeviceRigHost())
            {
                var renderer = new RigRenderer(host);
                Assert.True(renderer.IsAvailable, renderer.Describe());
                for (var i = 0; i < 2; i++)
                {
                    // Read the DI back OFF DISK rather than regenerating the tone: the
                    // corpus renders the quantised file, and rendering the unquantised
                    // float here would make the "perfect prediction" row measure the
                    // 16-bit round trip instead of the rig (measured 0.1493 when it
                    // did — a whole "different riff" apart, which is what a reader
                    // would have mistaken for a harness that does not work).
                    var buffer = WavIo.Read(Path.Combine(diDir, $"tone{i}.wav"));
                    var dry = new MonoAudio(buffer.Interleaved[..(int)(Seconds * Rate)], Rate);
                    var wet = await renderer.RenderAsync(dry, truth, CancellationToken.None);
                    using var w = new WavWriter(Path.Combine(root, "clips", $"{i}.wav"), Rate, 1,
                        WavSampleFormat.Pcm16);
                    w.Write(wet.Samples);
                }
            }

            var predictions = Path.Combine(root, "predictions-test.jsonl");
            File.WriteAllLines(predictions,
            [
                Line("clips/0.wav", "di/tone0.wav", truth, truth, 0.9),
                Line("clips/1.wav", "di/tone1.wav", truth, Rig("chorus", "delay"), 0.2)
            ]);

            var (rows, summary) = await RunAsync(root, predictions, targetIsRender: false);

            Assert.Equal(2, rows.Length);
            Assert.All(rows, r => Assert.Equal("clip", r.TargetSource));
            // The truth is NOT re-rendered in this mode, so that column is honestly
            // absent rather than quietly zero.
            Assert.All(rows, r => Assert.True(double.IsNaN(r.TruthDistance)));

            // A PERFECT prediction does NOT measure 0 in clip mode, and the reason is
            // worth knowing: the corpus stores 16-bit PCM at a low RMS, and that round
            // trip alone measured 0.1436 here against UNDER 0.05 when the same clip is
            // written as Float32 (probed 2026-09-14). So clip-mode distances carry a
            // per-row storage offset that render mode does not — the claim is a RATIO,
            // never an absolute threshold.
            Assert.True(rows[0].Distance < 0.25,
                $"a prediction equal to the truth measured {rows[0].Distance:F4} against its own clip; "
                + "0.1436 was the 16-bit storage floor when this was written");
            Assert.True(rows[1].Distance > 4 * rows[0].Distance,
                $"a chorus+delay ({rows[1].Distance:F4}) must be far from a bigmuff+amp+cab clip "
                + $"({rows[0].Distance:F4})");

            // The perfect prediction beats the control and the wrong one does not —
            // which is the whole claim of the generic column, on two rows.
            Assert.True(rows[0].Distance < rows[0].GenericDistance);
            Assert.True(rows[1].Distance > rows[1].GenericDistance);
            Assert.Equal(0.5, summary.All.FractionBeatsGeneric, 9);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ARigTheEngineRefusesIsCountedAndNamed_NotDroppedIntoTheAggregate()
    {
        SkipWithoutEngine();
        var root = Directory.CreateTempSubdirectory("evalrender-refuse").FullName;
        try
        {
            MakeDi(root, files: 1);
            var truth = Rig("amp", "cab");
            var predictions = Path.Combine(root, "predictions-test.jsonl");
            File.WriteAllLines(predictions,
            [
                Line("clips/0.wav", "di/tone0.wav", truth, truth, 0.9),
                // A DI path that is not on disk: the row must be SKIPPED with a
                // reason, because silently dropping it improves the aggregate it
                // belonged to.
                Line("clips/1.wav", "di/missing.wav", truth, truth, 0.9)
            ]);

            var (rows, summary) = await RunAsync(root, predictions, targetIsRender: true);

            Assert.Equal(2, rows.Length);
            Assert.Equal(1, summary.Scored);
            Assert.Equal(1, summary.Skipped);
            Assert.NotNull(rows[1].Error);
            Assert.Contains("di/missing.wav", string.Join(" ", summary.SkipReasons), StringComparison.Ordinal);
            Assert.Equal(1, summary.All.Rows);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
