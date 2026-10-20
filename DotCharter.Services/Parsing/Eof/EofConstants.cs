namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Format constants for Editor on Fire <c>.eof</c> project files.
///     Mirrored from <c>editor-on-fire/src/song.h</c>.
/// </summary>
internal static class EofConstants
{
    public const string FileHeader = "EOFSONH";

    // Allegro PACKFILE magic ('s','l','h','!') stored as big-endian 0x736C6821.
    public static readonly byte[] AllegroPackMagic = [0x73, 0x6C, 0x68, 0x21];
    public static readonly byte[] AllegroNopackMagic = [0x73, 0x6C, 0x68, 0x2E];

    // Track formats
    public const int TrackFormatLegacy = 1;
    public const int TrackFormatVocal = 2;
    public const int TrackFormatProKeys = 3;
    public const int TrackFormatProGuitar = 4;
    public const int TrackFormatProVariableLegacy = 5;

    // Track flags
    public const uint TrackFlagAltName = 2;
    public const uint TrackFlagUnlimitedDiffs = 4; // EOF sets it on all pro guitar tracks when RS export is on

    // Note flags (extended marker)
    public const uint NoteFlagExtended = 0x80000000u;

    // Common note flags (shared between all track formats — see EOF_NOTE_FLAG_* in song.h)
    public const uint NoteFlagAutoHopo = 1;       // Determined by phrase status (also set when manually toggled)
    public const uint NoteFlagCrazy = 4;          // Crazy: sustain overlaps other notes; on a pro guitar chord it also forces the full chord box
    public const uint NoteFlagForceHopo = 8;      // Manually defined as HOPO
    public const uint NoteFlagForceStrum = 16;    // Manually defined as forced strum (no HOPO)
    public const uint NoteFlagIsTrill = 65536;
    public const uint NoteFlagIsTremolo = 131072;

    // Per-string fret byte semantics for pro guitar notes
    public const byte FretMutedBit = 0x80;        // MSB set = string is muted
    public const byte FretValueMask = 0x7F;       // Lower 7 bits hold the actual fret number
    public const byte FretMutedNoFret = 0xFF;     // Stored value when string is muted with no fret specified

    // Pro guitar note flags
    public const uint PgFlagAccent = 32;
    public const uint PgFlagPHarmonic = 64;
    public const uint PgFlagLinkNext = 128;
    public const uint PgFlagUnpitchedSlide = 256;
    public const uint PgFlagHo = 512;
    public const uint PgFlagPo = 1024;
    public const uint PgFlagTap = 2048;
    public const uint PgFlagSlideUp = 4096;
    public const uint PgFlagSlideDown = 8192;
    public const uint PgFlagStringMute = 16384;
    public const uint PgFlagPalmMute = 32768;
    public const uint PgFlagBend = 2097152;
    public const uint PgFlagHarmonic = 4194304;
    public const uint PgFlagVibrato = 16777216;
    public const uint PgFlagRsNotation = 33554432;
    public const uint PgFlagPop = 67108864;
    public const uint PgFlagSlap = 134217728;

    /// <summary>EOF_PRO_GUITAR_NOTE_FLAG_SPLIT (song.h:64): "a chord that exports
    /// to Rocksmith as single notes instead of a chord" — EOF breaks it up at RS
    /// export (rs.c:2082-2124). <see cref="Chord.SplitIntoNotes" /> both ways.</summary>
    public const uint PgFlagSplit = 0x20000000;

    // Pro guitar EXTENDED note flags — the u32 that follows the note record when
    // NoteFlagExtended is set (EOF_PRO_GUITAR_NOTE_EFLAG_* in song.h:130+).
    /// <summary>EOF_PRO_GUITAR_NOTE_EFLAG_IGNORE (song.h:130) — "a note that will
    /// export to RS2 format with the 'ignore' status set". EOF's own RS-XML reader
    /// sets it from <c>ignore="1"</c> (rs_import.c:257 for notes, :1788 for chords)
    /// and its writer turns it back into <c>ignore</c> (rs.c:5760).</summary>
    public const uint PgEFlagIgnore = 1;

    /// <summary>EOF_PRO_GUITAR_NOTE_EFLAG_FINGERLESS (song.h:137) — "a chord that
    /// intentionally has no defined fingering". Not finger 0: EOF calls its
    /// fingering VALID (rs.c:3593-3596), exports its template with every finger
    /// <c>-1</c> (rs.c:6407-6408, used at rs.c:910-914) and its chord notes with
    /// <c>leftHand="-1"</c> (rs.c:5958). <see cref="ChordTemplate.Fingerless" />
    /// both ways.</summary>
    public const uint PgEFlagFingerless = 32;

    // Section types (per track, post-notes)
    public const int SectionSolo = 1;
    public const int SectionStarPower = 2;
    public const int SectionBookmark = 3;
    public const int SectionFretCatalog = 4;
    public const int SectionLyricPhrase = 5;
    public const int SectionTrill = 9;
    public const int SectionArpeggio = 10;
    public const int SectionTrainer = 11;
    public const int SectionTremolo = 14;
    public const int SectionSlider = 15;
    public const int SectionFretHandPos = 16;
    public const int SectionRsPopupMessage = 17;
    public const int SectionRsToneChange = 18;
    public const int SectionHandshape = 19;        // Like arpeggio, but with a status flag

    // Section-instance flags (arpeggio sections). EOF_RS_ARP_HANDSHAPE
    // (song.h:165): set = the phrase exports as a plain handshape; clear = a
    // true arpeggio ("-arp" template treatment). The ONLY discriminator.
    public const uint SectionFlagRsArpHandshape = 2;

    // Track-level custom data block IDs (pro guitar)
    public const uint PgCustomFingerArray = 2;
    public const uint PgCustomArrangementType = 3;
    public const uint PgCustomIgnoreTuning = 4;
    public const uint PgCustomCapoPosition = 6;
    public const uint PgCustomTechNotes = 7;
    public const uint PgCustomNumDiffs = 9;

    // Arrangement type values (custom block 3 payload byte)
    public const byte ArrTypeCombo = 1;
    public const byte ArrTypeRhythm = 2;
    public const byte ArrTypeLead = 3;
    public const byte ArrTypeBass = 4;

    // Beat flags
    public const uint BeatFlagAnchor = 1;
    public const uint BeatFlagStart44 = 4;
    public const uint BeatFlagStart34 = 8;
    public const uint BeatFlagStart54 = 16;
    public const uint BeatFlagStart64 = 32;
    public const uint BeatFlagCustomTs = 64;
    public const uint BeatFlagKeySig = 128;
    public const uint BeatFlagStart24 = 512;
}
