// FrameSnapshot.Notes: note and chord population - the per-sound flags, bends and the per-string link-next.
using DotCharter.Rendering.Data.Render;
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using IGuitarSound = DotCharter.Core.Domain.Guitar.IGuitarSound;

namespace DotCharter.Rendering.Data;

public partial class FrameSnapshot
{
    private NoteFlags GetNoteFlags(IGuitarSound snd, bool isSelected, bool isHovered, bool isAccent, int bendCount,
        bool isLinkedTarget)
    {
        var flags = NoteFlags.None;
        if (snd.SlideTo != null) flags |= NoteFlags.HasSlide;
        if (snd.UnpitchedSlide) flags |= NoteFlags.UnpitchedSlide;
        if (snd.Vibrato) flags |= NoteFlags.Vibrato;
        if (snd.Tremolo) flags |= NoteFlags.Tremolo;
        if (snd.LinkNext) flags |= NoteFlags.LinkNext;
        if (bendCount > 0) flags |= NoteFlags.HasBends;
        if (isSelected) flags |= NoteFlags.IsSelected;
        if (isHovered) flags |= NoteFlags.IsHovered;
        if (isAccent) flags |= NoteFlags.Accent;
        if (isLinkedTarget) flags |= NoteFlags.LinkPrevious;
        return flags;
    }

    private void UpdateNotes(IList<GuitarNote> sourceNotes, int startIdx, int endIdx, ref RenderNote[] targetArray,
        out int targetCount, HashSet<(long timeMs, int stringIdx)> linkedTargets, IList<BeatMarker> beats,
        SnapshotUpdateContext ctx, bool isGhost = false)
    {
        targetCount = endIdx >= startIdx && endIdx >= 0 ? endIdx - startIdx + 1 : 0;
        EnsureCapacity(ref targetArray, targetCount);
        var totalBendsReq = 0;
        for (var i = 0; i < targetCount; i++) totalBendsReq += sourceNotes[startIdx + i].BendValues.Count;
        EnsureCapacity(ref BendValues, BendValueCount + totalBendsReq);
        for (var i = 0; i < targetCount; i++)
        {
            var n = sourceNotes[startIdx + i];
            var bOffset = BendValueCount;
            var noteBends = n.BendValues;
            var bCount = noteBends.Count;
            // Indexed: foreach over ObservableCollection allocates a boxed
            // enumerator per bend-carrying note per frame.
            for (var bi = 0; bi < bCount; bi++)
            {
                var b = noteBends[bi];
                BendValues[BendValueCount++] = new RenderBendValue
                { Time = GetCachedPositionMs(b.Position, beats), Step = (float)b.Step };
            }
            var timeMs = GetCachedPositionMs(n.Position, beats);
            var endMs = GetCachedPositionMs(n.EndPosition, beats);
            var isLinked = !isGhost && linkedTargets.Contains(((long)Math.Round(timeMs), n.StringIndex));

            var isSelected = !isGhost && _selNotesCache.Contains(n);
            var isHovered = !isGhost && ctx.HoveredNote == n;

            var flags = GetNoteFlags(n, isSelected, isHovered, n.Accent, bCount, isLinked);
            if (isGhost) flags |= NoteFlags.IsGhost;

            targetArray[i] = new RenderNote
            {
                Time = timeMs, Sustain = endMs - timeMs, StringIndex = (byte)n.StringIndex,
                Fret = (byte)n.Fret, SlideTo = (byte)(n.SlideTo ?? 0), Mute = (byte)n.Mute, Hopo = (byte)n.Hopo,
                Harmonic = (byte)n.Harmonic, BassPicking = (byte)n.BassPicking,
                Flags = flags,
                BendOffset = bOffset, BendCount = (byte)bCount
            };
        }
    }

    private void UpdateChords(IList<Chord> sourceChords, int startIdx, int endIdx, ref RenderChord[] targetChordArray,
        out int targetChordCount, ref RenderChordNote[] targetNoteArray, out int targetNoteCount,
        HashSet<(long timeMs, int stringIdx)> linkedTargets, IList<BeatMarker> beats, SnapshotUpdateContext ctx,
        bool isGhost = false)
    {
        targetChordCount = endIdx >= startIdx && endIdx >= 0 ? endIdx - startIdx + 1 : 0;
        EnsureCapacity(ref targetChordArray, targetChordCount);
        targetNoteCount = 0;
        var expectedNotes = 0;
        for (var i = 0; i < targetChordCount; i++) expectedNotes += sourceChords[startIdx + i].ChordNotes.Count;
        EnsureCapacity(ref targetNoteArray, expectedNotes);
        var totalBendsReq = 0;
        for (var i = 0; i < targetChordCount; i++)
        {
            var cns = sourceChords[startIdx + i].ChordNotes;
            for (var j = 0; j < cns.Count; j++)
                totalBendsReq += cns[j].BendValues.Count;
        }
        EnsureCapacity(ref BendValues, BendValueCount + totalBendsReq);
        for (var i = 0; i < targetChordCount; i++)
        {
            var c = sourceChords[startIdx + i];
            var cFlags = ChordFlags.None;

            var isSelected = !isGhost && _selChordsCache.Contains(c);
            var isHovered = !isGhost && ctx.HoveredChord == c;

            if (isSelected) cFlags |= ChordFlags.IsSelected;
            if (isHovered) cFlags |= ChordFlags.IsHovered;
            if (c.Accent) cFlags |= ChordFlags.Accent;
            if (c.SplitIntoNotes) cFlags |= ChordFlags.SplitIntoNotes;
            if (isGhost) cFlags |= ChordFlags.IsGhost;
            if (!isGhost && ctx.RepeatChords.Contains(c)) cFlags |= ChordFlags.Repeat;
            if (!isGhost && ctx.EditTargetChords.Contains(c)) cFlags |= ChordFlags.EditTarget;

            // Cheap linear scan over the WINDOWED handshapes only (HandShapeStart
            // is already extended one back in BuildSnapshot, so the shape that
            // contains the window's left edge is included). Scanning the full
            // collection here was O(visibleChords x totalHandShapes) per frame
            // on large charts. A chord counts as inside the shape when its
            // onset falls within [Position, EndPosition).
            var hsLast = Math.Min(ctx.HandShapeEnd, ctx.ActiveHandShapes.Count - 1);
            for (var hi = Math.Max(0, ctx.HandShapeStart); hi <= hsLast; hi++)
            {
                var hs = ctx.ActiveHandShapes[hi];
                if (c.Position >= hs.Position && c.Position < hs.EndPosition)
                {
                    cFlags |= ChordFlags.InHandShape;
                    break;
                }
            }

            var timeMs = GetCachedPositionMs(c.Position, beats);
            targetChordArray[i] = new RenderChord
            {
                Time = timeMs, TemplateId = c.TemplateId, NoteOffset = targetNoteCount,
                NoteCount = (byte)c.ChordNotes.Count, Flags = cFlags
            };
            var cns2 = c.ChordNotes;
            for (var j = 0; j < cns2.Count; j++)
            {
                var cn = cns2[j];
                var bOffset = BendValueCount;
                var chordNoteBends = cn.BendValues;
                var bCount = chordNoteBends.Count;
                for (var bi = 0; bi < bCount; bi++)
                {
                    var b = chordNoteBends[bi];
                    BendValues[BendValueCount++] = new RenderBendValue
                    { Time = GetCachedPositionMs(b.Position, beats), Step = (float)b.Step };
                }
                var isLinked = !isGhost && linkedTargets.Contains(((long)Math.Round(timeMs), cn.StringIndex));

                // LinkNext is PER STRING (GetNoteFlags reads the chord note's own):
                // in a chord where one string slides on and another is held or
                // re-picked, only the linked string links.
                var noteFlags = GetNoteFlags(cn, isSelected, isHovered, false, bCount, isLinked);
                if (isGhost) noteFlags |= NoteFlags.IsGhost;

                // If this chord note links forward to a target that itself
                // carries a sustain-relevant technique, mark the flag so the
                // chord renderer doesn't suppress its connecting sustain.
                // The link target sits at (chordTime + cn.Sustain, sameString).
                if (!isGhost && cn.LinkNext)
                {
                    var endMsForLink = GetCachedPositionMs(c.Position + cn.Sustain, beats);
                    if (ctx.TechniqueOrigins.Contains(((long)Math.Round(endMsForLink), cn.StringIndex)))
                        noteFlags |= NoteFlags.LinkTargetHasTechnique;
                }

                var endMs = GetCachedPositionMs(c.Position + cn.Sustain, beats);
                targetNoteArray[targetNoteCount++] = new RenderChordNote
                {
                    StringIndex = (byte)cn.StringIndex, Fret = (byte)Math.Max(0, cn.Fret), Sustain = endMs - timeMs,
                    Mute = (byte)cn.Mute, Hopo = (byte)cn.Hopo, Harmonic = (byte)cn.Harmonic,
                    BassPicking = (byte)cn.BassPicking,
                    SlideTo = (byte)(cn.SlideTo ?? 0), Flags = noteFlags, BendOffset = bOffset, BendCount = (byte)bCount
                };
            }
        }
    }
}
