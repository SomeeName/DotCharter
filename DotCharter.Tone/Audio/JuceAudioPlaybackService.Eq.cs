// Partial: the six-band parametric EQ (ABI v29). PushEqTo is the one place a section becomes engine calls; the rebuild replay lives in .EngineLifecycle.cs.
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    // ---- Parametric EQ (ABI v29) ------------------------------------------

    /// <summary>The last section pushed, kept so a device change / watchdog
    /// recovery can replay it onto the rebuilt engine. Without this the EQ
    /// silently reverts the first time the user changes their interface — the
    /// exact shape of the co-op routing bug this codebase already paid for.</summary>
    private Equalizer.EqSection? _eqSection;

    /// <summary>True whenever there is an engine at all. The exports are v29 and
    /// the wrapper's ABI check refuses to load an older DLL, so an engine that
    /// exists has them.</summary>
    public bool SupportsParametricEq => _engine != null;

    public void ApplyEq(Equalizer.EqSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        _eqSection = section;
        var engine = _engine;
        if (engine is null) return;
        PushEqTo(engine, section, _logger);
    }

    /// <summary>
    ///     How many bands the ENGINE is running, or −1 when there is no engine
    ///     running the EQ to ask.
    ///     <para>
    ///         <b>"No section has been pushed" is a −1, not a 0.</b> The engine is
    ///         created lazily — the first audio load or the first transport call —
    ///         so an app that has opened no device at all, and an engine that came
    ///         up after the module was constructed and has therefore never been
    ///         handed a section, both answer this honestly with "nothing to ask".
    ///         The native export itself is unchanged and still authoritative the
    ///         moment there IS something to ask (see the ABI v29 rule in the root
    ///         AGENTS.md): what this distinguishes is "the engine says zero" from
    ///         "there is no engine running this section". They read identically on
    ///         a badge and they mean opposite things — the owner reported a "0/6"
    ///         standing over lit band chips.
    ///     </para>
    /// </summary>
    public int EngineEqActiveCount
    {
        get
        {
            var engine = _engine;
            return engine is null || _eqSection is null ? -1 : engine.GetEqActiveCount();
        }
    }

    /// <summary>
    ///     Reads the pre-EQ magnitude spectrum, or 0 bins when there is nothing
    ///     to read.
    ///     <para>
    ///         <b>The length is checked HERE.</b> Since the wave-4 wrapper audit
    ///         <c>JuceAudioEngine.ReadEqSpectrum</c> THROWS
    ///         <see cref="ArgumentOutOfRangeException" /> for a buffer outside
    ///         <c>[MinEqSpectrumBins, MaxEqSpectrumBins]</c> — deliberately, so a
    ///         host cannot mistake a refused call for a silent engine. This
    ///         service is the app's boundary and its contract (shared with
    ///         <c>IAudioPlaybackService</c>'s default <c>=> 0</c>) is "how many
    ///         bins were written": a metering poll running at ~15 Hz must not
    ///         become an unhandled exception on the UI thread because a caller
    ///         sized its buffer wrong. The bad size is LOGGED once per call and
    ///         answered with 0, which every reader already handles (the inspector
    ///         plot draws nothing unless it got exactly what it asked for).
    ///     </para>
    /// </summary>
    public int ReadEqSpectrum(float[] destinationDb)
    {
        ArgumentNullException.ThrowIfNull(destinationDb);
        if (destinationDb.Length is < JuceAudioEngine.MinEqSpectrumBins
            or > JuceAudioEngine.MaxEqSpectrumBins)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"EQ spectrum read refused: {destinationDb.Length} bins is outside the engine's " +
                $"[{JuceAudioEngine.MinEqSpectrumBins}, {JuceAudioEngine.MaxEqSpectrumBins}].");
            return 0;
        }

        return _engine?.ReadEqSpectrum(destinationDb) ?? 0;
    }

    /// <summary>The one place a section becomes engine calls. Shared by
    /// <see cref="ApplyEq" /> and the engine-rebuild replay so the two can never
    /// send different things.</summary>
    private static void PushEqTo(JuceAudioEngine engine, Equalizer.EqSection section, IAppLogger? logger)
    {
        // The app's EqBandType/EqRoute are a numeric MIRROR of the ABI enums, so
        // the casts below are enum->enum, not magic ints. That the two agree
        // member for member is pinned by Services/Audio/EngineAbiEnumMirrorTests
        // — an `(int)` cast here would have compiled just as happily against a
        // silent renumber.
        WriteEqBands(section,
            (i, type, freqHz, q, gainDb, enabled) => engine.SetEqBand(i, type, freqHz, q, gainDb, enabled),
            logger);

        engine.SetEqSolo(section.SoloedBand);
        engine.SetEqRoute((JuceEqRoute)section.Route);
        engine.SetEqTrim((float)section.TrimDb);
        // LAST, deliberately: every call above republishes the coefficient set,
        // so flipping the power first would leave one block audible with a
        // half-written band list.
        engine.SetEqEnabled(section.IsEnabled);
    }

    /// <summary>One band write, as a delegate, so the loop below is reachable
    /// without an engine — and therefore without an audio device.</summary>
    internal delegate void EqBandWriter(int index, JuceEqBandType type, float freqHz, float q, float gainDb,
        bool enabled);

    /// <summary>
    ///     Writes every band of <paramref name="section" /> through
    ///     <paramref name="write" /> and returns how many landed.
    ///     <para>
    ///         <b>NO BAND MAY STOP THE OTHERS.</b> <c>SetEqBand</c> throws on a
    ///         bad index, and this loop is the engine-REBUILD replay path —
    ///         a device change, the NAM 48 kHz request, a watchdog recovery. A
    ///         throw on band <c>i</c> would abandon bands <c>i+1…n</c> AND the
    ///         solo / route / trim / power calls after the loop, leaving exactly
    ///         the half-written coefficient set the ordering above was designed
    ///         to prevent — and leaving the section switched OFF on the rebuilt
    ///         engine while the UI kept drawing the user's curve, which is the
    ///         "replay on rebuild" bug this codebase has already shipped twice.
    ///         So: bands past the engine's fixed count are dropped BEFORE the
    ///         loop (they cannot be written by any ordering), and each write is
    ///         guarded so a rejected band costs one log line and nothing else.
    ///     </para>
    /// </summary>
    internal static int WriteEqBands(Equalizer.EqSection section, EqBandWriter write, IAppLogger? logger)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(write);

        var count = Math.Min(section.Bands.Count, JuceAudioEngine.EqBandCount);
        if (section.Bands.Count > JuceAudioEngine.EqBandCount)
        {
            logger?.Warning(AppLogCategory.Audio,
                $"EQ section carries {section.Bands.Count} bands but the engine runs " +
                $"{JuceAudioEngine.EqBandCount}; the extra bands were not pushed.");
        }

        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var band = section.Bands[i].Clamped();
            try
            {
                write(i, (JuceEqBandType)band.Type, (float)band.FrequencyHz, (float)band.Q,
                    (float)band.GainDb, band.Enabled);
                written++;
            }
            catch (Exception ex)
            {
                logger?.Error(AppLogCategory.Audio,
                    $"EQ band {i} was rejected by the engine ({ex.GetType().Name}: {ex.Message}); " +
                    "continuing with the remaining bands so the replay completes.", ex);
            }
        }

        return written;
    }
}
