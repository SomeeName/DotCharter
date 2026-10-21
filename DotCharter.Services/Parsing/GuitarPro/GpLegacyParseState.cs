using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
/// Per-import scratchpad for the legacy GP3/4/5 binary parser. Keeps the
/// transient bend / let-ring buffers off the importer itself so the importer
/// can focus on byte-stream walking and arrangement assembly.
///
/// Lifecycle: created once per <c>ParseFile</c> call, cleared up front, then
/// fed by the per-note effect readers and consumed by the beat-flushing pass.
/// All state is reference-keyed by the in-flight <see cref="GuitarNote"/>
/// instances built during the parse.
/// </summary>
internal sealed class GpLegacyParseState
{
    /// <summary>
    /// Per-note bend points captured while reading note effects, keyed by
    /// the <see cref="GuitarNote"/> object that owns them. Consumed when the
    /// beat is flushed into the level (single-note or chord-note path).
    /// </summary>
    public Dictionary<GuitarNote, List<(int Offset, double Step)>> PendingBendOffsets { get; } = [];

    /// <summary>
    /// Notes that have a let-ring effect flag set. Promoted to a beat-level
    /// marker (see <see cref="LetRingByLevel"/>) when the beat is flushed.
    /// </summary>
    public HashSet<GuitarNote> PendingLetRingNotes { get; } = [];

    /// <summary>
    /// Beat positions that should receive a let-ring HandShape during
    /// post-processing, grouped per level (one per imported track).
    /// </summary>
    public Dictionary<ChartLevel, HashSet<FractionalPosition>> LetRingByLevel { get; } = [];

    /// <summary>
    /// Notes whose binary note-type was 2 ("tied to the PREVIOUS note").
    /// The legacy format marks the tie on the CONTINUATION, but the chart
    /// model marks it on the ORIGIN (<c>LinkNext</c>) — the beat-flushing
    /// pass consumes this set to retarget the flag onto the previous sound
    /// on the same string. (Setting <c>LinkNext</c> on the continuation
    /// itself was the bug that made legacy ties never merge and linked
    /// unrelated following notes.)
    /// </summary>
    public HashSet<GuitarNote> TiedContinuations { get; } = [];

    /// <summary>
    /// Notes carrying the GP3-5 slur bit (note-effect fx1 0x02), which marks the
    /// ORIGIN of a hammer-on/pull-off. Consumed when the note is emitted, to arm
    /// <see cref="PendingHopoDestinations"/>.
    /// </summary>
    public HashSet<GuitarNote> HopoOrigins { get; } = [];

    /// <summary>
    /// (track, voice lane, string) keys whose last sound was a slur origin: the
    /// next non-tied sound on that string receives the HammerOn flag (the
    /// destination), exactly where gpif's HopoDestination puts it.
    /// </summary>
    public HashSet<(int TrackId, int Voice, int StringIndex)> PendingHopoDestinations { get; } = [];

    /// <summary>
    /// The most recent EMITTED sound per (track, voice lane, string) — the
    /// tie-retarget target. Keyed for the whole file walk because the walk is
    /// measure-major and ties cross barlines by design.
    /// </summary>
    public Dictionary<(int TrackId, int Voice, int StringIndex), IGuitarSound> LastSoundOnString { get; } = [];

    public void Reset()
    {
        PendingBendOffsets.Clear();
        PendingLetRingNotes.Clear();
        LetRingByLevel.Clear();
        TiedContinuations.Clear();
        HopoOrigins.Clear();
        PendingHopoDestinations.Clear();
        LastSoundOnString.Clear();
    }

    public HashSet<FractionalPosition> GetLetRingSet(ChartLevel level)
    {
        if (!LetRingByLevel.TryGetValue(level, out var set))
        {
            set = [];
            LetRingByLevel[level] = set;
        }
        return set;
    }

    public void AppendBendPoints(GpLegacyBinaryReader reader, GuitarNote note)
    {
        PendingBendOffsets.TryGetValue(note, out var list);
        var listRef = list;
        GpBendChunkReader.AppendPoints(reader, ref listRef);
        PendingBendOffsets[note] = listRef!;
    }
}
