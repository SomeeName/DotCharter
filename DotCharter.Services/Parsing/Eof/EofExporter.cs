using System.Globalization;
using DotCharter.Services.Export;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Exports a DotCharter <see cref="ChartProject"/> as an Editor on Fire
///     project file (<c>.eof</c>). Mirrors <c>eof_save_song</c> in
///     <c>editor-on-fire/src/song.c</c> byte-for-byte: the canonical 15-slot
///     track scaffold (global track + PART GUITAR … PART REAL_GUITAR_BONUS,
///     the empty bonus track omitted for backwards compatibility exactly like
///     EOF does), ppqn/flags beat records plus the floating-point beat-timing
///     custom block, beat-index-anchored text events, and Pro Guitar tracks
///     with sections (FHP / arpeggio / tremolo / tone change) and the
///     finger/arrangement-type/tuning/capo custom data blocks.
///     <para>
///     DotCharter arrangements map onto EOF's pro guitar slots: bass
///     arrangements fill PART REAL_BASS then PART REAL_BASS_22; guitar
///     arrangements fill PART REAL_GUITAR, PART REAL_GUITAR_22 then
///     PART REAL_GUITAR_BONUS. The first vocal track becomes PART VOCALS
///     lyrics. The output opens in EOF and round-trips through
///     <see cref="EofImporter"/> (see the round-trip parity tests).
///     </para>
/// </summary>
public static class EofExporter
{
    /// <summary>Exports the project; lossy decisions never throw — they are
    /// collected on the result's <see cref="EofExportResult.Report"/>.</summary>
    /// <param name="project">The saved snapshot.</param>
    /// <param name="includeVocals">Whether PART VOCALS carries the project's
    /// lyrics — see <see cref="VocalsExportPolicy" />.</param>
    public static EofExportResult Export(ChartProject project, bool includeVocals = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        PlayOnlyExportGuard.ThrowIfPlayOnly(project, "Editor on Fire export");
        var session = new EofExportSession(project, includeVocals);
        return new EofExportResult { Bytes = session.Write(), Report = session.Report };
    }

    /// <summary>
    ///     Writes through <see cref="AtomicFile" />, and that is not incidental:
    ///     the <c>.eof</c> is <b>somebody else's project file</b> (the rule
    ///     <c>Export/CompanionOwnership</c> exists for), and
    ///     <c>SaveCompanionService</c> rewrites it inside the worker of every
    ///     single project save. A bare <c>File.WriteAllBytes</c> TRUNCATES first,
    ///     so a crash, a kill or a power cut in that window left the charter's
    ///     EOF project empty — and unlike the derived bundle beside it, that one
    ///     is not regenerable from anything we hold.
    /// </summary>
    public static EofExportResult ExportToFile(ChartProject project, string filePath,
        bool includeVocals = true)
    {
        var result = Export(project, includeVocals);
        AtomicFile.WriteAllBytes(filePath, result.Bytes);
        return result;
    }
}

/// <summary>
///     Encapsulates the per-export mutable state (writer, project, track
///     assignments) and walks the EOF format sections in the canonical order
///     of <c>eof_save_song</c> — the write-side mirror of <see cref="EofParseSession"/>.
/// </summary>
internal sealed class EofExportSession
{
    private const uint DefaultTimeDivision = 480; // EOF_DEFAULT_TIME_DIVISION (midi.h)
    private const byte DefaultNumFrets = 24;      // RS2 default (eof_song_add_track)

    // Text event flags (song.h EOF_EVENT_FLAG_*).
    private const ushort EventFlagRsPhrase = 1;
    private const ushort EventFlagRsSection = 2;
    private const ushort EventFlagRsEvent = 4;
    private const ushort EventFlagRsSoloPhrase = 8;
    private const ushort EventFlagFloatingPos = 16;

    private readonly ChartProject _project;
    private readonly IList<BeatMarker> _beats;
    private readonly EofPackWriter _w = new();

    /// <summary>File track index → the arrangement exported into that slot.</summary>
    private readonly Dictionary<int, Arrangement> _assignments;

    /// <summary>Whether the bonus pro guitar slot (index 14) is used — EOF omits it when empty.</summary>
    private readonly bool _writeBonusTrack;

    /// <summary>Whether PART VOCALS carries the project's lyrics. False writes the
    /// track scaffold EMPTY — EOF's own no-lyrics shape — so the file stays valid
    /// and the export simply has no vocals. Sourced from
    /// <see cref="VocalsExportPolicy" />, the one reader of
    /// <c>AppConfig.ExportIncludeVocals</c>.</summary>
    private readonly bool _includeVocals;

    public EofConversionReport Report { get; } = new();

    public EofExportSession(ChartProject project, bool includeVocals = true)
    {
        _project = project;
        _includeVocals = includeVocals;
        _beats = project.Beats;
        _assignments = AssignTracks(project, Report);
        _writeBonusTrack = _assignments.ContainsKey(EofTrackScaffold.ProGuitarBonusIndex);
    }

    public byte[] Write()
    {
        WriteHeaderAndProperties();
        WriteIniBlocks();
        WriteOggProfiles();
        WriteBeats();
        WriteTextEvents();
        WriteGlobalCustomDataBlocks();
        WriteTracks();
        return _w.ToArray();
    }

    /// <summary>
    ///     Maps arrangements to EOF pro guitar slots: bass to PART REAL_BASS /
    ///     PART REAL_BASS_22 (overflowing into guitar slots), everything else to
    ///     PART REAL_GUITAR / PART REAL_GUITAR_22 / PART REAL_GUITAR_BONUS.
    ///     Vocal-type arrangements and overflow are dropped (vocals ride the
    ///     project's <see cref="ChartProject.VocalTracks"/> instead).
    /// </summary>
    internal static Dictionary<int, Arrangement> AssignTracks(ChartProject project,
        EofConversionReport? report = null)
    {
        var guitarSlots = new Queue<int>(EofTrackScaffold.GuitarSlotIndices);
        var bassSlots = new Queue<int>(EofTrackScaffold.BassSlotIndices);
        var result = new Dictionary<int, Arrangement>();

        foreach (var arrangement in project.Arrangements)
        {
            if (arrangement.ArrangementType == ArrangementType.Vocal)
            {
                report?.Add(EofConversionKind.Dropped, EofLoss.VocalArrangement, arrangement.Name,
                    "Vocal-type arrangements have no pro guitar slot — lyrics export from the project's vocal tracks instead.");
                continue;
            }

            var isBass = arrangement.ArrangementType == ArrangementType.Bass;
            var slots = isBass && bassSlots.Count > 0 ? bassSlots : guitarSlots;
            if (isBass && bassSlots.Count == 0 && guitarSlots.Count > 0)
            {
                report?.Add(EofConversionKind.Approximated, EofLoss.BassInGuitarSlot, arrangement.Name,
                    "Both PART REAL_BASS slots are taken — exported into a guitar slot with the bass arrangement type.");
            }

            if (slots.Count == 0)
            {
                report?.Add(EofConversionKind.Dropped, EofLoss.NoFreeSlot, arrangement.Name,
                    "All EOF pro guitar slots are taken — the arrangement was not exported.");
                continue;
            }

            result[slots.Dequeue()] = arrangement;
        }

        return result;
    }

    private void WriteHeaderAndProperties()
    {
        _w.WriteFixedString(EofConstants.FileHeader, 16);
        _w.WriteU32(1); // Project revision.
        _w.WriteU8(0);  // Timing format (0 = millisecond based).
        _w.WriteU32(DefaultTimeDivision);
    }

    private void WriteIniBlocks()
    {
        var chart = _project.Chart;
        // INI string types per eof_save_song: 2=artist, 3=title, 4=frettist,
        // 6=year, 8=album, 9=genre. Only populated strings are written.
        var strings = new List<(byte Type, string Value)>();
        AddIniString(strings, 2, chart.Artist);
        AddIniString(strings, 3, chart.SongName);
        AddIniString(strings, 4, chart.Charter);
        AddIniString(strings, 6, chart.AlbumYear);
        AddIniString(strings, 8, chart.AlbumName);
        AddIniString(strings, 9, chart.Genre);

        _w.WriteU16((ushort)strings.Count);
        foreach (var (type, value) in strings)
        {
            _w.WriteU8(type);
            _w.WriteString(value);
        }

        // INI booleans: single byte each, low 7 bits = type, MSB = TRUE.
        // Type 1 = "lyrics" — set when the project carries any lyrics.
        // Type 11 = accurate_ts — EOF defaults it ON and always saves it
        // (song.c:246); loaders zero it for files that lack the byte.
        var hasLyrics = FindVocalTrack() != null;
        _w.WriteU16((ushort)(hasLyrics ? 2 : 1));
        if (hasLyrics) _w.WriteU8(0x80 | 1);
        _w.WriteU8(0x80 | 11);

        // INI numbers: EOF always writes the song difficulty (type 2);
        // 0xFF is its "undefined" default (song.c:262), 0 would read as a
        // deliberate zero-difficulty rating.
        _w.WriteU16(1);
        _w.WriteU8(2);
        _w.WriteU32(0xFF);
    }

    private static void AddIniString(List<(byte, string)> list, byte type, string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed)) list.Add((type, trimmed));
    }

    private void WriteOggProfiles()
    {
        // EOF projects reference their audio as an OGG profile; synthesize one
        // from the project's audio file (EOF's canonical default is guitar.ogg).
        var name = "guitar.ogg";
        if (!string.IsNullOrWhiteSpace(_project.AudioFileName))
        {
            var stem = Path.GetFileNameWithoutExtension(_project.AudioFileName);
            if (!string.IsNullOrWhiteSpace(stem)) name = stem + ".ogg";
        }

        _w.WriteU16(1);      // One OGG profile.
        _w.WriteString(name);
        _w.WriteString(null); // Original audio file name (EOF writes an empty string).
        _w.WriteString(null); // Profile description.
        // EOF invariant: midi_offset IS the first beat's position (its GP
        // import ends with midi_offset = beat[0].pos, gp_import.c:5649, and
        // every beat recalculation re-bases the map on it, beat.c:132).
        // Writing 0 while beat 0 sits later trips eof_detect_tempo_map_
        // corruption on EVERY open, and accepting the offered repair snaps
        // the chart to 0 ms — a 10 s desync for projects with baked leading
        // silence.
        _w.WriteU32(_beats.Count > 0 ? ToMsU32(_beats[0].Time) : 0);
        _w.WriteU32(0);       // Profile flags.
    }

    private void WriteBeats()
    {
        _w.WriteU32((uint)_beats.Count);
        for (var i = 0; i < _beats.Count; i++)
        {
            var beat = _beats[i];
            var ppqn = beat.Bpm > 0 ? (uint)Math.Clamp(Math.Round(60_000_000.0 / beat.Bpm), 1, uint.MaxValue) : 500_000u;
            _w.WriteU32(ppqn);
            _w.WriteU32(ToMsU32(beat.Time));
            var flags = BuildBeatFlags(i);
            byte key = 0;
            if (i == 0 && _project.Chart.KeyAccidentals != 0)
            {
                // Carry the project key signature on the first beat.
                flags |= EofConstants.BeatFlagKeySig;
                key = (byte)(sbyte)Math.Clamp(_project.Chart.KeyAccidentals, -7, 7);
            }

            _w.WriteU32(flags);
            _w.WriteU8(key);
        }

        ReportMeasureDerivationMismatches();
    }

    /// <summary>
    ///     EOF does not store per-beat measure flags — readers re-derive
    ///     measure starts by counting beats from the last time signature.
    ///     Simulate that derivation over the flags we just wrote and report
    ///     any beat whose measure-start state won't survive.
    /// </summary>
    private void ReportMeasureDerivationMismatches()
    {
        var beatsSinceTsChange = 0;
        for (var i = 0; i < _beats.Count; i++)
        {
            var beat = _beats[i];
            var tsChanged = i == 0
                            || beat.BeatsInMeasure != _beats[i - 1].BeatsInMeasure
                            || beat.NoteDenominator != _beats[i - 1].NoteDenominator;
            if (tsChanged) beatsSinceTsChange = 0;
            var derived = i == 0 || tsChanged ||
                          (beat.BeatsInMeasure > 0 && beatsSinceTsChange % beat.BeatsInMeasure == 0);
            // One note per beat, at that beat: the report names WHICH bar line moves.
            if (derived != beat.IsMeasure)
            {
                Report.Add(EofConversionKind.Approximated, EofLoss.MeasureStart, EofLosses.BeatsSubject,
                    $"The beat at {ToMsU32(beat.Time)} ms changes its measure-start state — EOF derives measures from time signatures, not per-beat flags.",
                    beat.Time);
            }

            beatsSinceTsChange++;
        }
    }

    /// <summary>
    ///     EOF flags only the FIRST beat of each time signature; intermediate
    ///     measure starts are re-derived on load by counting beats. The anchor
    ///     bit (1) is preserved verbatim.
    /// </summary>
    private uint BuildBeatFlags(int index)
    {
        var beat = _beats[index];
        uint flags = 0;
        // EOF requires beat 0 to be an anchor (main.c:6367, self-repaired on
        // load at beat.c:261-263) and every tempo change is by definition an
        // anchored beat — mirror both invariants instead of relying on the
        // loader's fixup.
        if (beat.IsAnchor || index == 0 ||
            Math.Abs(beat.Bpm - _beats[index - 1].Bpm) > 0.0001)
        {
            flags |= EofConstants.BeatFlagAnchor;
        }

        var tsChanged = index == 0
                        || beat.BeatsInMeasure != _beats[index - 1].BeatsInMeasure
                        || beat.NoteDenominator != _beats[index - 1].NoteDenominator;
        if (tsChanged) flags |= TimeSignatureFlags(beat.BeatsInMeasure, beat.NoteDenominator);
        return flags;
    }

    internal static uint TimeSignatureFlags(int beatsInMeasure, int denominator)
    {
        if (denominator == 4)
        {
            switch (beatsInMeasure)
            {
                case 2: return EofConstants.BeatFlagStart24;
                case 3: return EofConstants.BeatFlagStart34;
                case 4: return EofConstants.BeatFlagStart44;
                case 5: return EofConstants.BeatFlagStart54;
                case 6: return EofConstants.BeatFlagStart64;
            }
        }

        // Custom TS: (numerator-1) in the MSB, (denominator-1) in the 2nd MSB.
        var num = (uint)Math.Clamp(beatsInMeasure, 1, 256) - 1;
        var den = (uint)Math.Clamp(denominator, 1, 256) - 1;
        return EofConstants.BeatFlagCustomTs | (num << 24) | (den << 16);
    }

    private void WriteTextEvents()
    {
        var events = BuildTextEvents();
        _w.WriteU32((uint)events.Count);
        foreach (var ev in events)
        {
            _w.WriteString(ev.Text);
            _w.WriteU32(ev.Pos);
            _w.WriteU16(ev.Track);
            _w.WriteU16(ev.Flags);
        }
    }

    private readonly record struct TextEvent(string Text, uint Pos, ushort Track, ushort Flags, double SortMs);

    /// <summary>
    ///     Turns each arrangement's <see cref="EventPoint"/>s into EOF text
    ///     events on that arrangement's file track. Positions land on beat
    ///     indices when the event sits exactly on a beat; otherwise the
    ///     FLOATING_POS flag carries a millisecond position.
    /// </summary>
    private List<TextEvent> BuildTextEvents()
    {
        var events = new List<TextEvent>();
        var placedPhrases = new Dictionary<Arrangement, HashSet<string>>();
        foreach (var (trackIndex, arrangement) in _assignments.OrderBy(kv => kv.Key))
        {
            var placed = new HashSet<string>();
            placedPhrases[arrangement] = placed;
            var coveredByEventPoint = new HashSet<FractionalPosition>();

            foreach (var ep in arrangement.EventPoints)
            {
                var (pos, floating, ms) = AnchorPosition(ep.Position);

                void Emit(string text, ushort flags)
                {
                    if (floating) flags |= EventFlagFloatingPos;
                    events.Add(new TextEvent(text, pos, (ushort)trackIndex, flags, ms));
                }

                var hasSection = !string.IsNullOrEmpty(ep.Section);
                var hasPhrase = !string.IsNullOrEmpty(ep.Phrase);
                if (hasPhrase)
                {
                    placed.Add(ep.Phrase);
                    coveredByEventPoint.Add(ep.Position);
                }

                var soloFlag = hasPhrase && arrangement.PhraseInfos.TryGetValue(ep.Phrase, out var info) && info.Solo
                    ? EventFlagRsSoloPhrase
                    : (ushort)0;

                if (hasSection && hasPhrase && ep.Section == ep.Phrase)
                {
                    // EOF stores RS section + phrase as ONE event with both bits
                    // (mirrors gp_import.c) — keep that shape when the texts agree.
                    Emit(ep.Section, (ushort)(EventFlagRsSection | EventFlagRsPhrase | soloFlag));
                }
                else
                {
                    if (hasSection) Emit(ep.Section, EventFlagRsSection);
                    if (hasPhrase) Emit(ep.Phrase, (ushort)(EventFlagRsPhrase | soloFlag));
                }

                if (!string.IsNullOrEmpty(ep.Events)) Emit(ep.Events, EventFlagRsEvent);
            }

            EmitExplicitPhraseIterations(events, trackIndex, arrangement, coveredByEventPoint, placed);
        }

        ReportPhraseMetadataLoss(placedPhrases);
        return events.OrderBy(e => e.SortMs).ThenBy(e => e.Track).ToList();
    }

    /// <summary>
    ///     Emits the phrase markers an arrangement states in
    ///     <see cref="Arrangement.PhraseIterations" /> rather than as an
    ///     <see cref="EventPoint.Phrase" />.
    ///     <para>
    ///         <b>A chart states its phrases in TWO stores and this writer only
    ///         ever read one.</b> <c>RsXmlSongReader</c> fills
    ///         <c>Phrases</c>/<c>PhraseIterations</c> and never touches
    ///         <c>EventPoints</c>, so every chart that arrived as RS-XML exported
    ///         an <c>.eof</c> with no RS phrase markers at all — on EVERY Ctrl+S,
    ///         since this is a save companion. Opened in Editor on Fire the chart
    ///         had no phrase structure, and re-importing it lost the solo phrases
    ///         too. This is the mirror of
    ///         <c>RsXmlSongWriter.MergeEditorPhraseMarkers</c>, which merges the
    ///         other way for the same reason, and it is ADDITIVE the same way: a
    ///         chart whose stores agree (an rscp round trip, a psarc import — both
    ///         fill both) matches by POSITION and this adds nothing.
    ///     </para>
    /// </summary>
    private void EmitExplicitPhraseIterations(List<TextEvent> events, int trackIndex, Arrangement arrangement,
        HashSet<FractionalPosition> coveredByEventPoint, HashSet<string> placed)
    {
        foreach (var iteration in arrangement.PhraseIterations)
        {
            if (coveredByEventPoint.Contains(iteration.Position)) continue;
            if (iteration.PhraseId < 0 || iteration.PhraseId >= arrangement.Phrases.Count) continue;

            var name = arrangement.Phrases[iteration.PhraseId];
            if (string.IsNullOrEmpty(name)) continue;

            placed.Add(name);
            var flags = EventFlagRsPhrase;
            if (arrangement.PhraseInfos.TryGetValue(name, out var info) && info.Solo)
                flags |= EventFlagRsSoloPhrase;

            var (pos, floating, ms) = AnchorPosition(iteration.Position);
            if (floating) flags |= EventFlagFloatingPos;
            events.Add(new TextEvent(name, pos, (ushort)trackIndex, flags, ms));
        }
    }

    private void ReportPhraseMetadataLoss(IReadOnlyDictionary<Arrangement, HashSet<string>> placedPhrases)
    {
        foreach (var (_, arrangement) in _assignments)
        {
            var referenced = placedPhrases.TryGetValue(arrangement, out var placed) ? placed : [];
            foreach (var phrase in arrangement.Phrases.Where(p => !referenced.Contains(p)))
            {
                Report.Add(EofConversionKind.Dropped, EofLoss.UnplacedPhrase, arrangement.Name,
                    $"Phrase \"{phrase}\" is never placed by an event point — EOF only stores placed phrases.",
                    detail: phrase);
            }

            foreach (var (name, info) in arrangement.PhraseInfos)
            {
                if (info.MaxDifficulty != 0)
                {
                    Report.Add(EofConversionKind.Approximated, EofLoss.PhraseMaxDifficulty, arrangement.Name,
                        $"Phrase \"{name}\" max difficulty {info.MaxDifficulty} is not representable in EOF (solo flag survives).",
                        detail: string.Create(CultureInfo.InvariantCulture, $"{name} ({info.MaxDifficulty})"));
                }
            }
        }
    }

    /// <summary>Beat-index anchoring: an event exactly on a beat stores the beat
    /// number; anything else stores milliseconds with the FLOATING_POS flag.</summary>
    private (uint Pos, bool Floating, double Ms) AnchorPosition(FractionalPosition position)
    {
        var ms = TimelineMath.CalculateTimeMs(position, _beats);
        if (position.Fraction.Numerator == 0 && position.BeatId >= 0 && position.BeatId < _beats.Count)
            return ((uint)position.BeatId, false, ms);
        return (ToMsU32(ms), true, ms);
    }

    private void WriteGlobalCustomDataBlocks()
    {
        if (_beats.Count == 0)
        {
            // No beats — write the debug block exactly like eof_save_song.
            _w.WriteU32(1);
            _w.WriteU32(4);
            _w.WriteU32(0xFFFFFFFF);
            return;
        }

        // Floating-point beat timings (block ID 2). TRAP: unlike every other
        // custom data block, the size written for ID 2 covers the string
        // payload ONLY — it does NOT include the 4-byte block ID (eof_save_song
        // writes file_size_ex(temp) where the temp file holds just the strings).
        var payload = new EofPackWriter();
        foreach (var beat in _beats)
            payload.WriteString(beat.Time.ToString("F15", System.Globalization.CultureInfo.InvariantCulture));

        _w.WriteU32(1); // One global custom data block.
        _w.WriteU32((uint)payload.Length);
        _w.WriteU32(2);
        _w.WriteBytes(payload.ToArray());
    }

    private void WriteTracks()
    {
        var trackCount = _writeBonusTrack ? EofTrackScaffold.Tracks.Length + 1 : EofTrackScaffold.Tracks.Length;
        _w.WriteU32((uint)trackCount);

        WriteGlobalTrack();
        foreach (var track in EofTrackScaffold.Tracks)
        {
            if (track.Index == EofTrackScaffold.ProGuitarBonusIndex && !_writeBonusTrack)
                continue; // Empty bonus track is omitted, like eof_save_song's omit_bonus.

            if (_assignments.TryGetValue(track.Index, out var arrangement))
                WriteProGuitarTrack(track, arrangement);
            else
                WriteEmptyTrack(track);
        }
    }

    private void WriteGlobalTrack()
    {
        _w.WriteString(null); // Track 0 has no name.
        _w.WriteU8(0);        // Format (global).
        _w.WriteU8(0);        // Behavior.
        _w.WriteU8(0);        // Type.
        _w.WriteU8(0);        // Difficulty.
        _w.WriteU32(0);       // Flags.
        _w.WriteU16(0xFFFF);  // Compliance flags (track 0 only).

        // Section chunk: bookmarks when the project has any (EOF caps at 10).
        var bookmarks = _project.Bookmarks.Take(10).ToList();
        _w.WriteU16((ushort)(bookmarks.Count > 0 ? 1 : 0));
        if (bookmarks.Count > 0)
        {
            _w.WriteU16(EofConstants.SectionBookmark);
            _w.WriteU32((uint)bookmarks.Count);
            foreach (var bookmark in bookmarks)
            {
                _w.WriteString(null);
                _w.WriteU8(0xFF);
                _w.WriteU32(ToMsU32(bookmark.Time));
                _w.WriteU32((uint)Math.Max(0, bookmark.Index));
                _w.WriteU32(0);
            }
        }

        WriteDebugCustomBlock();
    }

    private void WriteTrackHeader(EofScaffoldTrack track, string? altName)
    {
        _w.WriteString(track.Name);
        _w.WriteU8(track.Format);
        _w.WriteU8(track.Behavior);
        _w.WriteU8(track.Type);
        _w.WriteU8(0xFF); // Track difficulty: undefined.
        var flags = track.Flags;
        if (!string.IsNullOrEmpty(altName)) flags |= EofConstants.TrackFlagAltName;
        _w.WriteU32(flags);
        _w.WriteU16(0); // Compliance flags.
        if (!string.IsNullOrEmpty(altName)) _w.WriteString(altName);
    }

    private void WriteEmptyTrack(EofScaffoldTrack track)
    {
        WriteTrackHeader(track, null);
        switch (track.Format)
        {
            case EofConstants.TrackFormatLegacy:
                _w.WriteU8(track.NumLanes);
                _w.WriteU32(0); // No notes.
                _w.WriteU16(0); // No section types.
                WriteDebugCustomBlock();
                break;
            case EofConstants.TrackFormatVocal:
                WriteVocalTrackBody();
                break;
            case EofConstants.TrackFormatProGuitar:
                _w.WriteU8(DefaultNumFrets);
                _w.WriteU8(track.NumStrings);
                for (var i = 0; i < track.NumStrings; i++) _w.WriteU8(0);
                _w.WriteU32(0); // No notes.
                _w.WriteU16(0); // No section types.
                // EOF always writes the "ignore tuning" block for pro guitar
                // tracks, and stamps the bass slots' arrangement type at
                // project creation (song.c:4567-4571).
                var isBassSlot = EofTrackScaffold.BassSlotIndices.Contains(track.Index);
                _w.WriteU32((uint)(isBassSlot ? 2 : 1));
                if (isBassSlot)
                    WriteByteCustomBlock(EofConstants.PgCustomArrangementType, EofConstants.ArrTypeBass);
                WriteByteCustomBlock(EofConstants.PgCustomIgnoreTuning, 1);
                break;
            default:
                throw new InvalidOperationException($"Unexpected scaffold track format {track.Format}.");
        }
    }

    /// <summary>The debug custom data block eof_save_song emits when a track has no real blocks.</summary>
    private void WriteDebugCustomBlock()
    {
        _w.WriteU32(1);
        _w.WriteU32(4);
        _w.WriteU32(0xFFFFFFFF);
    }

    /// <summary>One-byte track custom data block; size (5) includes the 4-byte block ID.</summary>
    private void WriteByteCustomBlock(uint id, byte value)
    {
        _w.WriteU32(5);
        _w.WriteU32(id);
        _w.WriteU8(value);
    }

    // ------------------------------------------------------------------
    // Vocals

    private VocalTrack? FindVocalTrack() => VocalsExportPolicy.PackageTrack(_project, _includeVocals)?.Track;

    private void WriteVocalTrackBody()
    {
        var vocals = FindVocalTrack();
        _w.WriteU8(0); // Tone set.

        var populated = VocalsExportPolicy.TracksToWrite(_project, _includeVocals).Count;
        if (populated > 1)
        {
            Report.Add(EofConversionKind.Dropped, EofLoss.ExtraVocalTrack, EofLosses.VocalsSubject,
                $"{populated - 1} additional vocal track(s) dropped — EOF has a single PART VOCALS track.",
                detail: (populated - 1).ToString(CultureInfo.InvariantCulture));
        }

        if (vocals == null)
        {
            _w.WriteU32(0); // No lyrics.
            _w.WriteU16(0); // No section types.
            WriteDebugCustomBlock();
            return;
        }

        var lyrics = vocals.Phrases
            .SelectMany(p => p.Lyrics)
            .OrderBy(l => l.Position)
            .ToList();

        _w.WriteU32((uint)lyrics.Count);
        foreach (var lyric in lyrics)
        {
            var text = lyric.Text;
            if (lyric.IsWordPart && !text.EndsWith('-')) text += "-";
            _w.WriteString(text);
            _w.WriteU8(0); // Lyric set.
            // EOF pitch 0 = unpitched; DotCharter uses 254/255 sentinels for that.
            var pitched = lyric.Note is > 0 and < 128;
            if (!pitched && lyric.Note is not (0 or 254 or 255))
            {
                Report.Add(EofConversionKind.Approximated, EofLoss.LyricPitch, EofLosses.VocalsSubject,
                    $"Lyric \"{lyric.Text}\" pitch {lyric.Note} is outside EOF's MIDI range and exports unpitched.",
                    ToMs(lyric.Position), string.Create(CultureInfo.InvariantCulture, $"\"{lyric.Text}\" ({lyric.Note})"));
            }

            _w.WriteU8(pitched ? (byte)lyric.Note : (byte)0);
            var startMs = ToMs(lyric.Position);
            var endMs = ToMs(lyric.EndPosition);
            _w.WriteU32(ToMsU32(startMs));
            _w.WriteU32(ToMsU32(Math.Max(0, endMs - startMs)));
            _w.WriteU32(0); // Lyric flags.
        }

        var lines = vocals.Phrases.OrderBy(p => p.Position).ToList();
        _w.WriteU16((ushort)(lines.Count > 0 ? 1 : 0));
        if (lines.Count > 0)
        {
            _w.WriteU16(EofConstants.SectionLyricPhrase);
            _w.WriteU32((uint)lines.Count);
            foreach (var line in lines)
            {
                _w.WriteString(null);
                _w.WriteU8(0);
                _w.WriteU32(ToMsU32(ToMs(line.Position)));
                _w.WriteU32(ToMsU32(ToMs(line.EndPosition)));
                _w.WriteU32(0);
            }
        }

        WriteDebugCustomBlock();
    }

    // ------------------------------------------------------------------
    // Pro guitar tracks

    private void WriteProGuitarTrack(EofScaffoldTrack track, Arrangement arrangement)
    {
        var altName = !string.IsNullOrWhiteSpace(arrangement.Name) && arrangement.Name != track.Name
            ? arrangement.Name
            : null;
        WriteTrackHeader(track, altName);

        if (Math.Abs(arrangement.CentOffset) > 0.0001)
        {
            Report.Add(EofConversionKind.Dropped, EofLoss.CentOffset, arrangement.Name,
                $"Cent offset {arrangement.CentOffset} is not representable in an EOF project.",
                detail: arrangement.CentOffset.ToString("+0.##;-0.##", CultureInfo.InvariantCulture));
        }
        // A chart states its tuning TWICE — letters and offsets — and this is the
        // one answer for anything that WRITES one. Reading TuningPitches directly
        // exported an arrangement that only had LETTERS as E standard, which is
        // the exact bug TuningOffsetResolution was introduced for; this writer
        // runs on every save and was the one left behind.
        var tuningOffsets = TuningOffsetResolution.Resolve(arrangement);
        if (tuningOffsets.Count > 0 && tuningOffsets.Count != arrangement.StringCount)
        {
            Report.Add(EofConversionKind.Approximated, EofLoss.TuningStringCount, arrangement.Name,
                $"Tuning defines {tuningOffsets.Count} strings but the arrangement has {arrangement.StringCount}; missing strings export as standard.",
                detail: string.Create(CultureInfo.InvariantCulture, $"{tuningOffsets.Count} / {arrangement.StringCount}"));
        }

        var numStrings = (byte)Math.Clamp(arrangement.StringCount, 1, 8);
        _w.WriteU8(DefaultNumFrets);
        _w.WriteU8(numStrings);
        for (var s = 0; s < numStrings; s++)
        {
            var step = s < tuningOffsets.Count ? tuningOffsets[s] : 0;
            _w.WriteU8((byte)(sbyte)Math.Clamp(step, sbyte.MinValue, sbyte.MaxValue));
        }

        var notes = EofNoteBuilder.Build(arrangement, _beats, Report);
        _w.WriteU32((uint)notes.Count);
        foreach (var note in notes)
            EofProGuitarNoteWriter.Write(_w, note.Raw);

        WriteProGuitarSections(arrangement);
        WriteProGuitarCustomBlocks(arrangement, notes, numStrings);
    }

    private void WriteProGuitarSections(Arrangement arrangement)
    {
        var capo = arrangement.Capo;
        var templatesById = new Dictionary<int, ChordTemplate>();
        foreach (var template in arrangement.ChordTemplates) templatesById.TryAdd(template.Id, template);

        // Arpeggio/handshape sections from the levels' hand shapes. EOF keeps
        // both in the ARPEGGIO section array; the RS_ARP_HANDSHAPE flag is the
        // only thing separating a plain handshape from a true arpeggio — with
        // it missing, EOF treats the whole chart as arpeggio phrases.
        var arpeggios = new List<(byte Diff, uint Start, uint End, uint Flags)>();
        // Tremolo sections from runs of tremolo notes.
        var tremolos = new List<(byte Diff, uint Start, uint End)>();
        // Fret hand positions (end position doubles as the fret number).
        var anchors = new List<(byte Diff, uint Start, uint Fret)>();

        foreach (var level in arrangement.Levels.OrderBy(l => l.DifficultyIndex))
        {
            var diff = ClampDiff(level.DifficultyIndex);
            // ONE ordered view of the level's chords for the whole hand-shape
            // pass. The shape-loss report must not scan (and sort) the entire
            // chord collection per hand shape; see ChordPositionIndex.
            var chordsByPosition = level.HandShapes.Count > 0
                ? new ChordPositionIndex(level.Chords)
                : null;
            foreach (var hs in level.HandShapes.OrderBy(h => h.Position))
            {
                // BOTH spellings, via ChordTemplate.IsArpeggio: an RS-XML or
                // psarc import states an arpeggio as ForceArpeggioInRs (from the
                // `-arp` displayName) and never touches Arpeggio, so reading the
                // latter alone shipped every imported arpeggio into the .eof as
                // an ordinary hand shape — the SNG mapper and the RS-XML writer
                // have folded the two together for releases.
                var isArpeggio = templatesById.TryGetValue(hs.TemplateId, out var template) && template.IsArpeggio;
                arpeggios.Add((diff, ToMsU32(ToMs(hs.Position)), ToMsU32(ToMs(hs.EndPosition)),
                    isArpeggio ? 0u : EofConstants.SectionFlagRsArpHandshape));
                ReportHandShapeShapeLoss(arrangement, chordsByPosition!, hs);
            }

            foreach (var run in BuildTremoloRuns(level))
                tremolos.Add((diff, run.Start, run.End));
            foreach (var chord in level.Chords)
            {
                if (chord.ChordNotes.Any(cn => cn.Tremolo) && !IsTremoloChord(chord))
                {
                    Report.Add(EofConversionKind.Dropped, EofLoss.PartialChordTremolo, arrangement.Name,
                        $"Chord tremolo on some strings at {ToMsU32(ToMs(chord.Position))} ms is not representable (an EOF tremolo phrase covers every string of a chord).",
                        ToMs(chord.Position));
                }
            }

            foreach (var anchor in level.Anchors.OrderBy(a => a.Position))
            {
                var fret = anchor.Fret - capo;
                if (fret <= 0)
                {
                    Report.Add(EofConversionKind.Dropped, EofLoss.AnchorOnCapo, arrangement.Name,
                        $"Fret hand position at {ToMsU32(ToMs(anchor.Position))} ms lands on fret {fret} after removing the capo — EOF skips fret 0 positions.",
                        ToMs(anchor.Position), anchor.Fret.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                if (anchor.Width != 4)
                {
                    Report.Add(EofConversionKind.Approximated, EofLoss.AnchorWidth, arrangement.Name,
                        $"Fret hand position at {ToMsU32(ToMs(anchor.Position))} ms has width {anchor.Width}; EOF stores no width (default 4).",
                        ToMs(anchor.Position), anchor.Width.ToString(CultureInfo.InvariantCulture));
                }

                anchors.Add((diff, ToMsU32(ToMs(anchor.Position)), (uint)fret));
            }
        }

        // Tone changes; the default tone is flagged via end position == 1.
        var toneChanges = arrangement.ToneChanges.OrderBy(t => t.Position)
            .Where(t => !string.IsNullOrWhiteSpace(t.ToneName))
            .Select(t => (t.ToneName, Start: ToMsU32(ToMs(t.Position))))
            .ToList();
        var defaultTone = ResolveDefaultTone(arrangement, toneChanges.Select(t => t.ToneName).ToList());
        ReportToneLoss(arrangement, toneChanges.Select(t => t.ToneName).ToList(), defaultTone);

        var typeCount = (ushort)((arpeggios.Count > 0 ? 1 : 0) + (tremolos.Count > 0 ? 1 : 0) +
                                 (anchors.Count > 0 ? 1 : 0) + (toneChanges.Count > 0 ? 1 : 0));
        _w.WriteU16(typeCount);

        if (arpeggios.Count > 0)
        {
            _w.WriteU16(EofConstants.SectionArpeggio);
            _w.WriteU32((uint)arpeggios.Count);
            foreach (var (diff, start, end, sectionFlags) in arpeggios)
                WriteSectionInstance(null, diff, start, end, sectionFlags);
        }

        if (tremolos.Count > 0)
        {
            _w.WriteU16(EofConstants.SectionTremolo);
            _w.WriteU32((uint)tremolos.Count);
            foreach (var (diff, start, end) in tremolos)
                WriteSectionInstance(null, diff, start, end);
        }

        if (anchors.Count > 0)
        {
            _w.WriteU16(EofConstants.SectionFretHandPos);
            _w.WriteU32((uint)anchors.Count);
            foreach (var (diff, start, fret) in anchors)
                WriteSectionInstance(null, diff, start, fret);
        }

        if (toneChanges.Count > 0)
        {
            _w.WriteU16(EofConstants.SectionRsToneChange);
            _w.WriteU32((uint)toneChanges.Count);
            foreach (var (name, start) in toneChanges)
                WriteSectionInstance(name, 0xFF, start, name == defaultTone ? 1u : 0u);
        }
    }

    internal static string? ResolveDefaultTone(Arrangement arrangement, IReadOnlyList<string> toneNames)
    {
        if (toneNames.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(arrangement.StartingTone) && toneNames.Contains(arrangement.StartingTone))
            return arrangement.StartingTone;
        return toneNames[0];
    }

    private void ReportToneLoss(Arrangement arrangement, IReadOnlyList<string> toneChangeNames, string? defaultTone)
    {
        if (!string.IsNullOrWhiteSpace(arrangement.StartingTone) && arrangement.StartingTone != defaultTone)
        {
            Report.Add(EofConversionKind.Approximated,
                toneChangeNames.Count == 0 ? EofLoss.StartingToneWithoutChange : EofLoss.StartingToneReplaced,
                arrangement.Name,
                toneChangeNames.Count == 0
                    ? $"Starting tone \"{arrangement.StartingTone}\" has no tone-change marker — EOF only stores the default tone via a tone change."
                    : $"Starting tone \"{arrangement.StartingTone}\" is not among the tone changes; \"{defaultTone}\" becomes the default tone.",
                detail: toneChangeNames.Count == 0 ? arrangement.StartingTone : $"{arrangement.StartingTone} → {defaultTone}");
        }

        foreach (var name in arrangement.ToneNames.Where(n => !toneChangeNames.Contains(n)))
        {
            Report.Add(EofConversionKind.Dropped, EofLoss.UnusedTone, arrangement.Name,
                $"Tone \"{name}\" is never used by a tone change — EOF stores no free-standing tone list.",
                detail: name);
        }
    }

    /// <summary>
    ///     EOF stores a handshape as a bare window; on import the shape template
    ///     is re-bound to the first chord inside it. Report handshapes whose
    ///     template would not survive that re-binding.
    /// </summary>
    private void ReportHandShapeShapeLoss(Arrangement arrangement, ChordPositionIndex chords, HandShape hs)
    {
        // "The first chord inside the window, else the first chord after it" is
        // one question: the first chord at or after the window's START. See
        // ChordPositionIndex for why the two branches collapse, and for the
        // O(handShapes x chords log chords) pass this replaced.
        var resolved = chords.FirstAtOrAfter(hs.Position);
        if (resolved?.TemplateId != hs.TemplateId)
        {
            Report.Add(EofConversionKind.Approximated, EofLoss.HandShapeShape, arrangement.Name,
                $"Handshape at {ToMsU32(ToMs(hs.Position))} ms carries a shape that differs from the chords inside it — readers re-derive the shape from the nearest chord.",
                ToMs(hs.Position));
        }
    }

    private void WriteSectionInstance(string? name, byte diff, uint start, uint end, uint flags = 0)
    {
        _w.WriteString(name);
        _w.WriteU8(diff);
        _w.WriteU32(start);
        _w.WriteU32(end);
        _w.WriteU32(flags);
    }

    /// <summary>A chord tremolo on every string — what an EOF tremolo phrase over a
    /// chord means (its phrase status marks the whole note, rs.c:5735).</summary>
    private static bool IsTremoloChord(Chord chord) => chord.ChordNotes.Count > 0 && chord.ChordNotes.All(cn => cn.Tremolo);

    /// <summary>
    ///     Merges consecutive tremolo sounds of a level into tremolo section
    /// windows: single notes, and chords tremolo on every string. A run
    ///     breaks at any other note or chord so the window never claims a sound
    ///     that isn't tremolo.
    /// </summary>
    private List<(uint Start, uint End)> BuildTremoloRuns(ChartLevel level)
    {
        var sounds = new List<(double Ms, double EndMs, bool Tremolo)>();
        foreach (var note in level.Notes)
        {
            var ms = ToMs(note.Position);
            sounds.Add((ms, ToMs(note.EndPosition), note.Tremolo));
        }

        foreach (var chord in level.Chords)
        {
            var ms = ToMs(chord.Position);
            var tremolo = IsTremoloChord(chord);
            sounds.Add((ms, tremolo ? ToMs(chord.EndPosition) : ms, tremolo));
        }

        sounds.Sort((a, b) => a.Ms.CompareTo(b.Ms));

        var runs = new List<(uint, uint)>();
        double? runStart = null;
        double runEnd = 0;
        foreach (var sound in sounds)
        {
            if (sound.Tremolo)
            {
                runStart ??= sound.Ms;
                runEnd = Math.Max(runEnd, sound.EndMs);
            }
            else if (runStart != null)
            {
                runs.Add((ToMsU32(runStart.Value), ToMsU32(runEnd)));
                runStart = null;
            }
        }

        if (runStart != null) runs.Add((ToMsU32(runStart.Value), ToMsU32(runEnd)));
        return runs;
    }

    private void WriteProGuitarCustomBlocks(Arrangement arrangement, IReadOnlyList<EofExportNote> notes,
        byte numStrings)
    {
        // Finger definitions: one byte per used string per note, in note order
        // (custom block 2) — written whenever the track has notes, like EOF.
        var fingerBytes = new List<byte>();
        foreach (var note in notes)
        {
            for (var s = 0; s < numStrings; s++)
            {
                if ((note.Raw.NoteBitmask & (1 << s)) == 0) continue;
                fingerBytes.Add(note.Fingers.TryGetValue(s, out var finger) ? ToEofFinger(finger) : (byte)0);
            }
        }

        var maxDiffCount = arrangement.Levels.Count > 0
            ? arrangement.Levels.Max(l => l.DifficultyIndex) + 1
            : 0;

        // Bend-curve tech notes (custom block 7): the full point list of every
        // non-two-point bend, position-sorted — the note itself only carries
        // the peak strength.
        var techNotes = notes.SelectMany(n => n.BendTechNotes).OrderBy(t => t.PositionMs).ToList();

        var blockCount = (fingerBytes.Count > 0 ? 1 : 0) // Fingers
                         + (techNotes.Count > 0 ? 1 : 0) // Tech notes
                         + 1                              // Arrangement type
                         + 1                              // Ignore tuning
                         + (arrangement.Capo != 0 ? 1 : 0)
                         + (maxDiffCount > 5 ? 1 : 0);
        _w.WriteU32((uint)blockCount);

        if (fingerBytes.Count > 0)
        {
            _w.WriteU32((uint)(fingerBytes.Count + 4));
            _w.WriteU32(EofConstants.PgCustomFingerArray);
            foreach (var b in fingerBytes) _w.WriteU8(b);
        }

        if (techNotes.Count > 0)
        {
            var payload = new EofPackWriter();
            payload.WriteU32((uint)techNotes.Count);
            foreach (var tech in techNotes) EofProGuitarNoteWriter.Write(payload, tech);
            _w.WriteU32((uint)(payload.Length + 4)); // Size includes the block ID (unlike global block 2).
            _w.WriteU32(EofConstants.PgCustomTechNotes);
            _w.WriteBytes(payload.ToArray());
        }

        WriteByteCustomBlock(EofConstants.PgCustomArrangementType, ToEofArrangementType(arrangement.ArrangementType));
        WriteByteCustomBlock(EofConstants.PgCustomIgnoreTuning, 1);
        if (arrangement.Capo != 0)
            WriteByteCustomBlock(EofConstants.PgCustomCapoPosition, (byte)Math.Clamp(arrangement.Capo, 0, 127));
        if (maxDiffCount > 5)
            WriteByteCustomBlock(EofConstants.PgCustomNumDiffs, (byte)Math.Clamp(maxDiffCount, 6, 255));
    }

    /// <summary>DotCharter finger 0 = thumb; EOF stores thumb as 5 (1..4 line up).</summary>
    internal static byte ToEofFinger(int finger) => finger == 0 ? (byte)5 : (byte)Math.Clamp(finger, 1, 5);

    internal static byte ToEofArrangementType(ArrangementType type)
    {
        return type switch
        {
            ArrangementType.Combo => EofConstants.ArrTypeCombo,
            ArrangementType.Rhythm => EofConstants.ArrTypeRhythm,
            ArrangementType.Bass => EofConstants.ArrTypeBass,
            _ => EofConstants.ArrTypeLead
        };
    }

    private static byte ClampDiff(int difficultyIndex) => (byte)Math.Clamp(difficultyIndex, 0, 254);

    private double ToMs(FractionalPosition position) => TimelineMath.CalculateTimeMs(position, _beats);

    internal static uint ToMsU32(double ms)
    {
        if (double.IsNaN(ms) || ms <= 0) return 0;
        return (uint)Math.Min(Math.Round(ms), uint.MaxValue);
    }
}
