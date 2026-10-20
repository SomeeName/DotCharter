// RsGameTables: Rocksmith 2014's own amp chains and knob curves, built from the PLAYER'S install at run time — the repo holds none of them.
namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     The tables the RS tone converter reads from the game itself: the managed index of
///     every amp's soundbank chain (<see cref="AmpModels" />, the rows of the native
///     <c>rsamp</c> table, in its order) and the knob curves of the gear mapped through
///     the game's own numbers (<see cref="Curves" />).
///     <para>
///         <b>None of it is in the repository</b>. An optional plugin
///         (<c>IGameInstallReader</c> in DotCharter.Services) reads it from the player's own
///         Rocksmith 2014 install once in the background, caches the result
///         in the user data folder and publishes it here (<see cref="Publish" />). Until
///         then — and always without an install or a key file — <see cref="Current" /> is
///         <see cref="Empty" />, and the converter maps every gear through its generic,
///         label-reading path.
///     </para>
/// </summary>
internal sealed class RsGameTables
{
    private static readonly AsyncLocal<RsGameTables?> FlowScope = new();
    private static volatile RsGameTables? _process;

    private readonly Lazy<Dictionary<string, int>> _ampIndex;

    public RsGameTables(IReadOnlyList<RsAmpModel> ampModels, RsGameCurveSet curves, byte[]? nativeAmpTable = null)
    {
        AmpModels = ampModels;
        Curves = curves;
        NativeAmpTable = nativeAmpTable;
        _ampIndex = new Lazy<Dictionary<string, int>>(() =>
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < AmpModels.Count; i++) d[AmpModels[i].Key] = i;
            return d;
        });
    }

    /// <summary>No tables: the state without an install.</summary>
    public static RsGameTables Empty { get; } = new([], RsGameCurveSet.Empty);

    /// <summary>The tables in use: the calling flow's <see cref="Scope" />, else the process's.</summary>
    public static RsGameTables Current => FlowScope.Value ?? _process ?? Empty;

    /// <summary>Every amp the game ships, in the native <c>rsamp</c> table's order; empty
    /// without an install.</summary>
    public IReadOnlyList<RsAmpModel> AmpModels { get; }

    /// <summary>The game's knob curves; each set null when not available.</summary>
    public RsGameCurveSet Curves { get; }

    /// <summary>
    ///     The amp chains the native <c>rsamp</c> core runs, as the blob JuceSharp's
    ///     <c>dca_rsamp_set_table</c> takes (ABI v45, <c>RsAmpChainTable</c>): one row per
    ///     <see cref="AmpModels" /> entry, in the same order. Null without an install.
    ///     <c>JuceAudioPlaybackService</c> hands it to the engine when it is published and
    ///     again on every engine rebuild; until then every <c>rsamp</c> module passes audio
    ///     through.
    /// </summary>
    public byte[]? NativeAmpTable { get; }

    /// <summary>Raised (on the publishing thread) after <see cref="Publish" /> made new
    /// tables the process's.</summary>
    public static event Action<RsGameTables>? Published;

    /// <summary>The process's tables, ignoring any flow <see cref="Scope" />.</summary>
    public static RsGameTables Process => _process ?? Empty;

    /// <summary>Whether nothing at all was read.</summary>
    public bool IsEmpty => AmpModels.Count == 0 && Curves.IsEmpty;

    /// <summary>The row index of an amp key, or false.</summary>
    public bool TryGetAmpIndex(string gearKey, out int index) => _ampIndex.Value.TryGetValue(gearKey, out index);

    /// <summary>Makes <paramref name="tables" /> the process's tables.</summary>
    public static void Publish(RsGameTables tables)
    {
        _process = tables;
        Published?.Invoke(tables);
    }

    /// <summary><paramref name="tables" /> for the calling async flow only, so nothing
    /// running in parallel sees them: a test's tables, or a derivation that must not
    /// depend on what was loaded when it ran (<c>RsConverterIdiom</c>). Dispose to restore.</summary>
    public static IDisposable Scope(RsGameTables tables)
    {
        var previous = FlowScope.Value;
        FlowScope.Value = tables;
        return new Restore(previous);
    }

    private sealed class Restore(RsGameTables? previous) : IDisposable
    {
        public void Dispose() => FlowScope.Value = previous;
    }
}
