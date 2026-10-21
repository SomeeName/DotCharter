using DotCharter.Services.Parsing.MoverPhrases;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Imports Editor on Fire (<c>.eof</c>) project files. Mirrors the layout in
///     <c>editor-on-fire/src/song.c</c> (<c>eof_load_song_pf</c>). Files are
///     wrapped in an Allegro PACKFILE (LZSS) compressed container; the importer
///     transparently decompresses before parsing.
///     Only Pro Guitar tracks (format 4) become DotCharter <see cref="Arrangement"/>s;
///     other track formats (legacy, vocal) are walked over for stream alignment but
///     not surfaced.
/// </summary>
public sealed class EofImporter
{
    private readonly EofDecodedFileCache _decodedFiles;

    public EofImporter() : this(EofDecodedFileCache.Shared)
    {
    }

    internal EofImporter(EofDecodedFileCache decodedFiles) => _decodedFiles = decodedFiles;

    public List<EofTrackInfo> GetAvailableTracks(string filePath)
    {
        // One gesture, one decode: the import dialog previews the tracks and the
        // import then reads the same file (EofDecodedFileCache). The payload is
        // SHARED — EofPackReader only reads it.
        var reader = new EofPackReader(_decodedFiles.Read(filePath));

        var session = new EofParseSession(reader);
        session.ReadHeaderAndProperties();
        session.ReadIniBlocks();
        session.ReadOggProfiles();
        session.ReadBeats();
        session.ReadTextEvents();
        session.ReadGlobalCustomDataBlocks();

        var trackCount = reader.ReadU32();
        var tracks = new List<EofTrackInfo>();
        for (var i = 0; i < trackCount; i++)
        {
            var info = session.ReadTrackHeader(i);
            // Skip the body — track-info preview only collects identity.
            var noteCount = session.SkipTrackBody(info.Format);
            info.NoteCount = noteCount;

            // Surface only Pro Guitar tracks that actually have notes — EOF
            // routinely saves stub Pro Guitar tracks (alternate tunings, 22-fret
            // slots) with zero notes that would otherwise clutter the dialog
            // and produce empty arrangements after import.
            if (info.Format == EofConstants.TrackFormatProGuitar && noteCount > 0)
                tracks.Add(info);
        }

        return tracks;
    }

    public List<Arrangement> ImportTracks(string filePath, List<int> trackIdsToImport) => ImportTracksWithMetadata(filePath, trackIdsToImport).Arrangements;

    public EofImportResult ImportTracksWithMetadata(string filePath, List<int> trackIdsToImport)
    {
        var reader = new EofPackReader(_decodedFiles.Read(filePath));

        var result = new EofImportResult();
        var session = new EofParseSession(reader, result.Metadata);

        session.ReadHeaderAndProperties();
        session.ReadIniBlocks();
        session.ReadOggProfiles();
        session.ReadBeats();
        session.ReadTextEvents();
        session.ReadGlobalCustomDataBlocks();

        var trackCount = reader.ReadU32();
        var beatMarkers = BuildBeatMarkers(result.Metadata);

        for (var i = 0; i < trackCount; i++)
        {
            var info = session.ReadTrackHeader(i);

            if (info.Format == EofConstants.TrackFormatProGuitar && trackIdsToImport.Contains(info.Id))
            {
                var arr = EofProGuitarTrackParser.Parse(reader, info, beatMarkers);
                ApplyTextEventsToArrangement(arr, info.Id, result.Metadata, beatMarkers);
                result.Arrangements.Add(arr);
            }
            else
            {
                session.SkipTrackBody(info.Format);
            }
        }

        return result;
    }

    /// <summary>
    ///     Promotes EOF text events to per-arrangement <see cref="EventPoint"/> entries.
    ///     Track 0 events are global (apply to all arrangements); track-specific events
    ///     only apply to their owning arrangement. Phrase/section flags decide which slot
    ///     receives the text. Beat positions are converted to <see cref="FractionalPosition"/>
    ///     via the supplied beat markers.
    /// </summary>
    internal static void ApplyTextEventsToArrangement(Arrangement arrangement, int trackId,
        EofImportMetadata meta, List<BeatMarker> beats)
    {
        const ushort flagPhrase = 1;
        const ushort flagSection = 2;
        const ushort flagEvent = 4;
        const ushort flagSoloPhrase = 8;
        var movers = new List<EofMoverPoint>();

        foreach (var ev in meta.TextEvents)
        {
            if (ev.Track != 0 && ev.Track != trackId) continue;

            double timeMs;
            if (ev.IsMs)
                timeMs = ev.BeatPos;
            else if (ev.BeatPos < (uint)beats.Count)
                timeMs = beats[(int)ev.BeatPos].Time;
            else if (beats.Count > 0)
                timeMs = beats[^1].Time;
            else
                continue;

            var point = new EventPoint
            {
                Position = TimelineMath.PositionFromImportedMs(timeMs, beats)
            };

            var isPhrase = (ev.Flags & (flagPhrase | flagSoloPhrase)) != 0;
            var isSection = (ev.Flags & flagSection) != 0;
            var isEvent = (ev.Flags & flagEvent) != 0;
            var isSoloPhrase = (ev.Flags & flagSoloPhrase) != 0;

            // EOF stores RS phrases and RS sections as the same text-event
            // record with independent flag bits (often both set — see
            // gp_import.c which writes flags = RS_SECTION | RS_PHRASE).
            // The two correspond to different concepts in the runtime
            // (sections = riff names, phrases = phrase iterations) so when
            // both bits are present we surface the text in both buckets.
            if (isSection) point.Section = ev.Text;
            if (isPhrase) point.Phrase = ev.Text;
            if (isEvent) point.Events = ev.Text;
            if (!isSection && !isPhrase && !isEvent)
                point.Phrase = ev.Text; // untyped events surface as phrase markers (RB-style sections)

            arrangement.EventPoints.Add(point);
            // EOF reads moveR only off an RS phrase that is attached to a beat.
            var beatAttachedPhrase = (ev.Flags & flagPhrase) != 0 && !ev.IsMs;
            if (beatAttachedPhrase && MoverPhraseName.TryParseEof(ev.Text, out var moveBy, out var ownName))
                movers.Add(new EofMoverPoint(point, moveBy, ownName));
            if (isPhrase && !arrangement.Phrases.Contains(ev.Text))
                arrangement.Phrases.Add(ev.Text);
            if (isSoloPhrase)
            {
                if (!arrangement.PhraseInfos.TryGetValue(ev.Text, out var info))
                    arrangement.PhraseInfos[ev.Text] = new PhraseInfo(0, true);
                else
                    info.Solo = true;
            }
        }

        EofMoverPhrases.Resolve(arrangement, movers);
    }

    /// <summary>
    ///     The one mapping from an <c>.eof</c> beat to a chart
    ///     <see cref="BeatMarker"/>. PUBLIC because the import DIALOG builds the
    ///     project's beat map too, and a hand-rolled copy of the same projection
    ///     would leave <see cref="BeatMarker.IsAnchor"/> off.
    ///     <para>
    ///     Dropping it is real data loss, not a display detail: an anchored beat
    ///     is one whose timing the CHARTER pinned by hand, and the app treats it
    ///     as immovable — <c>TempoService</c> skips an anchored beat when it
    ///     re-times a run, <c>RscpWriter</c> persists the flag and
    ///     <c>EofExporter</c> re-emits it. So an imported EOF chart kept every
    ///     anchor while its NOTES were resolved (this method's own output is what
    ///     the importer positions against) and then saved a project that had
    ///     forgotten them: the first tempo edit dragged beats the charter had
    ///     deliberately fixed, and a round trip back to <c>.eof</c> lost every
    ///     anchor flag.
    ///     </para>
    /// </summary>
    public static List<BeatMarker> BuildBeatMarkers(EofImportMetadata meta)
    {
        var markers = new List<BeatMarker>(meta.Beats.Count);
        foreach (var b in meta.Beats)
        {
            markers.Add(new BeatMarker
            {
                Time = b.TimeMs,
                Bpm = b.Bpm,
                BeatsInMeasure = b.BeatsInMeasure,
                NoteDenominator = b.NoteDenominator,
                IsMeasure = b.IsMeasureStart,
                IsAnchor = b.IsAnchor
            });
        }

        return markers;
    }
}
