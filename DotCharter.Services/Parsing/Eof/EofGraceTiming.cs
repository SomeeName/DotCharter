using System.Numerics;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>The shared wire contract for single-note grace identities.</summary>
internal static class EofGraceTiming
{
    public const string SynthesizedNoteName = "[DotCharter grace]";
    public const uint MaxLengthMs = 200;

    /// <summary>Names that become the reserved identity after EOF's chord-name normalization.</summary>
    public static bool IsReservedName(string name)
    {
        var normalized = name.Trim();
        if (normalized.EndsWith(RsChordDisplaySuffix.Nop, StringComparison.Ordinal))
            normalized = normalized[..^RsChordDisplaySuffix.Nop.Length];
        return normalized == SynthesizedNoteName;
    }

    /// <summary>A bounded thirty-second at the serialized onset's canonical grid position.</summary>
    public static uint LengthAt(FractionalPosition position, IList<BeatMarker> beats)
    {
        var serializedMs = EofExportSession.ToMsU32(TimelineMath.CalculateTimeMs(position, beats));
        var onset = TimelineMath.PositionFromImportedMs(serializedMs, beats);
        var startMs = TimelineMath.CalculateTimeMs(onset, beats);
        var previousMs = TimelineMath.CalculateTimeMs(onset - new FractionalPosition(0, new Fraction(1, 8)), beats);
        return (uint)Math.Clamp(Math.Round(startMs - previousMs), 1, MaxLengthMs);
    }

    /// <summary>
    /// The wire length of a target displaced behind its single-string ornament. Zero
    /// means "no sustain" and nothing else, so a sustained target keeps at least one
    /// millisecond even when the ornament is as long as the whole authored sustain.
    /// </summary>
    public static uint DisplacedLength(uint authoredMs, uint ornamentMs) =>
        authoredMs == 0 ? 0 : authoredMs > ornamentMs ? authoredMs - ornamentMs : 1;

    /// <summary>
    /// The inverse of <see cref="DisplacedLength"/>, shared by the fold and its owner
    /// preview: the ornament's length goes back onto a sustained target only. The one
    /// millisecond <see cref="DisplacedLength"/> keeps for a sustain up to the ornament's
    /// length folds back to exactly the ornament, so a sustain equal to the ornament (on
    /// the beat grid, the common case) is the identity; a sustain one millisecond longer
    /// than the ornament also writes one millisecond and comes back one millisecond short.
    /// </summary>
    public static uint FoldedLength(uint targetMs, uint ornamentMs) =>
        targetMs switch
        {
            0 => 0,
            1 => Math.Max(1u, ornamentMs),
            _ => targetMs + ornamentMs
        };

    /// <summary>
    /// The raw-pair contract shared with final export validation; file attachments
    /// are an additional import veto in EofGraceAttachments. Requires exact marker;
    /// one non-ghost string, no assigned finger/legacy/extended technique; positive,
    /// distinct unmuted frets; only LINKNEXT + RS_NOTATION + the correct slide direction;
    /// exact canonical duration (1..200 ms). The target on this string and
    /// difficulty must be at the exact end/fret. Chord targets may retain authored
    /// names; reserved identities are never names, and single targets stay unnamed.
    /// Target ghosts and authored target techniques remain intact; intervening other
    /// strings/difficulties do not change the link. EofGracePairing validates any
    /// interleaved identities before reconstruction. Reconstruction cannot overflow.
    /// </summary>
    public static int TargetIndex(IReadOnlyList<EofRawProGuitarNote> notes, int index,
        IList<BeatMarker> beats, bool hasFingering = false)
    {
        var ornament = notes[index];
        if (!HasIdentity(ornament, hasFingering) || !HasCanonicalSlide(ornament, beats)) return -1;
        var end = (ulong)ornament.PositionMs + ornament.LengthMs;
        for (var j = index + 1; j < notes.Count; j++)
        {
            var target = notes[j];
            if (target.PositionMs > end) break;
            if (target.Difficulty != ornament.Difficulty ||
                ((target.NoteBitmask & ~target.GhostBitmask) & ornament.NoteBitmask) == 0)
            {
                continue;
            }
            if (MatchesTarget(ornament, target)) return j;
        }
        return -1;
    }

    private static bool HasIdentity(EofRawProGuitarNote note, bool hasFingering)
    {
        return note.Name == SynthesizedNoteName && !hasFingering && note.Difficulty < 255 &&
               BitOperations.IsPow2((uint)note.NoteBitmask) && note.GhostBitmask == 0 &&
               note.LegacyMask == 0 && note.ExtendedFlags == 0 &&
               note.LengthMs is > 0 and <= MaxLengthMs;
    }

    private static bool HasCanonicalSlide(EofRawProGuitarNote note, IList<BeatMarker> beats)
    {
        var s = BitOperations.TrailingZeroCount((uint)note.NoteBitmask);
        var origin = note.Frets[s];
        var target = note.SlideEndFret;
        if (origin is 0 or >= EofConstants.FretMutedBit || target is 0 or >= EofConstants.FretMutedBit || origin == target) return false;
        var flags = EofConstants.PgFlagLinkNext | EofConstants.PgFlagRsNotation |
                    (origin < target ? EofConstants.PgFlagSlideUp : EofConstants.PgFlagSlideDown);
        return note.Flags == flags && note.LengthMs == LengthAt(TimelineMath.PositionFromImportedMs(note.PositionMs, beats), beats);
    }

    private static bool MatchesTarget(EofRawProGuitarNote ornament, EofRawProGuitarNote target)
    {
        var s = BitOperations.TrailingZeroCount((uint)ornament.NoteBitmask);
        return (target.Name.Length == 0 ||
                BitOperations.PopCount((uint)(target.NoteBitmask & ~target.GhostBitmask)) > 1 && !IsReservedName(target.Name)) &&
               ((target.NoteBitmask & ~target.GhostBitmask) & ornament.NoteBitmask) == ornament.NoteBitmask &&
               (target.GhostBitmask & ~target.NoteBitmask) == 0 &&
               target.Frets[s] == ornament.SlideEndFret &&
               (ulong)ornament.PositionMs + ornament.LengthMs == target.PositionMs &&
               target.LengthMs <= uint.MaxValue - ornament.LengthMs;
    }
}
