namespace DotCharter.Services.Export.Sng.Model;

/// <summary>The SNG note mask bits (Rocksmith2014.NET <c>NoteMask</c>).</summary>
internal static class RsNoteMask
{
    public const uint Chord = 0x00000002;
    public const uint Open = 0x00000004;
    public const uint FretHandMute = 0x00000008;
    public const uint Tremolo = 0x00000010;
    public const uint Harmonic = 0x00000020;
    public const uint PalmMute = 0x00000040;
    public const uint Slap = 0x00000080;
    public const uint Pluck = 0x00000100;
    public const uint HammerOn = 0x00000200;
    public const uint PullOff = 0x00000400;
    public const uint Slide = 0x00000800;
    public const uint Bend = 0x00001000;
    public const uint Sustain = 0x00002000;
    public const uint Tap = 0x00004000;
    public const uint PinchHarmonic = 0x00008000;
    public const uint Vibrato = 0x00010000;
    public const uint Mute = 0x00020000;
    public const uint Ignore = 0x00040000;
    public const uint LeftHand = 0x00080000;
    public const uint RightHand = 0x00100000;
    public const uint HighDensity = 0x00200000;
    public const uint UnpitchedSlide = 0x00400000;
    public const uint Single = 0x00800000;
    public const uint ChordNotes = 0x01000000;
    public const uint DoubleStop = 0x02000000;
    public const uint Accent = 0x04000000;
    public const uint Parent = 0x08000000;
    public const uint Child = 0x10000000;
    public const uint Arpeggio = 0x20000000;
    public const uint ChordPanel = 0x80000000;
}
