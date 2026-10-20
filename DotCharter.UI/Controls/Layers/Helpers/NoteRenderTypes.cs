using Avalonia.Media;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.UI.Controls.Layers.Helpers;

public struct DeferredLabel
{
    public string Text;
    public float X;
    public float Y;
    public float Scale;
    public float TextYOffset;
    public Color TextColor;
    public Color BgColor;
    public Color OutlineColor;
    public bool HasBox;
}

// Unifies rendering properties for both standalone Notes and ChordNotes
public readonly ref struct NoteDrawData
{
    public readonly double Time;
    public readonly double Sustain;
    public readonly int StringIndex;
    public readonly int Fret;
    public readonly byte Mute;
    public readonly byte Hopo;
    public readonly byte Harmonic;
    public readonly byte SlideTo;
    public readonly byte BassPicking;
    public readonly NoteFlags Flags;
    public readonly bool IsPreview;
    public readonly int BendOffset;
    public readonly int BendCount;

    /// <summary>A repeat chord's note: its head, number and sustain draw at
    /// <see cref="RepeatChordInk.Dim" />, its technique glyphs at full alpha.</summary>
    public readonly bool Dimmed;

    public NoteDrawData(double time, double sustain, int stringIndex, int fret, byte mute, byte hopo, byte harmonic,
        byte slideTo, NoteFlags flags, bool isPreview, int bendOffset, int bendCount, byte bassPicking = 0,
        bool dimmed = false)
    {
        Time = time;
        Sustain = sustain;
        StringIndex = stringIndex;
        Fret = fret;
        Mute = mute;
        Hopo = hopo;
        Harmonic = harmonic;
        SlideTo = slideTo;
        BassPicking = bassPicking;
        Flags = flags;
        IsPreview = isPreview;
        BendOffset = bendOffset;
        BendCount = bendCount;
        Dimmed = dimmed;
    }
}
