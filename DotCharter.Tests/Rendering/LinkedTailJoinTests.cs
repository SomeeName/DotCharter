using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D highway's link pass (<c>FrameSnapshot.JoinLinkedTails</c>, #106): a tail
///     that links into the next note JOINS it — the bend carries across the link, and a
///     vibrato / tremolo eases in or out where only one side has it.
/// </summary>
public class LinkedTailJoinTests
{
    private static RenderNote Note(double time, double sustain, NoteFlags flags, int bendOffset = 0, int bendCount = 0)
        => new()
        {
            Time = time, Sustain = sustain, StringIndex = 2, Fret = 16, Flags = flags,
            BendOffset = bendOffset, BendCount = (byte)bendCount
        };

    private static FrameSnapshot Notes(RenderNote[] notes, RenderBendValue[]? bends = null)
    {
        var s = new FrameSnapshot
        {
            Notes = notes,
            NoteCount = notes.Length,
            BendValues = bends ?? [],
            BendValueCount = bends?.Length ?? 0
        };
        s.JoinLinkedTails();
        return s;
    }

    [Fact]
    public void APlainNoteTiedIntoAVibratoNote_EasesTheVibratoIn()
    {
        var s = Notes([
            Note(300, 400, NoteFlags.LinkNext),
            Note(700, 900, NoteFlags.LinkPrevious | NoteFlags.Vibrato)
        ]);
        Assert.True(s.Notes[1].Flags.HasFlag(NoteFlags.RampIn));
        Assert.False(s.Notes[0].Flags.HasFlag(NoteFlags.RampOut));
    }

    [Fact]
    public void AVibratoNoteTiedIntoAPlainNote_EasesItOut_AndTwoVibratosNeedNoRamp()
    {
        var outOf = Notes([
            Note(300, 400, NoteFlags.LinkNext | NoteFlags.Vibrato),
            Note(700, 900, NoteFlags.LinkPrevious)
        ]);
        Assert.True(outOf.Notes[0].Flags.HasFlag(NoteFlags.RampOut));

        var both = Notes([
            Note(300, 400, NoteFlags.LinkNext | NoteFlags.Vibrato),
            Note(700, 900, NoteFlags.LinkPrevious | NoteFlags.Vibrato)
        ]);
        Assert.False(both.Notes[0].Flags.HasFlag(NoteFlags.RampOut));
        Assert.False(both.Notes[1].Flags.HasFlag(NoteFlags.RampIn));
    }

    /// <summary>A bend carries through a chain of ties: the second and third notes
    /// have no bend of their own and hold the first one's full step.</summary>
    [Fact]
    public void ABend_CarriesThroughAChainOfTies()
    {
        var s = Notes([
            Note(300, 400, NoteFlags.LinkNext | NoteFlags.HasBends, bendOffset: 0, bendCount: 1),
            Note(700, 300, NoteFlags.LinkPrevious | NoteFlags.LinkNext),
            Note(1000, 500, NoteFlags.LinkPrevious)
        ], [new RenderBendValue { Time = 500, Step = 1f }]);
        Assert.Equal(1f, s.Notes[1].BendCarry);
        Assert.Equal(1f, s.Notes[2].BendCarry);
        Assert.Equal(0f, s.Notes[0].BendCarry);
    }

    /// <summary>A chord note ties into a single note the same way, and a note that is
    /// not linked is left exactly as projected.</summary>
    [Fact]
    public void AChordNoteTiesIntoANote_AndAnUnlinkedNoteIsUntouched()
    {
        var s = new FrameSnapshot
        {
            Chords = [new RenderChord { Time = 300, NoteOffset = 0, NoteCount = 1 }],
            ChordCount = 1,
            ChordNotes = [new RenderChordNote { StringIndex = 2, Fret = 16, Sustain = 400, Flags = NoteFlags.LinkNext }],
            ChordNoteCount = 1,
            Notes =
            [
                Note(700, 900, NoteFlags.LinkPrevious | NoteFlags.Tremolo),
                Note(2000, 500, NoteFlags.Vibrato)
            ],
            NoteCount = 2
        };
        s.JoinLinkedTails();
        Assert.True(s.Notes[0].Flags.HasFlag(NoteFlags.RampIn));
        Assert.Equal(NoteFlags.Vibrato, s.Notes[1].Flags);
        Assert.Equal(0f, s.Notes[1].BendCarry);
    }

    /// <summary>The envelope is 0 at a ramped end (so the tails meet on the centre
    /// line), 1 past the ramp, and 1 everywhere on an unramped tail.</summary>
    [Fact]
    public void TheWobbleEnvelope_StartsAtZeroOnARampedEnd()
    {
        Assert.Equal(0f, SustainRenderer.WobbleEnvelope(NoteFlags.RampIn, 700, 900, 700), 5);
        Assert.Equal(1f, SustainRenderer.WobbleEnvelope(NoteFlags.RampIn, 700, 900, 900), 5);
        Assert.Equal(0f, SustainRenderer.WobbleEnvelope(NoteFlags.RampOut, 300, 400, 700), 5);
        Assert.Equal(1f, SustainRenderer.WobbleEnvelope(NoteFlags.None, 300, 400, 300), 5);
    }
}
