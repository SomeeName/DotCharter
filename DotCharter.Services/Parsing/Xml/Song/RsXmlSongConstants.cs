namespace DotCharter.Services.Parsing.Xml.Song;

internal static class E
{
    // Root
    public const string Song = "song";
    public const string Version = "version";
    public const string Title = "title";
    public const string Arrangement = "arrangement";
    public const string Part = "part";
    public const string Offset = "offset";
    public const string CentOffset = "centOffset";
    public const string SongLength = "songLength";
    public const string LastConversionDateTime = "lastConversionDateTime";
    public const string StartBeat = "startBeat";
    public const string AverageTempo = "averageTempo";
    public const string Capo = "capo";
    public const string ArtistName = "artistName";
    public const string ArtistNameSort = "artistNameSort";
    public const string AlbumName = "albumName";
    public const string AlbumYear = "albumYear";
    public const string CrowdSpeed = "crowdSpeed";
    public const string NewLinkedDiffs = "newLinkedDiffs";
    public const string LinkedDiffs = "linkedDiffs";
    public const string Tonebase = "tonebase";
    public const string Tonea = "tonea";
    public const string Toneb = "toneb";
    public const string Tonec = "tonec";
    public const string Toned = "toned";

    // Tuning
    public const string Tuning = "tuning";
    public const string Strings = "strings";

    // ArrangementProperties
    public const string ArrangementProperties = "arrangementProperties";
    public const string Represent = "represent";
    public const string BonusArr = "bonusArr";
    public const string StandardTuning = "standardTuning";
    public const string NonStandardChords = "nonStandardChords";
    public const string BarreChords = "barreChords";
    public const string PowerChords = "powerChords";
    public const string DropDPower = "dropDPower";
    public const string OpenChords = "openChords";
    public const string FingerPicking = "fingerPicking";
    public const string PickDirection = "pickDirection";
    public const string DoubleStops = "doubleStops";
    public const string PalmMutes = "palmMutes";
    public const string Harmonics = "harmonics";
    public const string PinchHarmonics = "pinchHarmonics";
    public const string Hopo = "hopo";
    public const string Tremolo = "tremolo";
    public const string Slides = "slides";
    public const string UnpitchedSlides = "unpitchedSlides";
    public const string Bends = "bends";
    public const string Tapping = "tapping";
    public const string Vibrato = "vibrato";
    public const string FretHandMutes = "fretHandMutes";
    public const string SlapPop = "slapPop";
    public const string TwoFingerPicking = "twoFingerPicking";
    public const string FifthsAndOctaves = "fifthsAndOctaves";
    public const string Syncopation = "syncopation";
    public const string BassPick = "bassPick";
    public const string Sustain = "sustain";
    public const string PathLead = "pathLead";
    public const string PathRhythm = "pathRhythm";
    public const string PathBass = "pathBass";

    // Lists
    public const string Tones = "tones";
    public const string Tone = "tone";
    public const string Phrases = "phrases";
    public const string Phrase = "phrase";
    public const string PhraseIterations = "phraseIterations";
    public const string PhraseIteration = "phraseIteration";
    public const string PhraseProperties = "phraseProperties";
    public const string ChordTemplates = "chordTemplates";
    public const string ChordTemplate = "chordTemplate";
    public const string FretHandMuteTemplates = "fretHandMuteTemplates";
    public const string Ebeats = "ebeats";
    public const string Ebeat = "ebeat";
    public const string Sections = "sections";
    public const string Section = "section";
    public const string Events = "events";
    public const string Event = "event";
    public const string Levels = "levels";
    public const string Level = "level";
    public const string TranscriptionTrack = "transcriptionTrack";
    public const string Count = "count";

    // Phrase / Phrase iteration attributes
    public const string Name = "name";
    public const string MaxDifficulty = "maxDifficulty";
    public const string Solo = "solo";
    public const string PhraseId = "phraseId";

    // Section attributes
    public const string Number = "number";
    public const string StartTime = "startTime";

    // Event attributes
    public const string Time = "time";
    public const string Code = "code";

    // ChordTemplate attributes
    public const string ChordName = "chordName";
    public const string DisplayName = "displayName";

    // EBeat attributes
    public const string Measure = "measure";

    // Tone change attributes
    // (reuse Time and Name)

    // Level
    public const string Difficulty = "difficulty";
    public const string Notes = "notes";
    public const string Note = "note";
    public const string Chords = "chords";
    public const string Chord = "chord";
    public const string FretHandMutesEl = "fretHandMutes";
    public const string FretHandMuteEl = "fretHandMute";
    public const string Anchors = "anchors";
    public const string Anchor = "anchor";
    public const string HandShapes = "handShapes";
    public const string HandShape = "handShape";

    // Note / ChordNote attributes
    public const string String = "string";
    public const string Fret = "fret";
    public const string Accent = "accent";
    public const string Mute = "mute";
    public const string PalmMute = "palmMute";
    public const string Pluck = "pluck";
    public const string HopoAttr = "hopo";
    public const string HammerOn = "hammerOn";
    public const string PullOff = "pullOff";
    public const string Slap = "slap";
    public const string SlideTo = "slideTo";
    public const string SlideUnpitchTo = "slideUnpitchTo";
    public const string Bend = "bend";
    public const string Tap = "tap";
    public const string Harmonic = "harmonic";
    public const string HarmonicPinch = "harmonicPinch";
    public const string LinkNext = "linkNext";
    public const string Ignore = "ignore";
    public const string BendValues = "bendValues";
    public const string BendValue = "bendValue";
    public const string Step = "step";
    public const string TremoloAttr = "tremolo";
    public const string VibratoAttr = "vibrato";
    public const string LeftHand = "leftHand";
    public const string Strum = "strum";

    // Chord element attributes
    public const string ChordId = "chordId";
    public const string FretHandMuteAttr = "fretHandMute";
    public const string HighDensity = "highDensity";

    // Anchor attributes
    public const string Width = "width";

    // HandShape attributes
    public const string EndTime = "endTime";

    // TranscriptionTrack
    // (reuses Notes, Chords, Anchors, HandShapes, Difficulty, Count)
}
