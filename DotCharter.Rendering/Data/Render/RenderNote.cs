namespace DotCharter.Rendering.Data.Render;

public struct RenderNote
{
    public double Time;
    public double Sustain;
    public int BendOffset;
    public NoteFlags Flags;
    public byte StringIndex;
    public byte Fret;
    public byte Mute;
    public byte Hopo;
    public byte Harmonic;
    public byte BassPicking;
    public byte SlideTo;
    public byte BendCount;
    /// <summary>The bend (steps) this note's tail starts at because a linked sound
    /// ends there (the snapshot's link pass, <c>FrameSnapshot.JoinLinkedTails</c>);
    /// 0 when unlinked.</summary>
    public float BendCarry;
}

public struct RenderChord
{
    public double Time;
    public int TemplateId;
    public int NoteOffset;
    public byte NoteCount;
    public ChordFlags Flags;
}

public struct RenderChordNote
{
    public double Sustain;
    public int BendOffset;
    public NoteFlags Flags;
    public byte StringIndex;
    public byte Fret;
    public byte Mute;
    public byte Hopo;
    public byte Harmonic;
    public byte BassPicking;
    public byte SlideTo;
    public byte BendCount;
    /// <summary>The bend (steps) this tail starts at because a linked sound ends
    /// there; 0 when unlinked.</summary>
    public float BendCarry;
}
