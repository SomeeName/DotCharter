using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export.Rs7.Suite;

/// <summary>The techniques the suite exercises (#307). Slide and unpitched slide go 3 frets up, a bend
/// is a whole step up and back to 0, every sustaining technique rings for <see cref="Rs7SuiteSpec.SustainBeats" />.</summary>
[Flags]
internal enum Tech
{
    None = 0,
    Sustain = 1,
    Slide = 2,
    UnpitchedSlide = 4,
    Bend = 8,
    Vibrato = 16,
    NaturalHarmonic = 32,
    PinchHarmonic = 64,
    PalmMute = 128,
    HammerOn = 256,
    PullOff = 512,
    Tremolo = 1024,
    Accent = 2048,
    Tap = 4096,
    Slap = 8192,
    Pop = 16384
}

/// <summary>One sounding note, spelled in PACKAGE string order (rs7string's: guitar 0-5 = E A D G B e, 6 = low B,
/// 7 = low F#; bass 0-3 = E A D G, 4 = low B, 5 = high C), the way the issue and the mod's log talk.</summary>
internal readonly record struct Col(int String, int Fret, int Finger = -1, Tech Tech = Tech.None);

/// <summary>A single note (no <paramref name="Chord" /> name) or a chord / double stop (named), at a whole beat.
/// <paramref name="Level" /> is the lowest difficulty level that contains it.</summary>
internal sealed record Ev(int Beat, Col[] Cols, string? Chord = null, int Level = 0);

internal sealed record Sec(string Name, int Beat);

/// <summary>An instrument shape: guitar 6/7/8 strings or bass 4/5/6 strings.</summary>
internal sealed record Inst(string Key, ArrangementType Type, int Strings)
{
    public bool Bass => Type == ArrangementType.Bass;

    public bool Extended => Bass ? Strings > 4 : Strings > 6;

    /// <summary>Package string indices, lowest pitch first.</summary>
    public int[] Order => Bass
        ? Strings switch { 4 => [0, 1, 2, 3], 5 => [4, 0, 1, 2, 3], _ => [4, 0, 1, 2, 3, 5] }
        : Strings switch { 6 => [0, 1, 2, 3, 4, 5], 7 => [6, 0, 1, 2, 3, 4, 5], _ => [7, 6, 0, 1, 2, 3, 4, 5] };

    /// <summary>The package indices rs7string adds to the stock strings.</summary>
    public int[] Extras => Order.Where(s => Bass ? s >= 4 : s >= 6).ToArray();

    public int[] Normal => Order.Except(Extras).ToArray();

    public static readonly Inst G6 = new("g6", ArrangementType.Lead, 6);
    public static readonly Inst G7 = new("g7", ArrangementType.Lead, 7);
    public static readonly Inst G8 = new("g8", ArrangementType.Lead, 8);
    public static readonly Inst B4 = new("b4", ArrangementType.Bass, 4);
    public static readonly Inst B5 = new("b5", ArrangementType.Bass, 5);
    public static readonly Inst B6 = new("b6", ArrangementType.Bass, 6);
}

/// <summary>One arrangement. <paramref name="Tuning" /> holds one semitone offset per string in PACKAGE order
/// against the package open strings (<c>Rs7StringLayout.OpenMidi</c> / <c>BassOpenMidi</c>).</summary>
internal sealed record Part(
    Inst Inst, ArrangementType Role, int[] Tuning, Ev[] Events, Sec[]? Sections = null, int Levels = 1)
{
    public Part(Inst inst, int[] tuning, Ev[] events, Sec[]? sections = null, int levels = 1)
        : this(inst, inst.Type, tuning, events, sections, levels)
    {
    }
}

/// <summary>One chart (one package): <paramref name="Name" /> names what it tests.</summary>
internal sealed record ChartSpec(string Name, string Tests, Part[] Parts);

internal static class Rs7SuiteSpec
{
    public const int SustainBeats = 2;
    public const int SlideFrets = 3;
    public const double Bpm = 120;
    public const double BeatMs = 60_000 / Bpm;
    public const int LeadInBeats = 4;
    public const int MinBeats = 64;
    public const int MaxBeats = 119;
    public const int LowestMidi = 19; // G0

    public static double Seconds(int beat) => beat * BeatMs / 1000.0;

    public static string Fmt(double seconds) => seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The package open-string MIDI (offset 0) of string <paramref name="s" />.</summary>
    public static int Open(Inst inst, int s) => inst.Bass ? Rs7StringLayout.BassOpenMidi(s) : Rs7StringLayout.OpenMidi(s);

    public static int Midi(Inst inst, int[] tuning, int s, int fret) => Open(inst, s) + tuning[s] + fret;

    public static string NoteName(int midi)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[midi % 12]}{midi / 12 - 1}";
    }

    public static int[] Zero(Inst inst) => new int[inst.Strings];

    /// <summary>The same offset on every string.</summary>
    public static int[] All(Inst inst, int offset) => Enumerable.Repeat(offset, inst.Strings).ToArray();

    /// <summary>Every string at <paramref name="all" /> except package string <paramref name="s" />.</summary>
    public static int[] With(Inst inst, int all, int s, int offset)
    {
        var t = All(inst, all);
        t[s] = offset;
        return t;
    }

    public static bool IsExtraString(Inst inst, int s) => inst.Extras.Contains(s);

    /// <summary>The chart (lowest-first) string index of package string <paramref name="s" />.</summary>
    public static int ToDomain(Inst inst, int s) => Rs7StringLayout.ToDomain(s, inst.Strings, inst.Type);
}
