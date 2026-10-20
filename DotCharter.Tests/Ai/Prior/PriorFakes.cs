using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Prior;

/// <summary>
///     The prior lane's stand-ins for the two services it sits on: the design
///     service (for the prior source) and the model factory + model (for the
///     narrator). Both CAPTURE what they were asked, because every assertion in this
///     folder is about what the model is TOLD — a prior source that sends a prompt
///     which never mentions the stem produces a guitar rig for a bass part, and
///     nothing downstream can tell.
/// </summary>
internal static class PriorFakes
{
    /// <summary>A minimal rig for a describer/narrator test.</summary>
    internal static DcRigPreset Rig(params string[] moduleTypeIds)
        => new(moduleTypeIds.Select(id =>
            new DcRigModule(id, new Dictionary<string, float>(StringComparer.Ordinal))).ToList());

    /// <summary>A tone preset wrapping <paramref name="rig" />, as the design
    /// service would have returned it.</summary>
    internal static DspTonePreset Tone(DcRigPreset rig, string name = "Fake Tone")
        => new("fake-tone", name, DspToneCategory.Clean, "a fake", rig, DspToneOrigin.User);

    /// <summary>An <c>AppConfig</c> carrying provider settings.</summary>
    internal static AppConfig Config(AiProviderSettings settings) => new() { Ai = settings };

    /// <summary>Settings for a configured Claude provider.</summary>
    internal static AiProviderSettings Claude { get; } =
        new(AiProviderKind.Claude, "claude-fake-1", null);
}

/// <summary>A design service that answers a fixed tone, throws a fixed fault, or
/// blocks on the token — and remembers the request it was handed.</summary>
internal sealed class FakeToneDesignService : IToneDesignService
{
    /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
    public Task<DotCharter.Tone.Ai.ToneDesignManualPrompt> BuildManualPromptAsync(
        DotCharter.Tone.Ai.ToneDesignRequest request, CancellationToken ct) =>
        throw new NotSupportedException("this fake never builds a copy & paste prompt");

    /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
    public Task<DotCharter.Tone.Ai.ToneDesignResult> ImportReplyAsync(
        DotCharter.Tone.Ai.ToneDesignRequest request, string reply, IProgress<string>? status,
        CancellationToken ct) =>
        throw new NotSupportedException("this fake never imports a pasted answer");

    private readonly Func<ToneDesignRequest, ToneDesignResult> _answer;

    internal FakeToneDesignService(Func<ToneDesignRequest, ToneDesignResult> answer, bool configured = true)
    {
        _answer = answer;
        IsConfigured = configured;
    }

    /// <summary>The last request the service was handed — what the assertions read.</summary>
    internal ToneDesignRequest? LastRequest { get; private set; }

    /// <summary>How many times a design was asked for.</summary>
    internal int Calls { get; private set; }

    public bool IsConfigured { get; set; }

    public event EventHandler? ConfiguredChanged;

    public void NotifyConfigurationChanged() => ConfiguredChanged?.Invoke(this, EventArgs.Empty);

    public Task<ToneDesignResult> DesignAsync(
        ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        LastRequest = request;
        return Task.FromResult(_answer(request));
    }
}

/// <summary>A model that answers a fixed reply string, or throws.</summary>
internal sealed class FakeToneDesignModel(
    Func<ToneDesignPrompt, ToneDesignModelReply> answer,
    AiProviderKind kind = AiProviderKind.Claude) : IToneDesignModel, IDisposable
{
    /// <summary>The prompt the model was handed.</summary>
    internal ToneDesignPrompt? LastPrompt { get; private set; }

    /// <summary>True once the caller disposed it — the narrator must, because the
    /// OpenAI-compatible provider owns an HttpClient per instance.</summary>
    internal bool Disposed { get; private set; }

    public AiProviderKind Kind => kind;

    public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastPrompt = prompt;
        return Task.FromResult(answer(prompt));
    }

    public Task<AiProbeResult> ProbeAsync(CancellationToken ct)
        => Task.FromResult(new AiProbeResult(true, "ok", "fake"));

    public void Dispose() => Disposed = true;
}

/// <summary>A factory over one <see cref="FakeToneDesignModel" />, or one that
/// throws NotConfigured.</summary>
internal sealed class FakeToneDesignModelFactory(FakeToneDesignModel? model) : IToneDesignModelFactory
{
    /// <summary>The model it hands out, or null to make <see cref="Create" /> throw
    /// NotConfigured exactly as the real factory does.</summary>
    internal FakeToneDesignModel? Model { get; } = model;

    /// <summary>How many models were minted — one per narration, disposed by the
    /// caller.</summary>
    internal int Created { get; private set; }

    public IToneDesignModel Create(AiProviderSettings settings)
    {
        if (Model is null)
            throw new ToneDesignException(ToneDesignFault.NotConfigured, "no provider in this fake");
        Created++;
        return Model;
    }

    public bool Supports(AiProviderKind kind)
        => kind is AiProviderKind.Claude or AiProviderKind.OpenAiCompatible;
}
