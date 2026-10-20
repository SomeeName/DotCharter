namespace DotCharter.Services.Parsing.Eof;

/// <summary>One entry of the canonical EOF track scaffold (eof_default_tracks in song.c).</summary>
internal sealed record EofScaffoldTrack(
    int Index,
    string Name,
    byte Format,
    byte Behavior,
    byte Type,
    uint Flags,
    byte NumLanes,
    byte NumStrings);

/// <summary>
///     The canonical 15-slot track scaffold every EOF project carries
///     (track 0 = the unnamed global track, then <c>eof_default_tracks[1..14]</c>).
///     EOF writes all of these on every save — importing tools expect the fixed
///     order — with one exception: the bonus pro guitar track (index 14) is
///     omitted when empty, for compatibility with older EOF 1.8 builds.
/// </summary>
internal static class EofTrackScaffold
{
    // Track behaviors (song.h EOF_*_TRACK_BEHAVIOR).
    private const byte BehaviorGuitar = 1;
    private const byte BehaviorDrum = 2;
    private const byte BehaviorVocal = 3;
    private const byte BehaviorKeys = 4;
    private const byte BehaviorProGuitar = 5;
    private const byte BehaviorDance = 7;

    /// <summary>PART REAL_GUITAR_BONUS — omitted from the file when unused.</summary>
    public const int ProGuitarBonusIndex = 14;

    /// <summary>Pro guitar slots for guitar-family arrangements, in fill order.</summary>
    public static readonly int[] GuitarSlotIndices = [9, 12, ProGuitarBonusIndex];

    /// <summary>Pro guitar slots for bass arrangements, in fill order.</summary>
    public static readonly int[] BassSlotIndices = [8, 11];

    /// <summary>Tracks 1..14 (track 0, the global track, is written separately).</summary>
    public static readonly EofScaffoldTrack[] Tracks =
    [
        new(1, "PART GUITAR", EofConstants.TrackFormatLegacy, BehaviorGuitar, 1, 0, 5, 0),
        new(2, "PART BASS", EofConstants.TrackFormatLegacy, BehaviorGuitar, 2, 0, 5, 0),
        new(3, "PART GUITAR COOP", EofConstants.TrackFormatLegacy, BehaviorGuitar, 3, 0, 5, 0),
        new(4, "PART RHYTHM", EofConstants.TrackFormatLegacy, BehaviorGuitar, 4, 0, 5, 0),
        new(5, "PART DRUMS", EofConstants.TrackFormatLegacy, BehaviorDrum, 5, 0xFF000000, 5, 0),
        new(6, "PART VOCALS", EofConstants.TrackFormatVocal, BehaviorVocal, 6, 0xFF000000, 5, 0),
        new(7, "PART KEYS", EofConstants.TrackFormatLegacy, BehaviorKeys, 7, 0, 5, 0),
        // Pro guitar tracks carry UNLIMITED_DIFFS like a real EOF project with
        // RS export enabled (song.c:4567-4571) — without it EOF treats the
        // track as 5 static difficulties.
        new(8, "PART REAL_BASS", EofConstants.TrackFormatProGuitar, BehaviorProGuitar, 8,
            EofConstants.TrackFlagUnlimitedDiffs, 5, 4),
        new(9, "PART REAL_GUITAR", EofConstants.TrackFormatProGuitar, BehaviorProGuitar, 9,
            EofConstants.TrackFlagUnlimitedDiffs, 5, 6),
        new(10, "PART DANCE", EofConstants.TrackFormatLegacy, BehaviorDance, 10, 0, 4, 0),
        new(11, "PART REAL_BASS_22", EofConstants.TrackFormatProGuitar, BehaviorProGuitar, 11,
            EofConstants.TrackFlagUnlimitedDiffs, 5, 4),
        new(12, "PART REAL_GUITAR_22", EofConstants.TrackFormatProGuitar, BehaviorProGuitar, 12,
            EofConstants.TrackFlagUnlimitedDiffs, 5, 6),
        new(13, "PART REAL_DRUMS_PS", EofConstants.TrackFormatLegacy, BehaviorDrum, 13, 0, 5, 0),
        new(ProGuitarBonusIndex, "PART REAL_GUITAR_BONUS", EofConstants.TrackFormatProGuitar, BehaviorProGuitar, 14,
            EofConstants.TrackFlagUnlimitedDiffs, 5, 6)
    ];
}
