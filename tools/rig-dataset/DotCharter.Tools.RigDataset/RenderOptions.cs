using DotCharter.Services.Ai.Match;

namespace DotCharter.Tools.RigDataset;

/// <summary>Everything <c>render</c> takes. Defaults are the ones the corpus was
/// built with, so a re-run needs only <c>--clips</c>, <c>--seed</c> and
/// <c>--out</c>.</summary>
public sealed class RenderOptions
{
    public required string DiRoot { get; init; }
    public required string OutRoot { get; init; }
    public string? BedsRoot { get; init; }
    public string? NamRoot { get; init; }
    public string? StemModelPath { get; init; }

    public int Clips { get; init; } = 1000;
    public int Seed { get; init; } = 20260912;

    /// <summary>Worker threads, one per engine SEAT. Capped at
    /// <c>JuceAudioEngine.MaxPlayers</c> (4) because each worker renders through its
    /// own seat's rig, which is what lets four renders share one device-less engine
    /// without contending each other's process barrier.</summary>
    public int Threads { get; init; } = 4;

    public string Profile { get; init; } = RigProfileKinds.Mixed;

    public float Seconds { get; init; } = 5f;
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Fraction of clips that go through the stem separator. Stage B of the
    /// study's augmentation plan, and the one that matters most: it is the only stage
    /// that produces the distribution the model will ACTUALLY see at inference.</summary>
    public double SeparateFraction { get; init; }

    /// <summary>Fraction of clips that get a backing bed mixed under them (the cheap
    /// half of the same idea). Ignored when there are no beds.</summary>
    public double BleedFraction { get; init; } = 0.30;

    public float BleedMinDb { get; init; } = -40f;
    public float BleedMaxDb { get; init; } = -25f;
    public float TiltMaxDb { get; init; } = 3f;
    public float LevelJitterDb { get; init; } = 3f;

    /// <summary>Half-width of the DI pitch-shift window, in semitones. The study's
    /// figure is ±2: enough to make one recorded note four, small enough that a
    /// rate-based shift does not read as a tape fault. 0 switches the stage off —
    /// and the tag then says <c>di-pitch:+0.00st</c> rather than disappearing, so a
    /// slice is never silently absent from the manifest.</summary>
    public float DiPitchMaxSemitones { get; init; } = 2f;

    /// <summary>Half-width of the DI level window, in dB, applied AFTER
    /// normalisation. ±6 is the study's figure. This is what varies how hard a drive
    /// is hit, which is most of what "the same rig sounds different" means.</summary>
    public float DiLevelMaxDb { get; init; } = 6f;

    /// <summary>Fraction of clips whose DI gets a pickup-position comb. Half: it is
    /// an approximation (see <see cref="Augmentation.PickupComb" />), so it earns a
    /// share of the corpus rather than all of it.</summary>
    public double PickupCombFraction { get; init; } = 0.5;

    /// <summary>Pin ONNX Runtime's CUDA execution provider to one physical GPU.
    /// <c>OnnxSessionFactory</c> hard-codes <c>device_id = 0</c> and does not expose
    /// it, and JuceSharp is a submodule this tool does not edit — so the only lever
    /// is <c>CUDA_VISIBLE_DEVICES</c>, which REMAPS the chosen card to device 0
    /// before the runtime ever looks. Set before the first ORT call or it has no
    /// effect. −1 leaves the environment alone.</summary>
    public int CudaDevice { get; init; } = -1;

    /// <summary>Which stride of clip indices this PROCESS renders
    /// (<c>index % Shards == Shard</c>), and how many processes there are. Set by the
    /// parent when it fans out; 1/0 means "I am the only one".</summary>
    public int Shards { get; init; } = 1;

    /// <inheritdoc cref="Shards" />
    public int Shard { get; init; }

    /// <summary>The argv the parent hands each child, minus the shard flags. Kept
    /// verbatim so a child's options are the parent's by construction and a new flag
    /// never has to be added in two places.</summary>
    public IReadOnlyList<string> ChildArgs { get; init; } = [];

    /// <summary>Refuse to fall back to the DEFAULT audio device when the JUCE
    /// <c>Null</c> device is unavailable. Tests set it: the fallback opens the real
    /// sound card, and a test host that dies holding an ASIO device takes the
    /// machine's audio with it until someone kills the process.</summary>
    public bool RequireNullDevice { get; init; }

    public bool DryRun { get; init; }
    public int ProgressEvery { get; init; } = 100;
}
