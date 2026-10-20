using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Spectrum;
using JuceSharp;

namespace DotCharter.Services.Ai.Match;

/// <summary>
///     The seam between <see cref="RigRenderer" /> and a real JUCE engine: apply a
///     rig, render a planar stereo buffer in place, no device involved.
///     <para>
///         It exists so the renderer's own rules — level matching, chunking, mono
///         handling, determinism — are testable without the native DLL, and so that
///         the one place which knows how to stand an engine up offline is one small
///         class with three documented traps in it.
///     </para>
/// </summary>
public interface IOfflineRigHost : IDisposable, IOfflineToneRenderer
{
    /// <summary>False when the native engine or the module catalogue could not be
    /// loaded. A renderer over an unavailable host must SAY so, never hand back
    /// silence that reads as a rig which merely sounds quiet.</summary>
    bool IsAvailable { get; }

    /// <summary>One line naming the backend, for a report header.</summary>
    string Describe();

    /// <summary>
    ///     Rebuilds the chain from <paramref name="rig" /> and renders
    ///     <paramref name="left" /> / <paramref name="right" /> through it IN PLACE.
    ///     <para>
    ///         <b>Rebuilds, not updates.</b> A DSP module carries state — delay lines,
    ///         reverb tanks, envelope followers, LFO phase — so rendering twice through
    ///         a chain that was merely re-parameterised gives two different answers for
    ///         the same input. Every call starts from fresh nodes, and that is what
    ///         makes <see cref="IRigRenderer" /> deterministic.
    ///     </para>
    /// </summary>
    void Render(DcRigPreset rig, float[] left, float[] right, int sampleRate);

    /// <summary>The rig's node spectrum renders its pre/post cuts here, on
    /// the same offline engine as the loudness meter: false when the host is
    /// unavailable or the render threw, never silence that reads as a result.</summary>
    bool IOfflineToneRenderer.TryRender(DcRigPreset rig, float[] left, float[] right, int sampleRate)
    {
        try
        {
            if (!IsAvailable) return false;
            Render(rig, left, right, sampleRate);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
///     The real host: a device-less <see cref="JuceAudioEngine" /> on the null output
///     device, with the app's own <see cref="JuceDspChainService" /> and
///     <see cref="DcRigApplier" /> on top — so an offline render goes through exactly
///     the code path a live tone change goes through.
///     <para>
///         <b>Three things here are not optional and each fails quietly if dropped.</b>
///     </para>
///     <list type="number">
///         <item>
///             <see cref="JuceEngineConfig.EnableNullDevice" /> on the engine config — the
///             native device manager registers the "Null" device type for THIS engine
///             only (ABI v41, <c>dca_engine_create2</c>). Never the process-wide
///             <c>DCA_ENABLE_NULL_DEVICE</c> variable: this host is built during ordinary
/// play (the tone loudness meter), and a live-engine rebuild on another
///             thread while the variable was set listed the Null type to the player.
/// <c>ProductNullDeviceEnvLintTests</c> keeps product code off it.
///         </item>
///         <item>
///             <c>engine.Stop()</c> immediately after construction. Even the null
///             device is OPEN after the ctor, and the offline export refuses to run
///             while a device is open (it takes the chain's process barrier once per
///             chunk): the call returns <c>DCA_ERR_DEVICE</c>.
///         </item>
///         <item>
///             A silent warm-up block at the render rate. The rig prepares LAZILY on
///             first process, and a prepare landing in the middle of a render resets
///             every module's state halfway through the take.
///         </item>
///     </list>
///     <para>
///         Renders are serialised on one lock: there is one engine, one chain and one
///         seat, so two concurrent renders would interleave their nodes.
///     </para>
/// </summary>
public sealed class NullDeviceRigHost : IOfflineRigHost
{
    private const string NullDeviceType = "Null";
    private const int WarmupFrames = 512;
    private const int ChunkFrames = 4096;

    private readonly Lock _gate = new();
    private readonly IAppLogger? _logger;
    private readonly Func<JuceEngineConfig, JuceAudioEngine> _engineFactory;
    private JuceAudioEngine? _engine;
    private JuceDspChainService? _chain;
    private DcRigApplier? _applier;
    private string _describe = "not initialised";
    private bool _initialised;
    private bool _disposed;
    private double _preparedRate;

    /// <summary>Builds a host. Nothing native happens until the first
    /// <see cref="IsAvailable" /> or <see cref="Render" />.</summary>
    public NullDeviceRigHost(IAppLogger? logger = null) : this(logger, static config => new JuceAudioEngine(config))
    {
    }

    /// <summary>Test seam: <paramref name="engineFactory" /> builds the engine from the
    /// config this host chose (a test can hold the construction open to race a live
    /// engine against it).</summary>
    internal NullDeviceRigHost(IAppLogger? logger, Func<JuceEngineConfig, JuceAudioEngine> engineFactory)
    {
        _logger = logger;
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
    }

    /// <summary>The config the offline engine is built from. The Null device is a
    /// per-ENGINE flag, so nothing here is visible to any other engine.</summary>
    internal static JuceEngineConfig EngineConfig() => new()
    {
        OutputTypeName = NullDeviceType,
        EnableNullDevice = true,
        SampleRate = RigRenderer.RenderRate,
        BufferFrames = 512,
        EnableConsoleLog = false
    };

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                Initialise();
                return _chain is not null;
            }
        }
    }

    /// <inheritdoc />
    public string Describe()
    {
        lock (_gate)
        {
            Initialise();
            return _describe;
        }
    }

    /// <inheritdoc />
    public void Render(DcRigPreset rig, float[] left, float[] right, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length)
            throw new ArgumentException("Planar channels must be the same length.", nameof(right));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Initialise();
            if (_chain is null || _applier is null)
                throw new InvalidOperationException("The offline rig host is not available: " + _describe);

            // The lazy prepare must happen at THIS rate before a real sample goes
            // through, and it must happen on nodes we then throw away — otherwise the
            // first render after a rate change starts from a different state than
            // every later one, and determinism becomes a property of the SECOND call.
            if (Math.Abs(_preparedRate - sampleRate) > 1e-6)
            {
                Rebuild(rig);
                _chain.ProcessOfflineBuffer(new float[WarmupFrames], new float[WarmupFrames], WarmupFrames, sampleRate);
                _preparedRate = sampleRate;
            }

            Rebuild(rig);

            var chunk = Math.Min(ChunkFrames, Math.Max(left.Length, 1));
            var chunkL = new float[chunk];
            var chunkR = new float[chunk];
            for (var pos = 0; pos < left.Length; pos += chunk)
            {
                var n = Math.Min(chunk, left.Length - pos);
                Array.Copy(left, pos, chunkL, 0, n);
                Array.Copy(right, pos, chunkR, 0, n);
                if (!_chain.ProcessOfflineBuffer(chunkL, chunkR, n, sampleRate))
                {
                    throw new InvalidOperationException(
                        $"The native offline render refused the buffer at frame {pos}.");
                }

                Array.Copy(chunkL, 0, left, pos, n);
                Array.Copy(chunkR, 0, right, pos, n);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _chain = null;
            _applier = null;
            _engine?.Dispose();
            _engine = null;
        }
    }

    private void Rebuild(DcRigPreset rig)
    {
        foreach (var node in _chain!.GetNodes())
            _chain.RemoveNode(node.NodeId);

        var applied = _applier!.Apply(rig);
        if (applied.FailedOps > 0)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Offline rig render: {applied.FailedOps} chain operation(s) failed; the render is of a " +
                "chain that is not the preset.");
        }
    }

    private void Initialise()
    {
        if (_initialised) return;
        _initialised = true;

        try
        {
            // The Null device is requested for THIS engine through its config.
            // The native side promises the type never appears in a user-visible device
            // list, and the process environment is shared with the live engine, which
            // a device change / NAM 48 kHz request / watchdog can rebuild at any moment.
            _engine = _engineFactory(EngineConfig());

            // Offline means offline: the export refuses while a device is open.
            _engine.Stop();

            var chain = new JuceDspChainService(_logger);
            chain.LoadCatalog();
            chain.AttachEngine(_engine);
            _chain = chain;
            _applier = new DcRigApplier(chain, _logger);
            _describe = $"JUCE offline rig (null device, {RigRenderer.RenderRate} Hz)";
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                      or JuceAudioException or InvalidOperationException
                                      or EntryPointNotFoundException or TypeInitializationException)
        {
            _describe = "native audio engine unavailable: " + ex.Message;
            _logger?.Warning(AppLogCategory.Audio, "Offline rig host unavailable: " + ex.Message);
            _chain = null;
            _applier = null;
            _engine?.Dispose();
            _engine = null;
        }
    }
}

/// <summary>
///     Renders a dry DI through a <see cref="DcRigPreset" />, offline, deterministic,
///     level-matched — the "what would this rig sound like" half of the match lane.
///     <para>
///         <b>Deterministic is a promise, not a hope</b>, and it is what lets a search
///         lane compare two candidates at all: the same DI through the same rig is
///         bit-identical on every call, including immediately after a completely
///         different rig has been rendered. That costs a full chain rebuild per render
///         (see <see cref="IOfflineRigHost.Render" />), which is the right trade — a
///         reverb tail leaking from the previous candidate into this one's first
///         second is a scoring error that looks exactly like a better match.
///     </para>
///     <para>
///         <b>Mono in, mono out, and the left channel is the answer.</b> The DI is
///         duplicated into both planes and the LEFT plane is returned. The native
///         <c>channels: 1</c> convenience folds the output as <c>0.5·(L+R)</c>, which
///         silently CANCELS anything the rig put out of phase — a <c>widener</c>, a
///         ping-pong <c>delay</c>, a <c>rotary</c> — so a stereo effect would read as
///         a rig that did nothing. Taking one plane hears half the stereo image
///         instead of destroying it, and the target of every comparison is a mono stem
///         anyway.
///     </para>
///     <para>
///         Output level is <b>RMS-normalised to −20 dBFS with a peak guard</b>; see
///         <see cref="LevelMatch" /> for why RMS and not peak (short version: a
///         distorted render has 10–15 dB less crest, so peak matching would make every
///         dirty candidate arrive louder in every band and the metric would grade
///         partly on gain).
///     </para>
/// </summary>
public sealed class RigRenderer : ISynchronousRigRenderer
{
    /// <summary>Everything renders here: the engine's own default, the rate the stem
    /// cache decodes to, and the rate <see cref="ToneFeatureExtractor" /> analyses
    /// at — so the common path resamples nothing.</summary>
    public const int RenderRate = 48_000;

    private readonly IOfflineRigHost _host;

    /// <summary>Builds a renderer over a host. The renderer does not own the host's
    /// lifetime — DI owns the singleton, and a test owns its fake.</summary>
    public RigRenderer(IOfflineRigHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>True when the native engine is present. A caller that renders anyway
    /// gets an <see cref="InvalidOperationException" /> naming the reason.</summary>
    public bool IsAvailable => _host.IsAvailable;

    /// <summary>One line naming the backend, for a report header.</summary>
    public string Describe() => _host.Describe();

    /// <inheritdoc />
    public Task<MonoAudio> RenderAsync(MonoAudio di, DcRigPreset rig, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(di);
        ArgumentNullException.ThrowIfNull(rig);
        ct.ThrowIfCancellationRequested();

        // Offline rendering is a long CPU burn with no await in it; Task.Run keeps it
        // off whatever thread asked, and the host's lock keeps concurrent callers from
        // interleaving their chains.
        return Task.Run(() => Render(di, rig, ct), ct);
    }

    /// <inheritdoc />
    /// <remarks>The library's background grower calls this on its own
    /// BelowNormal thread; see <see cref="ISynchronousRigRenderer" />.</remarks>
    public MonoAudio Render(MonoAudio di, DcRigPreset rig, CancellationToken ct)
    {
        var source = MonoResampler.To(di, RenderRate);
        if (source.Samples.Length == 0) return new MonoAudio([], RenderRate);

        var left = new float[source.Samples.Length];
        var right = new float[source.Samples.Length];
        Array.Copy(source.Samples, left, left.Length);
        Array.Copy(source.Samples, right, right.Length);

        ct.ThrowIfCancellationRequested();
        _host.Render(rig, left, right, RenderRate);

        // A rig can produce a non-finite sample from a pathological param set, and a
        // single NaN poisons every statistic downstream — the same scrub-at-the-seam
        // rule the engine applies after every untrusted stage.
        for (var i = 0; i < left.Length; i++)
        {
            if (!float.IsFinite(left[i]))
                left[i] = 0f;
        }

        return LevelMatch.ToAnalysisLevel(new MonoAudio(left, RenderRate));
    }
}
