// ChartSoundRouting: the pure rules behind the Mixer's "Sound source" and "Route instrument" choices — which generator plays the chart, whether it goes through the rig, and which live instrument seats' rigs it enters.
namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>
///     Which of the player's live instrument seats the chart sound enters (ABI v51
///     ABI v51 seat masks). The GUITAR seat is seat 0 (the one input every setup
///     has), the BASS seat is seat 1 (the second jack of a "two separate
///     instruments" input, <c>RigConfig.InputChannelsAreSeparateGuitars</c>).
///     Values are persisted: append, never renumber.
/// </summary>
public enum ChartSoundSeatRoute
{
    /// <summary>The guitar seat only: the original path, and the default.</summary>
    GuitarOnly = 0,

    /// <summary>The bass seat only.</summary>
    BassOnly = 1,

    /// <summary>Both seats' rigs, each running its own tone.</summary>
    GuitarAndBass = 2,
}

/// <summary>
///     The Mixer's four-way "Sound source" (design D). It is TWO switches,
///     "Use my instrument" and "Through rig", read as one value so the choice and
///     the toggles can never disagree. Values are the bit pair
///     <c>(useInstrument ? 2 : 0) + (throughRig ? 1 : 0)</c>; append, never renumber.
///     <list type="bullet">
///         <item><see cref="SongTones" />: the built-in voice through the chart's own
///         tones (the default; <c>AppConfig.ChartSynthPlaysOwnRig</c> false).</item>
///         <item><see cref="OwnRig" />: the built-in voice through the player's own
///         board (<c>ChartSynthPlaysOwnRig</c> true).</item>
///         <item><see cref="MyInstrument" />: the loaded VST instrument, after the mix
///         (<c>RouteInstrumentToRig</c> false).</item>
///         <item><see cref="InstrumentThroughRig" />: the loaded VST instrument into
///         the rig (<c>RouteInstrumentToRig</c> true).</item>
///     </list>
/// </summary>
public enum ChartSoundSource
{
    SongTones = 0,
    OwnRig = 1,
    MyInstrument = 2,
    InstrumentThroughRig = 3,
}

/// <summary>Pure mapping between the Mixer's routing choices and the engine's seat masks.</summary>
public static class ChartSoundRouting
{
    /// <summary>The guitar seat: seat 0, where the chart synth's own source lives.</summary>
    public const int GuitarSeat = 0;

    /// <summary>The bass seat: the second live input.</summary>
    public const int BassSeat = 1;

    /// <summary>The engine-start route: the guitar seat only, what every build before
    /// seat routing did. A user who never touches the choice hears no change.</summary>
    public const ChartSoundSeatRoute DefaultSeatRoute = ChartSoundSeatRoute.GuitarOnly;

    /// <summary>A guitar / bass choice means something only with a bass seat: two
    /// live inputs.</summary>
    public static bool CanRouteToSeats(int liveSeats) => liveSeats > BassSeat;

    /// <summary>
    ///     The engine seat mask for <paramref name="route" /> (bit p = seat p). With one
    ///     live seat every choice is the guitar seat, which is the seat that exists:
    ///     the routed instrument would otherwise fall back to the master, out of the rig.
    /// </summary>
    public static uint SeatMask(ChartSoundSeatRoute route, int liveSeats)
    {
        const uint guitar = 1u << GuitarSeat;
        const uint bass = 1u << BassSeat;
        if (!CanRouteToSeats(liveSeats)) return guitar;
        return route switch
        {
            ChartSoundSeatRoute.BassOnly => bass,
            ChartSoundSeatRoute.GuitarAndBass => guitar | bass,
            _ => guitar,
        };
    }

    /// <summary>
    ///     The engine seat mask for the built-in voice's chart source. "Song tones" is
    ///     the guitar seat's rig carrying the chart's tones (the editor's tone preview
    ///     drives that ONE rig), so with <paramref name="songTones" /> the source plays
    ///     into the guitar seat whatever <paramref name="route" /> says: routed into the
    ///     bass seat it would sound through the bass player's own board instead. With
    ///     the player's own rig it follows <see cref="SeatMask" />.
    /// </summary>
    public static uint ChartSourceSeatMask(ChartSoundSeatRoute route, int liveSeats, bool songTones) =>
        songTones ? 1u << GuitarSeat : SeatMask(route, liveSeats);

    /// <summary>A stored value outside the enum reads as the default.</summary>
    public static ChartSoundSeatRoute Normalize(ChartSoundSeatRoute route) =>
        Enum.IsDefined(route) ? route : DefaultSeatRoute;

    /// <summary>"Use my instrument": the loaded VST instrument plays, not the built-in voice.</summary>
    public static bool UsesInstrument(ChartSoundSource source) =>
        source is ChartSoundSource.MyInstrument or ChartSoundSource.InstrumentThroughRig;

    /// <summary>"Through rig": the player's own rig (the built-in voice) or the rig
    /// input (the instrument).</summary>
    public static bool ThroughRig(ChartSoundSource source) =>
        source is ChartSoundSource.OwnRig or ChartSoundSource.InstrumentThroughRig;

    /// <summary>The one value the two toggles describe.</summary>
    public static ChartSoundSource Compose(bool useInstrument, bool throughRig) =>
        (ChartSoundSource)((useInstrument ? 2 : 0) + (throughRig ? 1 : 0));
}
