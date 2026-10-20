// FrameSnapshot.Build: the visible window - index ranges for notes, chords, anchors, beats and hand shapes, the link-landing walk, then the dispatch to Update.
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Rendering.Data;

public partial class FrameSnapshot
{
    /// <summary>
    ///     Establishes the visible window: computes index ranges for notes,
    ///     chords, anchors, beats and hand-shapes, builds the link-next target
    ///     hash, then dispatches to <see cref="Update" />.
    /// </summary>
    public static void BuildSnapshot(double viewStartMs, double viewEndMs,
        double currentTimeMs, FrameSnapshot snapshot, SnapshotUpdateContext ctx,
        HashSet<(long timeMs, int stringIdx)> linkedTargetsBuffer,
        double visibleRangeBufferMs = RenderingConstants.VisibleRangeBufferMs)
    {
        var notes = ctx.ActiveNotes;
        var chords = ctx.ActiveChords;
        var anchors = ctx.ActiveAnchors;
        var beats = ctx.ActiveBeats;
        var handShapes = ctx.ActiveHandShapes;

        TimelineMath.GetVisibleRange(notes, viewStartMs, viewEndMs, beats, visibleRangeBufferMs,
            out var nStart, out var nEnd);
        TimelineMath.GetVisibleRange(chords, viewStartMs, viewEndMs, beats, visibleRangeBufferMs,
            out var cStart, out var cEnd);
        TimelineMath.GetVisibleRange(anchors, viewStartMs, viewEndMs, beats, visibleRangeBufferMs,
            out var aStart, out var aEnd);
        TimelineMath.GetVisibleRange(handShapes, viewStartMs, viewEndMs, beats, visibleRangeBufferMs, out var hsStart,
            out var hsEnd);
        // A sustain longer than the buffer still crosses the view.
        var soundingFromMs = viewStartMs - visibleRangeBufferMs;
        nStart = TimelineMath.ExtendStartToSounding(notes, nStart, soundingFromMs, beats, static n => n.EndPosition);
        cStart = TimelineMath.ExtendStartToSounding(chords, cStart, soundingFromMs, beats, static c => c.EndPosition);
        hsStart = TimelineMath.ExtendStartToSounding(handShapes, hsStart, soundingFromMs, beats,
            static h => h.EndPosition);
        // Ghosts get the same windowing as the active collections — the
        // buffers are kept Position-sorted for exactly this (projecting the
        // full ghost chart per frame was the last O(total) per-frame path).
        TimelineMath.GetVisibleRange(ctx.GhostNotes, viewStartMs, viewEndMs, beats, visibleRangeBufferMs,
            out var gnStart, out var gnEnd);
        TimelineMath.GetVisibleRange(ctx.GhostChords, viewStartMs, viewEndMs, beats, visibleRangeBufferMs,
            out var gcStart, out var gcEnd);

        int bStart = 0, bEnd = beats.Count - 1;
        if (beats.Count > 0)
        {
            bStart = Math.Max(0,
                TimelineMath.BinarySearchBeats(beats, viewStartMs - visibleRangeBufferMs));
            bEnd = TimelineMath.BinarySearchBeats(beats, viewEndMs + visibleRangeBufferMs);
            if (bEnd < beats.Count - 1 &&
                beats[bEnd].Time < viewEndMs + visibleRangeBufferMs)
            {
                bEnd++;
            }
        }

        BuildLinkedTargets(ctx, linkedTargetsBuffer, beats, nStart, nEnd, cStart, cEnd);
        BuildTechniqueOrigins(ctx, beats, nStart, nEnd, cStart, cEnd);

        ctx.CurrentTimeMs = currentTimeMs;
        // Events/tones/lyrics/showlights window themselves inside Update —
        // they need the raw view range (plus buffer) rather than index pairs.
        ctx.ViewStartMs = viewStartMs;
        ctx.ViewEndMs = viewEndMs;
        ctx.VisibleRangeBufferMs = visibleRangeBufferMs;
        ctx.NoteStart = nStart;
        ctx.NoteEnd = nEnd;
        ctx.ChordStart = cStart;
        ctx.ChordEnd = cEnd;
        ctx.BeatStart = bStart;
        ctx.BeatEnd = bEnd;
        ctx.AnchorStart = Math.Max(0, aStart - 1);
        ctx.AnchorEnd = aEnd;
        ctx.HandShapeStart = Math.Max(0, hsStart - 1);
        ctx.HandShapeEnd = hsEnd;
        ctx.GhostNoteStart = gnStart;
        ctx.GhostNoteEnd = gnEnd;
        ctx.GhostChordStart = gcStart;
        ctx.GhostChordEnd = gcEnd;
        ctx.LinkedTargets = linkedTargetsBuffer;

        snapshot.Update(ctx);
    }

    private static void BuildLinkedTargets(SnapshotUpdateContext ctx,
        HashSet<(long timeMs, int stringIdx)> buffer, IList<BeatMarker> beats,
        int nStart, int nEnd, int cStart, int cEnd)
    {
        buffer.Clear();
        CollectLinkTargets(ctx.ActiveNotes, ctx.ActiveChords, beats, Math.Max(0, nStart - 10), nEnd + 10,
            Math.Max(0, cStart - 10), cEnd + 10, buffer);
        // PreviewNotes/PreviewChords are IList<T> bound to ObservableCollections;
        // the walk below is indexed, so an empty preview allocates nothing.
        CollectLinkTargets(ctx.PreviewNotes, ctx.PreviewChords, beats, 0, ctx.PreviewNotes.Count - 1, 0,
            ctx.PreviewChords.Count - 1, buffer);
    }

    /// <summary>
    ///     Adds the (rounded ms, string) every link in the given index ranges LANDS
    ///     on. The ONE walk both snapshot builders use: the 2D timeline's
    ///     <see cref="BuildSnapshot" /> and the 3D highway's
    ///     <c>Preview3DRenderDriver</c>, so neither keeps a copy that could drift.
    ///     <para>
    ///         <b>Per string.</b> A chord note links only when IT carries
    ///         <c>LinkNext</c>. OR-ing in <c>Chord.HasLinkNext()</c> would be wrong: in a
    ///         chord where one string slides on and another is re-picked (5+8,
    ///         the 5 slides to 6 and the 8 is struck again) the re-picked 8 would be
    ///         flagged a link landing — and the 3D highway, which drops every head
    ///         of a landing, would draw no head for it. Every importer already copies a
    ///         chord-level <c>linkNext</c> onto its chord notes (<c>RsXmlSongReader</c>),
    ///         and Rocksmith stores links per chord note too.
    ///     </para>
    /// </summary>
    /// <remarks>Indexed throughout: the collections are <c>ObservableCollection</c>s
    /// behind <see cref="IList{T}" />, whose enumerator would box once per frame.
    /// The ranges are clamped here, so callers may pass a widened window.</remarks>
    public static void CollectLinkTargets(IList<GuitarNote> notes, IList<Chord> chords, IList<BeatMarker> beats,
        int noteFrom, int noteTo, int chordFrom, int chordTo, HashSet<(long timeMs, int stringIdx)> buffer)
    {
        for (var i = Math.Max(0, noteFrom); i <= Math.Min(notes.Count - 1, noteTo); i++)
        {
            // A chain LANDS at the end of the linking sound's sustain, so a
            // sound with no sustain has no landing — registering one made the
            // sound its OWN target, the projection flagged it LinkPrevious, and
            // the renderers then drew nothing at all for it. See
            // `LinksIntoALaterSound`.
            var n = notes[i];
            if (LinksIntoALaterSound(n.LinkNext, n.Position, n.EndPosition))
                buffer.Add(((long)Math.Round(n.EndPosition.GetPositionMs(beats)), n.StringIndex));
        }

        for (var i = Math.Max(0, chordFrom); i <= Math.Min(chords.Count - 1, chordTo); i++)
        {
            var chord = chords[i];
            var cns = chord.ChordNotes;
            for (var j = 0; j < cns.Count; j++)
            {
                var cn = cns[j];
                var end = chord.Position + cn.Sustain;
                if (LinksIntoALaterSound(cn.LinkNext, chord.Position, end))
                    buffer.Add(((long)Math.Round(end.GetPositionMs(beats)), cn.StringIndex));
            }
        }
    }

    /// <summary>
    ///     Whether a sound actually links INTO a later sound: it must declare
    ///     <paramref name="linkNext" /> AND carry a sustain for the chain to land
    ///     on. <c>EndPosition == Position + Sustain</c>, so an unsustained sound's
    ///     "target" is itself — which the projection reads back as
    ///     <c>NoteFlags.LinkPrevious</c>, i.e. "a previous sound already drew this
    ///     head". `NoteHeadRenderer` then skips the note (and there is no tail to
    ///     draw either), and `ChordRenderer` — which ORs the flag across a chord's
    ///     notes — drops the box, the fill and every head.
    /// </summary>
    private static bool LinksIntoALaterSound(bool linkNext, FractionalPosition start, FractionalPosition end)
        => linkNext && end > start;

    /// <summary>
    /// Records the (timeMs, stringIdx) of every visible note (and chord note)
    /// that carries a sustain-relevant technique. <see cref="UpdateChords"/>
    /// uses this to flag the *previous* chord note that links into one of
    /// these targets so the chord-renderer keeps drawing its connecting
    /// sustain instead of suppressing it.
    /// </summary>
    private static void BuildTechniqueOrigins(SnapshotUpdateContext ctx, IList<BeatMarker> beats,
        int nStart, int nEnd, int cStart, int cEnd)
    {
        var notes = ctx.ActiveNotes;
        var chords = ctx.ActiveChords;
        var origins = ctx.TechniqueOrigins;
        origins.Clear();

        static bool NoteHasTechnique(IGuitarSound s)
            => s.SlideTo != null || s.UnpitchedSlide || s.Vibrato || s.Tremolo || s.BendValues.Count > 0;

        for (var i = Math.Max(0, nStart - 10); i <= Math.Min(notes.Count - 1, nEnd + 10); i++)
        {
            var n = notes[i];
            if (NoteHasTechnique(n))
                origins.Add(((long)Math.Round(n.Position.GetPositionMs(beats)), n.StringIndex));
        }

        for (var i = Math.Max(0, cStart - 10); i <= Math.Min(chords.Count - 1, cEnd + 10); i++)
        {
            var c = chords[i];
            var cTimeMs = (long)Math.Round(c.Position.GetPositionMs(beats));
            var cns = c.ChordNotes;
            for (var j = 0; j < cns.Count; j++)
            {
                var cn = cns[j];
                if (NoteHasTechnique(cn))
                    origins.Add((cTimeMs, cn.StringIndex));
            }
        }

        // Indexed for the same reason as CollectLinkTargets: a foreach over these
        // IList<T>-typed ObservableCollections boxes one enumerator per collection
        // per frame, unconditionally.
        var previewNotes = ctx.PreviewNotes;
        for (var i = 0; i < previewNotes.Count; i++)
        {
            var n = previewNotes[i];
            if (NoteHasTechnique(n))
                origins.Add(((long)Math.Round(n.Position.GetPositionMs(beats)), n.StringIndex));
        }

        var previewChords = ctx.PreviewChords;
        for (var i = 0; i < previewChords.Count; i++)
        {
            var c = previewChords[i];
            var cTimeMs = (long)Math.Round(c.Position.GetPositionMs(beats));
            // Chord.ChordNotes is an ObservableCollection too — same boxing, once
            // per preview chord. The identical walk in CollectLinkTargets is
            // already indexed; this one was simply missed.
            var cns = c.ChordNotes;
            for (var j = 0; j < cns.Count; j++)
            {
                var cn = cns[j];
                if (NoteHasTechnique(cn))
                    origins.Add((cTimeMs, cn.StringIndex));
            }
        }
    }
}
