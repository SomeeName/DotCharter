using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tools.RigDataset;
using DotCharter.Tools.RigDataset.Beds;
using DotCharter.Tools.RigDataset.Separate;

// tools/rig-dataset — the audio→rig training corpus.
//
//   render    DI clip + sampled rig -> wet clip + a RigDatasetRow, N times.
//   beds      synthesise the backing beds stages A and B both need, from our own DSP.
//   separate  stage B: mix a rendered clip INTO a bed, separate it, keep the stem.
//   sample    print rigs the sampler draws, without rendering (a prior sanity check).
//   knob-sensitivity  per-(module, param) A/B render -> tools/rig-model/knob_sensitivity.json.
//   eval-render       does the estimator's guess SOUND close? render truth vs predicted,
//                     measure the tone distance, normalise by the target's self-floor.
//   build-library     render the matcher's retrieval library for the BUNDLED DI, to
//                     publish as rig-library-v1.zip. Resumable; same file the app grows.
//
// The DI pool and the rendered clips live OUTSIDE the repo, under
// DotCharter-local/datasets/rig/. See tools/rig-dataset/README.md.

var argv = args;
if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
{
    Usage();
    return 0;
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl-C flushes the manifest instead of losing the run. The SECOND one kills.
    e.Cancel = !cts.IsCancellationRequested;
    if (!cts.IsCancellationRequested) Console.WriteLine("\nStopping after the in-flight clips…");
    cts.Cancel();
};

var flags = ParseFlags(argv.Skip(1));

switch (argv[0])
{
    case "render":
        return await new RenderCommand(BuildOptions(flags, argv)).RunAsync(cts.Token);

    case "knob-sensitivity":
    {
        var outPath = Get(flags, "out", null)
                      ?? Path.Combine("tools", "rig-model", "knob_sensitivity.json");
        return await new KnobSensitivityCommand(new KnobSensitivityOptions(
            OutPath: outPath,
            DiPath: Get(flags, "di", null),
            Limit: GetInt(flags, "limit", 0))).RunAsync(cts.Token);
    }

    case "eval-render":
    {
        var predictions = Get(flags, "predictions", null)
                          ?? throw new ArgumentException("--predictions <predictions-test.jsonl> is required.");
        var root = Get(flags, "out", null) ?? Get(flags, "root", null)
            ?? throw new ArgumentException("--root <dataset root> is required.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(predictions))!;
        var outPath = Get(flags, "report", null) ?? Path.Combine(dir, "render-eval.jsonl");
        return await new EvalRenderCommand(new EvalRenderOptions(
            PredictionsPath: predictions,
            Root: root,
            OutPath: outPath,
            SummaryPath: Get(flags, "summary", null)
                         ?? Path.ChangeExtension(outPath, null) + ".summary.json",
            Limit: GetInt(flags, "limit", 0),
            Seed: GetInt(flags, "seed", 20260914),
            Threads: GetInt(flags, "threads", 2),
            SelfWindowSeconds: GetFloat(flags, "self-window", (float)EvalRenderCommand.DefaultSelfWindowSeconds),
            TargetIsRender: string.Equals(Get(flags, "target", "clip"), "render", StringComparison.Ordinal),
            TopConfident: GetInt(flags, "top-confident", 0),
            Refine: flags.ContainsKey("refine"),
            RefineRenders: GetInt(flags, "refine-renders", 8),
            RefineLibrary: GetInt(flags, "refine-library", 64),
            RefineSeconds: GetFloat(flags, "refine-seconds", 60f),
            ProgressEvery: GetInt(flags, "progress-every", 10),
            BelowNormalPriority: !flags.ContainsKey("normal-priority"))).RunAsync(cts.Token);
    }

    case "build-library":
    {
        var outPath = Get(flags, "out", null)
                      ?? throw new ArgumentException("--out <file.bin or dir> is required.");
        var defaults = new RigLibraryOptions();
        return await new BuildLibraryCommand(new BuildLibraryOptions(
            Out: outPath,
            Count: GetInt(flags, "count", defaults.Count),
            Seed: GetInt(flags, "seed", defaults.Seed),
            ProgressEvery: GetInt(flags, "progress-every", 10),
            BelowNormalPriority: !flags.ContainsKey("normal-priority"))).RunAsync(cts.Token);
    }

    case "beds":
    {
        var root = Get(flags, "out", null)
                   ?? throw new ArgumentException("--out <dir> is required (the dataset root, or a beds dir).");
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return await new BedsCommand(new BedsOptions
        {
            OutDir = Path.GetFileName(trimmed) == "beds" ? root : Path.Combine(root, "beds"),
            Count = GetInt(flags, "count", BedPlanner.DefaultCount),
            Seed = GetInt(flags, "seed", 20260913),
            Force = flags.ContainsKey("force"),
            DryRun = flags.ContainsKey("dry-run"),
            // Beds default to REFUSING the fallback to the real sound card, unlike
            // `render`. A bed run is short and unattended and nobody is watching the
            // log — and a process that dies holding the ASIO device holds it
            // EXCLUSIVELY until someone kills it. One survived 32 hours that way.
            RequireNullDevice = !flags.ContainsKey("allow-default-device")
        }).RunAsync(cts.Token);
    }

    case "separate":
    {
        var root = Get(flags, "out", null)
                   ?? throw new ArgumentException("--out <dir> is required (the dataset root).");
        var bedsDir = Get(flags, "beds", Path.Combine(root, "beds"))!;
        return await new SeparateCommand(new SeparateOptions
        {
            Root = root,
            BedsRoot = Directory.Exists(bedsDir) ? bedsDir : null,
            StemModelPath = Get(flags, "stem-model", null),
            ModelName = Get(flags, "model-name", "htdemucs_6s")!,
            Fraction = GetFloat(flags, "separate-fraction", 0.40f),
            Seed = GetInt(flags, "seed", 20260913),
            GpuName = Get(flags, "gpu-name", GpuSelector.DefaultWantedName)!,
            AllowGamingGpu = flags.ContainsKey("allow-gaming-gpu"),
            ForceCpu = flags.ContainsKey("cpu"),
            IntraOpThreads = GetInt(flags, "intra-op-threads", 2),
            Threads = GetInt(flags, "threads", 1),
            Shards = GetInt(flags, "shards", 1),
            Shard = GetInt(flags, "shard", 0),
            ChildArgs = StripShardFlags(argv),
            Limit = GetInt(flags, "limit", 0),
            StopFile = Get(flags, "stop-file", null),
            ProgressEvery = GetInt(flags, "progress-every", 25),
            DryRun = flags.ContainsKey("dry-run")
        }).RunAsync(cts.Token);
    }

    case "merge":
    {
        var root = Get(flags, "out", null)
                   ?? throw new ArgumentException("--out <dir> is required.");
        Console.WriteLine($"{DatasetManifest.Merge(root)} rows in {Path.Combine(root, DatasetManifest.FileName)}.");
        return 0;
    }

    case "sample":
    {
        var n = GetInt(flags, "count", 20);
        var seed = GetInt(flags, "seed", 20260912);
        var sampler = new RigSampler();
        var rng = new Random(seed);
        var profile = new RigSamplingProfile(AllowNamCaptures: flags.ContainsKey("nam"))
        {
            Kind = Get(flags, "profile", RigProfileKinds.Mixed)!,
            NamCaptureDirectory = Get(flags, "nam", null)
        };
        for (var i = 0; i < n; i++)
        {
            var rig = sampler.Sample(rng, profile);
            Console.WriteLine(string.Join(" -> ", rig.Modules.Select(m => m.ModuleTypeId)));
        }

        return 0;
    }

    default:
        Console.Error.WriteLine($"error: unknown command '{argv[0]}'.");
        Usage();
        return 2;
}

static RenderOptions BuildOptions(Dictionary<string, string?> f, string[] argv)
{
    var outRoot = Get(f, "out", null)
                  ?? throw new ArgumentException("--out <dir> is required.");
    var diRoot = Get(f, "di", null) ?? Path.Combine(outRoot, "di");
    var bedsRoot = Get(f, "beds", null) ?? Path.Combine(Path.GetDirectoryName(outRoot.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? outRoot, "rig", "beds");

    return new RenderOptions
    {
        OutRoot = outRoot,
        DiRoot = diRoot,
        BedsRoot = Directory.Exists(bedsRoot) ? bedsRoot : null,
        NamRoot = Get(f, "nam", null),
        StemModelPath = Get(f, "stem-model", null),
        Clips = GetInt(f, "clips", 1000),
        Seed = GetInt(f, "seed", 20260912),
        Threads = GetInt(f, "threads", 4),
        Profile = Get(f, "profile", RigProfileKinds.Mixed)!,
        Seconds = GetFloat(f, "seconds", 5f),
        DiPitchMaxSemitones = GetFloat(f, "di-pitch-max", 2f),
        DiLevelMaxDb = GetFloat(f, "di-level-max", 6f),
        PickupCombFraction = GetFloat(f, "pickup-comb-fraction", 0.5f),
        SeparateFraction = GetFloat(f, "separate-fraction", 0f),
        BleedFraction = GetFloat(f, "bleed-fraction", 0.30f),
        CudaDevice = GetInt(f, "cuda-device", -1),
        Shards = GetInt(f, "shards", 1),
        Shard = GetInt(f, "shard", 0),
        ChildArgs = StripShardFlags(argv),
        ProgressEvery = GetInt(f, "progress-every", 100),
        DryRun = f.ContainsKey("dry-run")
    };
}

// The parent's own argv with --shards/--shard/--threads removed, so it can re-issue
// it to a child with its own values appended.
static string[] StripShardFlags(string[] argv)
{
    var outp = new List<string>();
    for (var i = 0; i < argv.Length; i++)
    {
        var a = argv[i];
        var name = a.StartsWith("--", StringComparison.Ordinal)
            ? a[2..].Split('=')[0]
            : null;
        if (name is "shards" or "shard" or "threads")
        {
            if (!a.Contains('=', StringComparison.Ordinal)
                && i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
            }

            continue;
        }

        outp.Add(a);
    }

    return outp.ToArray();
}

static Dictionary<string, string?> ParseFlags(IEnumerable<string> rest)
{
    var f = new Dictionary<string, string?>(StringComparer.Ordinal);
    string? pending = null;
    foreach (var a in rest)
    {
        if (a.StartsWith("--", StringComparison.Ordinal))
        {
            if (pending is not null) f[pending] = null;
            var eq = a.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) { f[a[2..eq]] = a[(eq + 1)..]; pending = null; }
            else
            {
                pending = a[2..];
            }
        }
        else if (pending is not null) { f[pending] = a; pending = null; }
    }

    if (pending is not null) f[pending] = null;
    return f;
}

static string? Get(Dictionary<string, string?> f, string k, string? fallback)
    => f.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

static int GetInt(Dictionary<string, string?> f, string k, int fallback)
    => int.TryParse(Get(f, k, null), out var v) ? v : fallback;

static float GetFloat(Dictionary<string, string?> f, string k, float fallback)
    => float.TryParse(Get(f, k, null), System.Globalization.CultureInfo.InvariantCulture, out var v)
        ? v
        : fallback;

static void Usage() => Console.WriteLine("""
    DotCharter rig-dataset tool

      render --out <dir> [--di <dir>] [--clips N] [--seed S] [--threads 4]
             [--profile mixed|clean|crunch|highgain|bass|ambient|direct]
             [--seconds 5] [--beds <dir>] [--nam <dir>] [--bleed-fraction 0.3]
             [--di-pitch-max 2] [--di-level-max 6] [--pickup-comb-fraction 0.5]
             [--separate-fraction 0] [--stem-model <htdemucs_6s.onnx>]
             [--cuda-device N] [--progress-every 100] [--dry-run]

        Renders N clips: DI clip + sampled rig -> wet clip + a RigDatasetRow in
        <dir>/manifest.jsonl. RESUMABLE — indices already in the manifest are
        skipped, and a clip's whole draw comes from its own derived seed, so the
        corpus is identical whether it was rendered in one run or five, on one
        thread or four.

        --threads N spawns N CHILD PROCESSES at Idle priority, each rendering
        indices where index % N == shard, and merges their manifests at the end.
        ProcessOffline is not concurrent even across engine seats, so threads in
        one process do not work; this is not a preference.
        --cuda-device pins ONNX Runtime via CUDA_VISIBLE_DEVICES (the session
        factory hard-codes device_id=0 and does not expose it).

      build-library --out <file.bin | dir> [--count 4000] [--seed 20260912]
                    [--progress-every 10] [--normal-priority]

        Renders the "Match from audio" retrieval library for the bundled reference
        DI: K sampled rigs through the real engine over the 20 s medley, embedded,
        appended one row at a time to the app's own file format. RESUMABLE — rerun
        to continue at the last complete row. ~2.8 h for 4,000 rows at 2.55 s a
        render (Release). The file name the app expects is printed and used when
        --out is a directory. Runs at BelowNormal unless --normal-priority.

      beds --out <dataset root|beds dir> [--count 30] [--seed 20260913]
           [--force] [--dry-run]

        Synthesises backing beds from the app's own DSP — drums, bass and keys, no
        third-party audio anywhere — and writes bed_NNN.wav plus a bed_NNN.json
        sidecar carrying the whole plan and a licence line. RESUMABLE: a bed with
        both files is skipped unless --force.

      separate --out <dataset root> --stem-model <htdemucs_6s.onnx>
               [--beds <dir>] [--separate-fraction 0.40] [--seed 20260913]
               [--gpu-name 3080] [--allow-gaming-gpu] [--cpu]
               [--threads 1] [--limit N] [--stop-file <path>] [--dry-run]

        STAGE B. Mixes each selected clip INTO a bed at a guitar-to-mix level drawn
        from −12…+3 dB, separates it with the app's own htdemucs, and stores the
        guitar stem under clips-sep/ with the ORIGINAL rig label. Selection is
        deterministic in (--seed, clip index), so raising the fraction only ever
        ADDS rows; resume skips any row that already carries a `separated:` tag.

        The GPU is chosen BY NAME and pinned by uuid through CUDA_VISIBLE_DEVICES
        (OnnxSessionFactory hard-codes device_id=0; the environment variable is
        read before ORT enumerates, so no submodule change is needed). It REFUSES
        the owner's RTX 5070 Ti unless --allow-gaming-gpu.

      merge --out <dir>

        Folds any leftover manifest.shard*.jsonl into manifest.jsonl, sorted.
        Run it if a fan-out was killed before it could merge.

      sample [--count 20] [--seed S] [--profile ...] [--nam <dir>]

        Prints chains the sampler draws, with no engine and no audio. The fast
        way to eyeball the priors after changing them.

      knob-sensitivity [--out tools/rig-model/knob_sensitivity.json]
                       [--di <file-or-dir>] [--limit N]

        Measures how far a FULL-TRAVEL move of each knob shifts the tone metric:
        for every (module, param) in RigLabelVocabulary, renders one fixed
        reference DI through that module twice — the param at normalised 0.1 and
        at 0.9, everything else at its catalogue default — and records the RAW
        ToneSimilarity distance. The python trainer turns it into a per-knob loss
        weight (tools/rig-model). Sequential: one engine per process.

        --di takes a WAV or a directory (first WAV ordinally); with no pool it
        falls back to the repo's synthesised ReferenceDi and says so in the JSON.
        --limit N stops after N params, for a smoke run.

      eval-render --predictions <predictions-test.jsonl> --root <dataset root>
                  [--report <render-eval.jsonl>] [--summary <path.json>]
                  [--limit N] [--seed S] [--threads 2] [--self-window 2.0]
                  [--target clip|render] [--top-confident N] [--progress-every 10]
                  [--normal-priority]
                  [--refine] [--refine-renders 8] [--refine-library 64]
                  [--refine-seconds 60]

        DOES IT SOUND CLOSE? For every row of a trained estimator's predictions,
        renders the PREDICTED rig over the row's dry DI span and measures the
        tone distance to the target. Writes one JSONL row per prediction plus a
        summary JSON.

        Three columns make the distance readable: `selfFloor` (the target against
        a disjoint window of ITSELF — what "the same tone" costs on this
        material, NaN when the target is too short for two windows),
        `distanceOverFloor` (what makes rows comparable), and `genericDistance`
        (a fixed overdrive -> amp -> cab at catalogue defaults, the audio eval's
        `floor-generic`) so "beats generic" is answered per row. The predicted
        rig's plausibility penalty rides along.

        --target clip (the default) scores against the wet clip already on disk,
        which carries the corpus augmentations no rig can reproduce — the product
        question. --target render re-renders the TRUTH rig as the target instead,
        where a prediction equal to the truth measures exactly 0; that is the
        harness's own non-vacuity check.

        --refine is OPT-IN and slow: it runs the matcher's CMA-ES refinement from
        the predicted rig as the prior for --refine-renders renders a row, and
        reports the distance AFTER refinement — the product path. It builds a
        --refine-library-row retrieval library once first.

        Renders serialise on one engine and one seat (the same rule `render`
        documents); --threads only bounds the rows in flight around that queue.
        The process drops to BelowNormal priority unless --normal-priority.
    """);
