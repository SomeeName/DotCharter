namespace DotCharter.Services.Export.Sng.Model;

/// <summary>
/// In-memory model of a Rocksmith 2014 SNG file. Field set mirrors
/// Rocksmith2014.NET's Rocksmith2014.SNG types so that
/// <see cref="RocksmithSngWriter"/> (and the package reader) round-trip a game
/// file without losing data. Data only: no format code lives in this folder.
/// </summary>
internal sealed class RocksmithSng
{
    public List<RsBeat> Beats { get; } = [];
    public List<RsChordTemplate> Chords { get; } = [];
    public List<RsChordNotes> ChordNotes { get; } = [];
    public List<RsEvent> Events { get; } = [];
    public List<RsPhrase> Phrases { get; } = [];
    public List<RsPhraseIteration> PhraseIterations { get; } = [];
    public List<RsSection> Sections { get; } = [];
    public List<RsLevel> Levels { get; } = [];
    public List<RsTone> Tones { get; } = [];
    public List<RsVocal> Vocals { get; } = [];
    public List<RsSymbolsHeader> SymbolsHeaders { get; } = [];
    public List<RsSymbolsTexture> SymbolsTextures { get; } = [];
    public List<RsSymbolDefinition> SymbolDefinitions { get; } = [];
    public List<RsPhraseExtraInfo> PhraseExtraInfos { get; } = [];
    public List<RsNewLinkedDifficulty> NewLinkedDifficulties { get; } = [];
    public List<RsAction> Actions { get; } = [];
    public List<RsDna> Dnas { get; } = [];
    public RsMetaData MetaData { get; set; } = new();

    /// <summary>The RS7X extension block after MetaData (7+ strings, <see cref="RsExtensionBlock" />);
    /// null when the body ends at MetaData or what follows breaks a validation rule.</summary>
    public RsExtensionBlock? Extension { get; set; }

    /// <summary>Why the bytes after MetaData were not taken as a block; null when there were none.</summary>
    public string? ExtensionProblem { get; set; }
}

internal sealed record RsBeat(float Time, short Measure, short Beat, uint Mask, int PhraseIteration = 0);

internal sealed record RsEvent(float Time, string Name);

internal sealed record RsPhrase(
    int MaxDifficulty,
    int IterationCount,
    string Name,
    sbyte Solo = 0,
    sbyte Disparity = 0,
    sbyte Ignore = 0);

internal sealed record RsPhraseIteration(int PhraseId, float StartTime, float EndTime, int[] Difficulty);

internal sealed record RsSection(
    string Name,
    int Number,
    float StartTime,
    float EndTime,
    int StartPhraseIterationId = 0,
    int EndPhraseIterationId = 0)
{
    /// <summary>Per-difficulty string usage bitmasks (36 bytes).</summary>
    public sbyte[] StringMask { get; init; } = new sbyte[36];
}

internal sealed record RsAnchor(
    float StartTime,
    sbyte Fret,
    int Width,
    float EndTime = 0f,
    float FirstNoteTime = 0f,
    float LastNoteTime = 0f,
    int PhraseIterationId = 0);

internal sealed record RsAnchorExtension(float BeatTime, sbyte Fret);

internal sealed record RsFingerPrint(
    int ChordId,
    float StartTime,
    float EndTime,
    float FirstNoteTime = 0f,
    float LastNoteTime = 0f);

internal sealed record RsBendValue(float Time, float Step);

internal sealed record RsTone(float Time, int ToneId);

internal sealed record RsDna(float Time, int DnaId);

internal sealed record RsVocal(float Time, int Note, float Length, string Lyric);

internal sealed record RsSymbolsHeader(
    int Id,
    int Unk2 = 0,
    int Unk3 = 0,
    int Unk4 = 0,
    int Unk5 = 0,
    int Unk6 = 0,
    int Unk7 = 0,
    int Unk8 = 2);

internal sealed record RsSymbolsTexture(string Font, int FontPathLength, int Width, int Height);

/// <summary>Outer/Inner are [YMin, XMin, YMax, XMax] rectangles.</summary>
internal sealed record RsSymbolDefinition(string Symbol, float[] Outer, float[] Inner);

internal sealed record RsPhraseExtraInfo(int PhraseId, int Difficulty, int Empty, sbyte LevelJump, short Redundant);

internal sealed record RsNewLinkedDifficulty(int LevelBreak, int[] Phrases);

internal sealed record RsAction(float Time, string Name);

internal sealed record RsChordTemplate(uint Mask, sbyte[] Frets, sbyte[] Fingers, string Name)
{
    /// <summary>Per-string MIDI notes (6 ints).</summary>
    public int[] Notes { get; init; } = new int[6];

    /// <summary>The last byte of the 32-byte name when the name ends before it: the
    /// pre-RS7X rs7string format kept the string-6 column there
    /// (<c>((finger + 1) &lt;&lt; 5) | (fret + 1)</c>, 0 = none). Read for old packages only.</summary>
    public byte NameByte31 { get; init; }
}

internal sealed class RsChordNotes
{
    public uint[] Mask { get; init; } = new uint[6];
    public List<RsBendValue>[] BendData { get; init; } = CreateBendArrays();
    public sbyte[] SlideTo { get; init; } = new sbyte[6];
    public sbyte[] SlideUnpitchTo { get; init; } = new sbyte[6];
    public short[] Vibrato { get; init; } = new short[6];

    private static List<RsBendValue>[] CreateBendArrays()
    {
        var result = new List<RsBendValue>[6];
        for (var i = 0; i < result.Length; i++) result[i] = [];
        return result;
    }
}

internal sealed class RsNote
{
    public uint Mask { get; init; }
    public uint Flags { get; init; }
    public uint Hash { get; init; }
    public float Time { get; init; }
    public sbyte StringIndex { get; init; }
    public sbyte Fret { get; init; }
    public sbyte AnchorFret { get; init; }
    public sbyte AnchorWidth { get; init; }
    public int ChordId { get; init; } = -1;
    public int ChordNotesId { get; init; } = -1;
    public int PhraseId { get; init; }
    public int PhraseIterationId { get; init; }
    public short FingerPrintId0 { get; init; } = -1;
    public short FingerPrintId1 { get; init; } = -1;
    public short NextIterNote { get; init; }
    public short PrevIterNote { get; init; }
    public short ParentPrevNote { get; init; }
    public sbyte SlideTo { get; init; } = -1;
    public sbyte SlideUnpitchTo { get; init; } = -1;
    public sbyte LeftHand { get; init; } = -1;
    public sbyte Tap { get; init; }
    public sbyte PickDirection { get; init; }
    public sbyte Slap { get; init; } = -1;
    public sbyte Pluck { get; init; } = -1;
    public short Vibrato { get; init; }
    public float Sustain { get; init; }
    public float MaxBend { get; init; }
    public List<RsBendValue> BendData { get; init; } = [];
}

internal sealed class RsLevel
{
    public int Difficulty { get; init; }
    public List<RsAnchor> Anchors { get; } = [];
    public List<RsAnchorExtension> AnchorExtensions { get; } = [];
    public List<RsFingerPrint> HandShapes { get; } = [];
    public List<RsFingerPrint> Arpeggios { get; } = [];
    public List<RsNote> Notes { get; } = [];
    public float[] AverageNotesPerIteration { get; set; } = [];
    public int[] NotesInPhraseIterationsExclIgnored { get; set; } = [];
    public int[] NotesInPhraseIterationsAll { get; set; } = [];
}

internal sealed class RsMetaData
{
    public double MaxScore { get; init; }
    public double MaxNotesAndChords { get; init; }
    public double MaxNotesAndChordsReal { get; init; }
    public double PointsPerNote { get; init; }
    public float FirstBeatLength { get; init; }
    public float StartTime { get; init; }
    public sbyte CapoFretId { get; init; } = -1;
    public string LastConversionDateTime { get; init; } = "";
    public short Part { get; init; } = 1;
    public float SongLength { get; init; }
    public short[] Tuning { get; init; } = [];
    public float FirstNoteTime { get; init; }
    public int MaxDifficulty { get; init; }
}
