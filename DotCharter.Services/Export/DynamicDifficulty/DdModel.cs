using System.Xml.Linq;

namespace DotCharter.Services.Export.DynamicDifficulty;

// The RS2014 XML shapes the dynamic-difficulty generator works on: a mirror of the
// classes in Rocksmith2014.NET's Rocksmith2014.XML (MIT, Copyright (c) 2020 Tapio
// Malmberg), reduced to what the generator reads and writes. Times are whole
// milliseconds, as that library keeps them. See THIRD-PARTY-NOTICES.md § 2.1.

/// <summary>The note flags, bit for bit as Rocksmith2014.XML's <c>NoteMask</c>.</summary>
[Flags]
internal enum DdNoteMask : ushort
{
    None = 0,
    LinkNext = 1 << 0,
    Accent = 1 << 1,
    HammerOn = 1 << 2,
    Harmonic = 1 << 3,
    Ignore = 1 << 4,
    FretHandMute = 1 << 5,
    PalmMute = 1 << 6,
    PullOff = 1 << 7,
    Tremolo = 1 << 8,
    PinchHarmonic = 1 << 9,
    PickDirection = 1 << 10,
    Slap = 1 << 11,
    Pluck = 1 << 12,
    RightHand = 1 << 13,
}

/// <summary>The chord flags, bit for bit as Rocksmith2014.XML's <c>ChordMask</c>.</summary>
[Flags]
internal enum DdChordMask : byte
{
    None = 0,
    LinkNext = 1 << 0,
    Accent = 1 << 1,
    FretHandMute = 1 << 2,
    HighDensity = 1 << 3,
    Ignore = 1 << 4,
    PalmMute = 1 << 5,
    Hopo = 1 << 6,
}

/// <summary>A note or a chord note.</summary>
internal sealed class DdNote
{
    public DdNote() { }

    /// <summary>A copy (Rocksmith2014.XML's <c>Note(Note)</c>).</summary>
    public DdNote(DdNote other)
    {
        Mask = other.Mask;
        Time = other.Time;
        String = other.String;
        Fret = other.Fret;
        Sustain = other.Sustain;
        LeftHand = other.LeftHand;
        SlideTo = other.SlideTo;
        SlideUnpitchTo = other.SlideUnpitchTo;
        Tap = other.Tap;
        Vibrato = other.Vibrato;
        MaxBend = other.MaxBend;
        if (other.BendValues != null) BendValues = [.. other.BendValues];
        Source = other.Source;
    }

    public DdNoteMask Mask { get; set; }
    public int Time { get; set; }
    public int String { get; set; }
    public int Fret { get; set; }
    public int Sustain { get; set; }
    public int LeftHand { get; set; } = -1;
    public int SlideTo { get; set; } = -1;
    public int SlideUnpitchTo { get; set; } = -1;
    public int Tap { get; set; }
    public int Vibrato { get; set; }
    public float MaxBend { get; set; }

    /// <summary>The bend curve; null when the note has none (the library's <c>BendValues</c>).</summary>
    public List<DdBendValue>? BendValues { get; set; }

    /// <summary>The element the note was read from; its other attributes and its bend
    /// values are written back unchanged. Null for a note the generator made.</summary>
    public XElement? Source { get; set; }

    public bool IsLinkNext
    {
        get => Has(DdNoteMask.LinkNext);
        set => Set(DdNoteMask.LinkNext, value);
    }

    public bool IsHammerOn
    {
        get => Has(DdNoteMask.HammerOn);
        set => Set(DdNoteMask.HammerOn, value);
    }

    public bool IsPullOff
    {
        get => Has(DdNoteMask.PullOff);
        set => Set(DdNoteMask.PullOff, value);
    }

    public bool IsTremolo
    {
        get => Has(DdNoteMask.Tremolo);
        set => Set(DdNoteMask.Tremolo, value);
    }

    public bool IsPinchHarmonic
    {
        get => Has(DdNoteMask.PinchHarmonic);
        set => Set(DdNoteMask.PinchHarmonic, value);
    }

    public bool IsIgnore
    {
        get => Has(DdNoteMask.Ignore);
        set => Set(DdNoteMask.Ignore, value);
    }

    public bool IsFretHandMute => Has(DdNoteMask.FretHandMute);
    public bool IsHarmonic => Has(DdNoteMask.Harmonic);
    public bool IsPalmMute => Has(DdNoteMask.PalmMute);
    public bool IsVibrato => Vibrato != 0;
    public bool IsBend => BendValues?.Count > 0;
    public bool IsSlide => SlideTo != -1;
    public bool IsUnpitchedSlide => SlideUnpitchTo != -1;
    public bool IsTap => Tap != 0;
    public bool IsHopo => Has(DdNoteMask.HammerOn) || Has(DdNoteMask.PullOff);

    private bool Has(DdNoteMask flag) => (Mask & flag) != 0;

    private void Set(DdNoteMask flag, bool on) => Mask = on ? Mask | flag : Mask & ~flag;
}

internal sealed class DdChord
{
    public DdChord() { }

    /// <summary>A copy with copied chord notes (Rocksmith2014.XML's <c>Chord(Chord)</c>).</summary>
    public DdChord(DdChord other)
    {
        Mask = other.Mask;
        Time = other.Time;
        ChordId = other.ChordId;
        Source = other.Source;
        if (other.ChordNotes != null) ChordNotes = [.. other.ChordNotes.Select(cn => new DdNote(cn))];
    }

    public DdChordMask Mask { get; set; }
    public int Time { get; set; }
    public int ChordId { get; set; }

    /// <summary>Null when the chord has no chord notes, as in the library.</summary>
    public List<DdNote>? ChordNotes { get; set; }

    public XElement? Source { get; set; }

    public bool HasChordNotes => ChordNotes?.Count > 0;

    public bool IsLinkNext
    {
        get => (Mask & DdChordMask.LinkNext) != 0;
        set => Mask = value ? Mask | DdChordMask.LinkNext : Mask & ~DdChordMask.LinkNext;
    }

    public bool IsIgnore
    {
        get => (Mask & DdChordMask.Ignore) != 0;
        set => Mask = value ? Mask | DdChordMask.Ignore : Mask & ~DdChordMask.Ignore;
    }

    public bool IsFretHandMute => (Mask & DdChordMask.FretHandMute) != 0;
    public bool IsPalmMute => (Mask & DdChordMask.PalmMute) != 0;
    public bool IsAccent => (Mask & DdChordMask.Accent) != 0;
}

internal sealed class DdHandShape
{
    public DdHandShape(int chordId, int startTime, int endTime)
    {
        ChordId = chordId;
        StartTime = startTime;
        EndTime = endTime;
    }

    public DdHandShape(DdHandShape other) : this(other.ChordId, other.StartTime, other.EndTime) { }

    public int ChordId { get; set; }
    public int StartTime { get; set; }
    public int EndTime { get; set; }
}

/// <summary>A bend value: whole milliseconds and a step in half tones.</summary>
internal sealed record DdBendValue(int Time, float Step);

/// <summary>An anchor. <see cref="Source" /> keeps the element it was read from.</summary>
internal sealed class DdAnchor
{
    public DdAnchor(int time, int fret, int width, XElement? source = null)
    {
        Time = time;
        Fret = fret;
        Width = width;
        Source = source;
    }

    public int Time { get; set; }
    public int Fret { get; set; }

    /// <summary>The library keeps the width as an sbyte, read from a float string.</summary>
    public int Width { get; set; }

    public XElement? Source { get; }
}

/// <summary>A chord template. <see cref="Frets" /> and <see cref="Fingers" /> are
/// <see cref="DdArrangement.StringSlots" /> wide (six for anything up to six strings,
/// as in the library).</summary>
internal sealed class DdChordTemplate
{
    public DdChordTemplate(string name, string displayName, int[] fingers, int[] frets)
    {
        Name = name;
        DisplayName = displayName;
        Fingers = fingers;
        Frets = frets;
    }

    public string Name { get; set; }
    public string DisplayName { get; set; }
    public int[] Fingers { get; }
    public int[] Frets { get; }

    /// <summary>The element it was read from, written back unchanged; null for a new one.</summary>
    public XElement? Source { get; init; }
}

internal sealed class DdLevel
{
    public DdLevel(int difficulty) => Difficulty = difficulty;

    public int Difficulty { get; }
    public List<DdNote> Notes { get; init; } = [];
    public List<DdChord> Chords { get; init; } = [];
    public List<DdAnchor> Anchors { get; init; } = [];
    public List<DdHandShape> HandShapes { get; init; } = [];
}

internal sealed class DdBeat(int time, int measure)
{
    public int Time { get; set; } = time;
    public int Measure { get; } = measure;
}

internal sealed class DdSection(string name, int time, XElement? source = null)
{
    public string Name { get; } = name;
    public int Time { get; set; } = time;
    public XElement? Source { get; } = source;
}

internal sealed class DdEvent(string code, int time, XElement? source = null)
{
    public string Code { get; set; } = code;
    public int Time { get; set; } = time;
    public XElement? Source { get; } = source;
}

internal sealed class DdPhrase(string name, int maxDifficulty, bool isSolo, XElement? source = null)
{
    public string Name { get; set; } = name;
    public int MaxDifficulty { get; } = maxDifficulty;
    public bool IsSolo { get; } = isSolo;
    public XElement? Source { get; } = source;
}

/// <summary>Hero levels; all zero means none (the library writes no element then).</summary>
internal sealed record DdHeroLevels(int Easy, int Medium, int Hard)
{
    public static readonly DdHeroLevels None = new(0, 0, 0);
}

internal sealed class DdPhraseIteration(int time, int phraseId, DdHeroLevels heroLevels, XElement? source = null)
{
    public int Time { get; set; } = time;
    public int PhraseId { get; } = phraseId;
    public DdHeroLevels HeroLevels { get; } = heroLevels;
    public XElement? Source { get; } = source;
}

internal sealed record DdNewLinkedDiff(int LevelBreak, IReadOnlyList<int> PhraseIds);

/// <summary>An instrumental arrangement as the generator sees it.</summary>
internal sealed class DdArrangement
{
    /// <summary>The width of a chord template: six, or the string count past six.</summary>
    public int StringSlots { get; init; } = 6;

    public int SongLength { get; init; }

    /// <summary>The library's <c>StartBeat</c>: the first beat's time.</summary>
    public int StartBeat => Beats.Count > 0 ? Beats[0].Time : 0;

    public float AverageTempo { get; init; } = 120f;

    /// <summary>0 lead, 1 rhythm, 2 bass (<c>DataExtractor.getPath</c>).</summary>
    public int Path { get; init; } = 2;

    public List<DdEvent> Events { get; init; } = [];
    public List<DdBeat> Beats { get; init; } = [];
    public List<DdSection> Sections { get; init; } = [];
    public List<DdChordTemplate> ChordTemplates { get; init; } = [];
    public List<DdPhrase> Phrases { get; set; } = [];
    public List<DdPhraseIteration> PhraseIterations { get; set; } = [];
    public List<DdNewLinkedDiff> NewLinkedDiffs { get; set; } = [];
    public List<DdLevel> Levels { get; set; } = [];
}
