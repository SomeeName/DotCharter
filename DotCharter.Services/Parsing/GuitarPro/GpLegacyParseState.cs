using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
/// Per-import scratchpad for the legacy GP3/4/5 binary parser. Keeps the
/// transient bend / let-ring buffers off the importer itself so the importer
/// can focus on byte-stream walking and arrangement assembly.
///
/// Lifecycle: created once per <c>ParseFile</c> call, cleared up front, then
/// fed by the per-note effect readers and consumed by the beat-flushing pass.
/// Buffers are keyed by in-flight <see cref="GuitarNote"/> instances or by
/// track, voice and string so lookahead and emitted sounds keep separate state.
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

    public HashSet<GuitarNote> RawDeadNotes { get; } = [];

    /// <summary>Notes with the ghost-note bit (note header 0x04), consumed when the note is emitted.</summary>
    public HashSet<GuitarNote> GhostNotes { get; } = [];

    /// <summary>Notes with the staccato bit (note-effects-2 0x01), consumed when the
    /// note is placed: a staccato imports as a strike, sustain 0.</summary>
    public HashSet<GuitarNote> StaccatoNotes { get; } = [];

    private readonly Dictionary<(int TrackId, int Voice), GpBendContinuation> _bendContinuations = [];

    /// <summary>The untied bend-continuation tracker of one (track, voice lane), the slot gpif keeps one per.</summary>
    public GpBendContinuation BendContinuationFor(int trackId, int voice)
    {
        if (!_bendContinuations.TryGetValue((trackId, voice), out var continuation))
            _bendContinuations[(trackId, voice)] = continuation = new GpBendContinuation();
        return continuation;
    }

    /// <summary>The bend-continuation view of a note about to be emitted as <paramref name="sound" />
    /// (consumes its ghost flag; read before its bend points are consumed).</summary>
    public GpBendContinuation.Struck StrikeBend(GuitarNote parsed, IGuitarSound sound)
    {
        PendingBendOffsets.TryGetValue(parsed, out var points);
        return new GpBendContinuation.Struck(sound, GpBendContinuation.LegacyBend(points),
            TiedContinuations.Contains(parsed), GhostNotes.Remove(parsed));
    }

    /// <summary>Feeds an emitted chord to its lane's tracker. Call it after every string's tie retarget,
    /// so a written tie already shows as <c>LinkNext</c> on the chord before.</summary>
    public void ObserveBendChord(int trackId, int voice, Chord chord, IReadOnlyList<GpBendContinuation.Struck> struck)
        => BendContinuationFor(trackId, voice).ObserveChord(chord, struck);

    /// <summary>Feeds an emitted single note to its lane's tracker; an <paramref name="expanded" /> trill
    /// is not one sound, so it breaks the string's line instead.</summary>
    public void ObserveBendNote(int trackId, int voice, GuitarNote note, bool expanded)
    {
        var continuation = BendContinuationFor(trackId, voice);
        var struck = StrikeBend(note, note);
        if (expanded) continuation.Forget(note.StringIndex);
        else continuation.ObserveNote(note, struck.Bend, struck.TieDestination, struck.Ghost);
    }
    private readonly Dictionary<GuitarNote, (int Flags, bool Version5)> _tieSlides = [];
    private readonly Dictionary<(int TrackId, int Voice, int StringIndex), TieChain> _tieChains = [];

    private sealed class TieChain(bool dead, GuitarNote first)
    {
        public bool Dead { get; } = dead;
        public List<IGuitarSound> Sounds { get; } = [first];
    }

    public bool ApplySlide(GpLegacyBinaryReader reader, GuitarNote note, bool version5, int capo)
    {
        var flags = reader.ReadSByte();
        var directional = version5 ? (flags & 12) != 0 && !GpPickScrape.IsPickScrape(flags) : flags is 3 or 4;
        if (directional && TiedContinuations.Contains(note)) _tieSlides[note] = (flags, version5);
        return GpLegacyNoteEffectsHelpers.ApplySlide(flags, note, version5, capo);
    }

    private readonly Dictionary<int, int> _droppedByTrack = [];

    /// <summary>Whether a note the reader cannot place (a string the track lacks, or a negative
    /// fret) is dropped; each drop is counted for the import report.</summary>
    public bool DropsNote(int trackId, bool unplaceable)
    {
        if (unplaceable) _droppedByTrack[trackId] = DroppedNotes(trackId) + 1;
        return unplaceable;
    }

    /// <summary>The notes <see cref="DropsNote" /> dropped from track <paramref name="trackId" />.</summary>
    public int DroppedNotes(int trackId) => _droppedByTrack.GetValueOrDefault(trackId);

    public void TrackReadNote(int trackId, int voice, GuitarNote note)
    {
        var key = (trackId, voice, note.StringIndex);
        var dead = RawDeadNotes.Remove(note) && !GpPickScrape.IsScrape(note);
        var hasSlide = _tieSlides.Remove(note, out var slide);
        if (!TiedContinuations.Contains(note) || !_tieChains.TryGetValue(key, out var chain))
            _tieChains[key] = chain = new TieChain(dead, note);
        else
            chain.Sounds.Add(note);
        if (!chain.Dead) return;
        if (hasSlide) GpDeadNoteSlide.Mark(note, slide.Version5 ? slide.Flags : slide.Flags == 3 ? 4 : 8);
        if (!chain.Sounds.Any(GpDeadNoteSlide.IsPending)) return;
        foreach (var sound in chain.Sounds) sound.Mute = MuteType.Full;
    }

    public void RetargetReadSound((int TrackId, int Voice, int StringIndex) key, GuitarNote parsed, IGuitarSound stored)
    {
        if (!_tieChains.TryGetValue(key, out var chain)) return;
        var index = chain.Sounds.FindIndex(s => ReferenceEquals(s, parsed));
        if (index >= 0) chain.Sounds[index] = stored;
    }

    public void Reset()
    {
        PendingBendOffsets.Clear();
        PendingLetRingNotes.Clear();
        LetRingByLevel.Clear();
        TiedContinuations.Clear();
        HopoOrigins.Clear();
        PendingHopoDestinations.Clear();
        LastSoundOnString.Clear();
        RawDeadNotes.Clear();
        GhostNotes.Clear();
        StaccatoNotes.Clear();
        _bendContinuations.Clear();
        _tieSlides.Clear();
        _tieChains.Clear();
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
