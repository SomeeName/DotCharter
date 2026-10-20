namespace DotCharter.Tools.RigDataset.Separate;

/// <summary>A separator the stage can drive. The interface exists so the resume and
/// selection logic can be tested over a FAKE — the real one is a 110 MB ONNX model
/// and ~4 s of CPU per clip, which is not a unit test.</summary>
public interface IGuitarSeparator
{
    /// <summary>One line naming the backend, recorded in the progress log.</summary>
    string Backend { get; }

    /// <summary>Mono in, mono guitar stem out, or <c>null</c> when the model produced
    /// no guitar source.</summary>
    /// <param name="mono">The mix.</param>
    /// <param name="sampleRate">Its rate.</param>
    /// <param name="ct">Cancellation.</param>
    Task<float[]?> IsolateGuitarAsync(float[] mono, int sampleRate, CancellationToken ct);
}

/// <summary>Everything <c>separate</c> takes.</summary>
public sealed class SeparateOptions
{
    /// <summary>The dataset root: the one that holds <c>manifest.jsonl</c> and
    /// <c>clips/</c>. Separated clips are written beside them under
    /// <c>clips-sep/</c>.</summary>
    public required string Root { get; init; }

    /// <summary>The bed pool. Without it the stage still runs, but every row is
    /// tagged <c>separated:htdemucs_6s:no-bed</c> and is worth much less: a separator
    /// handed a bare guitar mostly hands it back.</summary>
    public string? BedsRoot { get; init; }

    /// <summary>Path to <c>htdemucs_6s.onnx</c>.</summary>
    public string? StemModelPath { get; init; }

    /// <summary>The model name recorded in the tag.</summary>
    public string ModelName { get; init; } = "htdemucs_6s";

    /// <summary>Fraction of the manifest's rows to separate. 0.40 is the v2 target.</summary>
    public double Fraction { get; init; } = 0.40;

    /// <summary>Drives selection, the bed draw and the level draw. Keep it equal to
    /// the render's seed: selection is then a stable property of the CORPUS rather
    /// than of the day the stage happened to run.</summary>
    public int Seed { get; init; } = 20260913;

    /// <summary>Guitar level relative to the bed, in dB. The study's realistic
    /// window.</summary>
    public float MixMinDb { get; init; } = -12f;

    /// <inheritdoc cref="MixMinDb" />
    public float MixMaxDb { get; init; } = 3f;

    /// <summary>Level jitter applied after the stem comes back, in dB.</summary>
    public float LevelJitterDb { get; init; } = 3f;

    /// <summary>Name fragment of the GPU to pin. See <see cref="GpuSelector" />.</summary>
    public string GpuName { get; init; } = GpuSelector.DefaultWantedName;

    /// <summary>Permission to take the owner's gaming card.</summary>
    public bool AllowGamingGpu { get; init; }

    /// <summary>Skip GPU selection entirely and let ONNX Runtime fall to the CPU EP.
    /// Slow (~4 s per 5 s clip per process) but it needs no CUDA runtime.</summary>
    public bool ForceCpu { get; init; }

    /// <summary>CPU EP intra-op threads. Held at 2 so four shards stay inside the
    /// four-thread budget agent work runs under on this box.</summary>
    public int IntraOpThreads { get; init; } = 2;

    /// <inheritdoc cref="RenderOptions.Shards" />
    public int Shards { get; init; } = 1;

    /// <inheritdoc cref="RenderOptions.Shard" />
    public int Shard { get; init; }

    /// <summary>Child processes to fan out over, like the render. Defaults to 1: on a
    /// GPU a second ONNX session is contention, not throughput.</summary>
    public int Threads { get; init; } = 1;

    /// <inheritdoc cref="RenderOptions.ChildArgs" />
    public IReadOnlyList<string> ChildArgs { get; init; } = [];

    /// <summary>Stop after this many rows (0 = no limit). What the dry run uses to
    /// time the stage without committing to it.</summary>
    public int Limit { get; init; }

    /// <summary>A file whose EXISTENCE stops the run cleanly at the next row. The
    /// unattended pipeline's brake — the alternative is killing a process that holds
    /// a half-written manifest.</summary>
    public string? StopFile { get; init; }

    /// <summary>Rows between progress lines and manifest flushes.</summary>
    public int ProgressEvery { get; init; } = 25;

    /// <summary>Select, draw and log, but neither separate nor write.</summary>
    public bool DryRun { get; init; }
}
