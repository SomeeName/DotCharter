// FrameSnapshot.Links: after projection, makes a linked tail JOIN the tail it links into — the carried bend and the vibrato / tremolo ramps.
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Rendering.Data;

public partial class FrameSnapshot
{
    // (rounded start ms, string) of every projected LINK TARGET -> its index: >= 0 a
    // note, < 0 the complement of a chord-note index. Reused frame to frame; Clear()
    // keeps the buckets, so a steady frame allocates nothing.
    private readonly Dictionary<(long TimeMs, int StringIndex), int> _linkTargetIndex = [];

    /// <summary>
    ///     Each tail was drawn on its own, so a link only met its target by luck: a
    ///     plain note tied into a vibrato note started the vibrato mid-swing, and a bend
    ///     tied into a held note dropped back to the string at the link (owner report).
    ///     For every projected sound that links into a projected target, this:
    ///     <list type="bullet">
    ///         <item>carries the sound's final bend into the target
    ///         (<see cref="RenderNote.BendCarry" />): held where the target has no bend
    ///         of its own, moved from where it has — the tie keeps the pitch;</item>
    ///         <item>eases a vibrato / tremolo in on a target whose source lacks it
    ///         (<see cref="NoteFlags.RampIn" />) and out on a source whose target lacks
    ///         it (<see cref="NoteFlags.RampOut" />); two vibrato tails already join,
    ///         because the wobble's phase is absolute time.</item>
    ///     </list>
    ///     Walks notes and chords merged in TIME order, so a chain carries a bend
    ///     through every link. Active notes and chords only — previews and ghosts are
    ///     drawn faint and on their own.
    /// </summary>
    internal void JoinLinkedTails()
    {
        _linkTargetIndex.Clear();
        for (var i = 0; i < NoteCount; i++)
        {
            if ((Notes[i].Flags & NoteFlags.LinkPrevious) != 0)
                _linkTargetIndex[((long)Math.Round(Notes[i].Time), Notes[i].StringIndex)] = i;
        }

        for (var c = 0; c < ChordCount; c++)
        {
            var chord = Chords[c];
            for (var k = 0; k < chord.NoteCount; k++)
            {
                var j = chord.NoteOffset + k;
                if ((ChordNotes[j].Flags & NoteFlags.LinkPrevious) != 0)
                    _linkTargetIndex[((long)Math.Round(chord.Time), ChordNotes[j].StringIndex)] = ~j;
            }
        }

        if (_linkTargetIndex.Count == 0) return;

        int ni = 0, ci = 0;
        while (ni < NoteCount || ci < ChordCount)
        {
            if (ci >= ChordCount || (ni < NoteCount && Notes[ni].Time <= Chords[ci].Time))
            {
                ref var n = ref Notes[ni++];
                JoinInto(n.Time, n.Sustain, n.StringIndex, ref n.Flags,
                    EndBend(n.BendOffset, n.BendCount, n.BendCarry));
            }
            else
            {
                var chord = Chords[ci++];
                for (var k = 0; k < chord.NoteCount; k++)
                {
                    ref var cn = ref ChordNotes[chord.NoteOffset + k];
                    JoinInto(chord.Time, cn.Sustain, cn.StringIndex, ref cn.Flags,
                        EndBend(cn.BendOffset, cn.BendCount, cn.BendCarry));
                }
            }
        }
    }

    /// <summary>The bend a tail ends on: its last bend point, else what it carried in.</summary>
    private float EndBend(int bendOffset, int bendCount, float carry)
        => bendCount > 0 ? BendValues[bendOffset + bendCount - 1].Step : carry;

    private void JoinInto(double time, double sustain, int stringIndex, ref NoteFlags sourceFlags, float endBend)
    {
        if ((sourceFlags & NoteFlags.LinkNext) == 0 || sustain <= 0) return;
        if (!_linkTargetIndex.TryGetValue(((long)Math.Round(time + sustain), stringIndex), out var t)) return;

        ref var targetFlags = ref t >= 0 ? ref Notes[t].Flags : ref ChordNotes[~t].Flags;
        if (t >= 0) Notes[t].BendCarry = endBend;
        else ChordNotes[~t].BendCarry = endBend;

        const NoteFlags wobble = NoteFlags.Vibrato | NoteFlags.Tremolo;
        if ((targetFlags & wobble & ~(sourceFlags & wobble)) != 0) targetFlags |= NoteFlags.RampIn;
        if ((sourceFlags & wobble & ~(targetFlags & wobble)) != 0) sourceFlags |= NoteFlags.RampOut;
    }
}
