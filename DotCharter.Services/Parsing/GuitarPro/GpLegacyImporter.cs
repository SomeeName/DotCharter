using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using DotChord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using TuningOffset = DotCharter.Core.Domain.Project.TuningOffset;
using TrackHeader = DotCharter.Services.Parsing.GuitarPro.GpLegacyTrackHeader;
using MeasureHeader = DotCharter.Services.Parsing.GuitarPro.GpLegacyMeasureHeader;
using ParseResult = DotCharter.Services.Parsing.GuitarPro.GpLegacyParseResult;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Imports the binary Guitar Pro 3/4/5 (<c>.gp3</c>, <c>.gp4</c>, <c>.gp5</c>)
///     score formats and produces the same <see cref="GpImportResult"/> shape as
///     the XML-based <see cref="GpImporter"/>. The post-import passes
///     (<see cref="GpPostProcessor"/>, <see cref="DotCharter.Services.Editing.FretAnchors.FretAnchorGenerationService"/>) are
///     reused unchanged.
/// </summary>
internal sealed class GpLegacyImporter
{
    private readonly Dictionary<int, ChartLevel> _levelByTrack = [];
    private readonly Dictionary<int, NotationVoice> _sidecarByTrack = [];
    private readonly List<TrackHeader> _tracks = [];
    private readonly List<MeasureHeader> _measureHeaders = [];

    /// <summary>
    ///     The measure headers in PLAYBACK order — repeats and alternate endings
    ///     expanded by <see cref="GpMasterBarSequence" />, the same unfold the
    ///     gpif path runs. <see cref="_unfoldedBarStarts" /> is each unfolded
    ///     bar's chart position and <see cref="_unfoldedByOriginal" /> maps an
    ///     authored measure index to every unfolded slot that plays it (empty
    ///     only for an alternate ending no pass reaches). The byte stream still
    ///     holds ONE block per authored measure, so a repeated measure is read
    ///     once and its sounds are cloned to the later starts.
    /// </summary>
    private List<GpMasterBarSequence.UnfoldedIndex> _unfolded = [];

    /// <inheritdoc cref="_unfolded" />
    private FractionalPosition[] _unfoldedBarStarts = [];

    /// <inheritdoc cref="_unfolded" />
    private List<int>[] _unfoldedByOriginal = [];
    private readonly GpLegacyParseState _state = new();

    /// <summary>
    ///     The two swing spellings shared with the GPIF path. They are the
    ///     verbatim GPIF <c>&lt;TripletFeel&gt;</c> element values, because
    ///     <c>GpBarInfo.TripletFeel</c> is consumed by string comparison
    ///     (<c>GpArrangementParser.ApplyFeelToPosition</c>) and must mean the same
    ///     thing whichever importer wrote it. Guitar Pro's binary formats only
    ///     ever encode these two of the six GPIF feels — the "dotted" and
    ///     "Scottish" (inverse) feels are GP6+/GPIF-only, so there is deliberately
    ///     no legacy mapping for them.
    /// </summary>
    private const string TripletFeelEighth = "Triplet8th";

    private const string TripletFeelSixteenth = "Triplet16th";

    /// <summary>
    ///     GP3/GP4 carry ONE song-wide swing flag (a boolean in the pre-song
    ///     block) instead of GP5's per-bar byte, so it is captured here and
    ///     stamped onto every measure header. Empty = no feel.
    /// </summary>
    private string _globalTripletFeel = "";

    /// <summary>
    ///     The notes whose grace ornament was authored ON the beat (GP5 grace flag
    ///     bit <c>0x02</c>), by reference identity. Guitar Pro's two grace flavours
    ///     place differently and audibly — a BEFORE-beat grace steals time ahead of
    ///     the beat, an ON-beat one sits on the beat and pushes its target late —
    ///     and a reader that discarded the bit would place every binary grace
    ///     before the beat while the same score in gpif form places it correctly.
    ///     <para>
    ///     Deliberately an import-only side table rather than a domain field: the
    ///     flavour is consumed during import (by <see cref="ApplyChordGraces"/> at
    ///     beat flush and by <see cref="GpPostProcessor.MaterializeGraceSlides"/>
    ///     for single notes) and never reaches the chart model. GP3/GP4 store no
    ///     such bit, so their notes are simply never added — which is Log Charter's
    ///     pre-500 default of before-beat.
    ///     </para>
    /// </summary>
    private readonly HashSet<GuitarNote> _onBeatGraces = [];

    /// <summary>
    ///     Import-only side table: note ⇒ the trill's ABSOLUTE upper fret
    ///     (note-effects fx2 bit <c>0x20</c>). The binary reader must not
    ///     <c>Skip(2)</c> those bytes, or the same score imported as .gp5 would lose
    ///     every trill the gpif path expands. The expansion needs the note's
    ///     final position/sustain and the bar end, neither of which the note read
    ///     knows, so the upper fret is parked here and consumed at beat flush by
    ///     the shared <see cref="GpArrangementParser.ExpandTrill" />. Reference
    ///     identity, like <see cref="_onBeatGraces" />; never a domain field.
    /// </summary>
    private readonly Dictionary<GuitarNote, int> _pendingTrills = [];

    /// <summary>
    ///     Import-only side table: note ⇒ its AUTHORED left-hand finger, by
    ///     reference identity. The note read knows the finger but not the chart
    ///     POSITION, so the value waits here until beat flush moves it into
    ///     <see cref="_authoredFingersByTrack" />.
    /// </summary>
    private readonly Dictionary<GuitarNote, int> _pendingFingers = [];

    /// <summary>
    ///     The AUTHORED left-hand fingering, by (string, position), handed to
    ///     <see cref="GpPostProcessor.Run" /> so a new chord template prefers the
    ///     file's finger over <c>ChordTemplateFingerSetter</c>'s guess. Populated
    ///     from the PLAYABLE voice only.
    ///     <para>
    ///     <b>Keyed by TRACK.</b> One table for the whole file would key on
    ///     (string, position) — so in any multi-track score the rhythm
    ///     guitar's chord template could be fingered from the LEAD's note on the
    ///     same string at the same instant, which in a real arrangement is most
    ///     of them. <see cref="GpAuthoredFingering.Set" /> keeps the FIRST value
    ///     for a key, so the earlier track would silently win.
    ///     </para>
    /// </summary>
    private readonly Dictionary<int, GpAuthoredFingering> _authoredFingersByTrack = [];

    private GpAuthoredFingering AuthoredFingersFor(int trackId)
    {
        if (!_authoredFingersByTrack.TryGetValue(trackId, out var fingers))
            _authoredFingersByTrack[trackId] = fingers = new GpAuthoredFingering();
        return fingers;
    }

    /// <summary>
    ///     The AUTHORED chord NAME of each strum, by position, per TRACK — read
    ///     off the beat's chord diagram (<see cref="ReadChordDiagram" />) and
    ///     handed to <see cref="GpPostProcessor.Run" /> so a new chord template
    ///     keeps the arranger's name instead of <c>ChordNameSuggester</c>'s
    ///     pitch-class guess. Populated from the PLAYABLE voice only.
    /// </summary>
    private readonly Dictionary<int, GpAuthoredChordNames> _authoredChordNamesByTrack = [];

    private GpAuthoredChordNames AuthoredChordNamesFor(int trackId)
    {
        if (!_authoredChordNamesByTrack.TryGetValue(trackId, out var names))
            _authoredChordNamesByTrack[trackId] = names = new GpAuthoredChordNames();
        return names;
    }

    private int _majorVersion;
    private GpVersion _version;
    private GpImportSettings _settings = GpImportSettings.Default;

    // Song-attributes header metadata, captured by ReadSongAttributes and
    // handed out through GpLegacyParseResult -> GpImportMetadata.
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _tabber = "";
    private bool IsAfter50 => _version.IsAfter50;
    private bool IsVersion5 => _version.IsVersion5;
    private bool IsVersion4Plus => _version.IsVersion4Plus;

    public List<GpTrackInfo> GetTracks(string filePath)
    {
        var result = ParseFile(filePath, headersOnly: true);
        return result.Tracks;
    }

    public GpImportResult Import(string filePath, List<int> trackIdsToImport, GpImportSettings? settings = null)
    {
        _settings = settings ?? GpImportSettings.Default;
        var parsed = ParseFile(filePath, headersOnly: false);

        var importResult = new GpImportResult();
        foreach (var bar in parsed.Bars) importResult.Metadata.Bars.Add(bar);
        foreach (var tp in parsed.TempoPoints) importResult.Metadata.TempoPoints.Add(tp);
        importResult.Metadata.KeyAccidentals = parsed.KeyAccidentals;
        importResult.Metadata.KeyIsMinor = parsed.KeyIsMinor;
        importResult.Metadata.Title = parsed.Title;
        importResult.Metadata.Artist = parsed.Artist;
        importResult.Metadata.Album = parsed.Album;
        importResult.Metadata.Tabber = parsed.Tabber;

        foreach (var track in _tracks)
        {
            if (!trackIdsToImport.Contains(track.Id)) continue;
            if (!_levelByTrack.TryGetValue(track.Id, out var level)) continue;

            var arr = BuildArrangement(track, level);
            // _onBeatGraces carries GP5's per-grace BeforeBeat/OnBeat flavour into
            // the single-note materializer; without it every binary grace placed
            // before the beat regardless of what the file said.
            GpPostProcessor.Run(level, arr, _settings, _onBeatGraces,
                authoredFingers: AuthoredFingersFor(track.Id),
                authoredChordNames: AuthoredChordNamesFor(track.Id));
            _state.LetRingByLevel.TryGetValue(level, out var letRingSet);
            GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRingSet);
            // Let-ring shapes join the chord shapes AFTER Run's own clamp, so the
            // combined set has to be re-clamped or a let-ring shape overlaps the
            // next chord shape until the save-time spacing enforcer clips it. The
            // gpif path has always done this; the two must stay symmetric.
            GpPostProcessor.ClampHandShapes(level);
            // arr.Capo is load-bearing and was missing here while the gpif and
            // Songsterr paths both passed it: without it the generator uses a
            // floor of fret 1 and treats only fret 0 as open, so a capo'd binary
            // import lays anchors BELOW the capo (validation rule L02). A capo of
            // 0 — every uncapoed file — is a strict no-op, so this only ever
            // changes output for scores that were already wrong.
            // A pick scrape sits on the anchor in force at its time.
            GpPickScrape.RunImportFhpPass(arr, level, AuthoredFingersFor(track.Id).IsAuthoredTemplate,
                GpPickScrape.LastFret(track.Frets));
            arr.Levels.Add(level);

            AttachSidecar(track, arr);

            importResult.Arrangements.Add(arr);
        }

        return importResult;
    }

    private ParseResult ParseFile(string filePath, bool headersOnly)
    {
        _tracks.Clear();
        _measureHeaders.Clear();
        _unfolded = [];
        _unfoldedBarStarts = [];
        _unfoldedByOriginal = [];
        _levelByTrack.Clear();
        _sidecarByTrack.Clear();
        _globalTripletFeel = "";
        _title = "";
        _artist = "";
        _album = "";
        _tabber = "";
        _onBeatGraces.Clear();
        _pendingTrills.Clear();
        _pendingFingers.Clear();
        _authoredFingersByTrack.Clear();
        _authoredChordNamesByTrack.Clear();
        _state.Reset();

        using var stream = File.OpenRead(filePath);
        var reader = new GpLegacyBinaryReader(stream);

        ReadVersion(reader);
        ReadSongAttributes(reader);
        ReadLyricsAndPreSongData(reader);

        var initialBpm = reader.ReadInt32();
        if (IsAfter50) reader.Skip(1);

        int keyAccidentals;
        var keyIsMinor = false;
        if (IsVersion4Plus)
        {
            keyAccidentals = reader.ReadSByte();
            reader.Skip(3);
            reader.ReadByte(); // octave
        }
        else
        {
            keyAccidentals = reader.ReadInt32();
        }

        ReadChannels(reader);

        if (IsVersion5)
        {
            reader.Skip(38); // 19 musical directions × 2 bytes
            reader.Skip(4);  // master reverb / unknown
        }

        var numMeasures = reader.ReadInt32();
        var numTracks = reader.ReadInt32();

        ReadMeasureHeaders(reader, numMeasures);
        ReadTracks(reader, numTracks);

        var result = new ParseResult
        {
            Tracks = _tracks.Select(t => new GpTrackInfo { Id = t.Id, Name = t.Name }).ToList(),
            KeyAccidentals = keyAccidentals,
            KeyIsMinor = keyIsMinor,
            Title = _title,
            Artist = _artist,
            Album = _album,
            Tabber = _tabber
        };

        BuildUnfoldedBarSequence();

        // The chart's bars are the UNFOLDED ones: a repeated section really is
        // played N times, and GpBeatMapBuilder + every GpTempoPoint.BarIndex
        // index into this list. GpMetadataParser does the same on the gpif side.
        foreach (var entry in _unfolded)
        {
            var mh = _measureHeaders[entry.OriginalIndex];
            result.Bars.Add(new GpBarInfo
            {
                BeatsInMeasure = mh.Numerator,
                NoteDenominator = mh.Denominator,
                TripletFeel = mh.TripletFeel
            });
        }

        result.TempoPoints.Add(new GpTempoPoint { BarIndex = 0, BarPosition = 0, Bpm = initialBpm });

        if (headersOnly) return result;

        // Initialize per-track chart levels.
        foreach (var track in _tracks)
            _levelByTrack[track.Id] = new ChartLevel { DifficultyIndex = 0 };

        ReadBeats(reader, result);

        return result;
    }

    // ---- VERSION ---------------------------------------------------------

    private void ReadVersion(GpLegacyBinaryReader reader)
    {
        var versionString = reader.ReadStringByte(31);
        var idx = versionString.LastIndexOf('v');
        if (idx < 0 || !GpVersion.TryParse(versionString.Substring(idx + 1), out _version))
            throw new InvalidDataException("Unsupported Guitar Pro version: " + versionString);
        _majorVersion = _version.Major;

        if (_majorVersion < 3)
            throw new NotSupportedException("Guitar Pro versions prior to 3.0 are not supported.");
        if (_majorVersion > 5)
            throw new NotSupportedException("Use the GPX/GP importer for Guitar Pro 6+ files.");
    }

    // ---- SONG HEADER -----------------------------------------------------

    private void ReadSongAttributes(GpLegacyBinaryReader reader)
    {
        // Title/artist/album/tab are read here, not DISCARDED, or every
        // legacy import would leave the project's Song Metadata at its defaults.
        // Trimmed because a blank string is the model's "not carried" sentinel
        // and GP pads unfilled fields with nothing but whitespace at worst.
        _title = reader.ReadStringInt().Trim();
        reader.ReadStringInt(); // subtitle
        _artist = reader.ReadStringInt().Trim();
        _album = reader.ReadStringInt().Trim();
        if (_majorVersion >= 5) reader.ReadStringInt(); // words (lyricist)
        reader.ReadStringInt(); // music
        reader.ReadStringInt(); // copyright
        _tabber = reader.ReadStringInt().Trim(); // tab ("tabbed by")
        reader.ReadStringInt(); // instructions

        var notices = reader.ReadInt32();
        for (var i = 0; i < notices; i++) reader.ReadStringInt();
    }

    private void ReadLyricsAndPreSongData(GpLegacyBinaryReader reader)
    {
        // GP3/GP4 store swing ONCE for the whole song as a boolean here (Log
        // Charter GP5FileReader.readGlobalTripletFeel: version < 500 reads a
        // boolean and maps true -> EIGHTH, false -> NONE). GP5 dropped it in
        // favour of the per-measure-header byte read in ReadMeasureHeaders and
        // leaves this field out entirely.
        if (_majorVersion <= 4)
            _globalTripletFeel = reader.ReadByte() != 0 ? TripletFeelEighth : "";

        if (_majorVersion >= 4)
        {
            reader.ReadInt32(); // associated lyric track
            // 5 lyric lines, each: int(start bar) + word-pascal string (int length + chars)
            for (var i = 0; i < 5; i++)
            {
                reader.ReadInt32(); // start bar
                var lineLen = reader.ReadInt32();
                if (lineLen > 0) reader.Skip(lineLen);
            }
        }

        if (_majorVersion >= 5)
        {
            // Page setup block: 49 bytes for v5.10+, 30 bytes for v5.0
            reader.Skip(IsAfter50 ? 49 : 30);
            // 11 page setup fields (header/footer/tempo etc.), each: skip(4) + byte-pascal string
            for (var i = 0; i < 11; i++) reader.ReadStringInt();
        }
    }

    // ---- CHANNELS --------------------------------------------------------

    private void ReadChannels(GpLegacyBinaryReader reader)
    {
        for (var i = 0; i < 64; i++)
        {
            // Read each channel descriptor to advance the stream past the channels
            // block; the actual MIDI mixer info is unused by the chart importer.
            reader.ReadInt32(); // patch
            reader.ReadByte();  // volume
            reader.ReadByte();  // pan
            reader.ReadByte();  // chorus
            reader.ReadByte();  // reverb
            reader.ReadByte();  // phaser
            reader.ReadByte();  // tremolo
            reader.Skip(2);     // padding
        }
    }

    // ---- MEASURE HEADERS -------------------------------------------------

    private void ReadMeasureHeaders(GpLegacyBinaryReader reader, int count)
    {
        var num = 4;
        var den = 4;

        for (var i = 0; i < count; i++)
        {
            // GP5 has a 1-byte sentinel between consecutive measure headers.
            if (_majorVersion >= 5 && i > 0) reader.Skip(1);

            var tripletFeel = _globalTripletFeel;
            var bitmask = reader.ReadByte();

            if ((bitmask & 0x01) != 0) num = reader.ReadByte();          // new numerator
            if ((bitmask & 0x02) != 0) den = reader.ReadByte();          // new denominator

            // 0x04 = repeat start (no extra bytes). 0x08 = end-of-repeat, with
            // a count byte: GP5 states the TOTAL passes, GP3/4 the number of
            // EXTRA ones (alphaTab's Gp3To5Importer makes exactly this
            // distinction). All three of these are read, not thrown away.
            var repeatStart = (bitmask & 0x04) != 0;
            var repeatEnd = (bitmask & 0x08) != 0;
            var repeatCount = 0;
            if (repeatEnd) repeatCount = reader.ReadByte() + (_majorVersion >= 5 ? 0 : 1);
            var alternateEndings = 0;

            if (_majorVersion < 5)
            {
                // A raw bitmask over passes, bit 0 = pass 1 — the same shape
                // gpif spells as a space-separated <AlternateEndings> list.
                if ((bitmask & 0x10) != 0) alternateEndings = reader.ReadByte() & 0xFF;
                if ((bitmask & 0x20) != 0)
                {
                    reader.ReadStringInt();
                    reader.ReadInt32();                                  // marker color
                }
                if ((bitmask & 0x40) != 0)
                {
                    reader.ReadByte();                                   // key
                    reader.ReadByte();                                   // mode
                }
            }
            else
            {
                if ((bitmask & 0x20) != 0)
                {
                    reader.ReadStringInt();
                    reader.ReadInt32();                                  // marker color
                }
                if ((bitmask & 0x10) != 0)
                    alternateEndings = reader.ReadByte() & 0xFF;         // volta (alt ending bitmask)
                if ((bitmask & 0x40) != 0)
                {
                    reader.ReadByte();                                   // key
                    reader.ReadByte();                                   // mode
                }
                // 0x80 = double bar (no extra bytes)

                if ((bitmask & 0x03) != 0) reader.Skip(4);               // unknown 4 bytes when timesig changed
                if ((bitmask & 0x10) == 0) reader.Skip(1);               // padding when no volta
                // Triple feel, per bar in GP5 (Log Charter GP5FileReader
                // readMasterBar: 1 -> EIGHTH, 2 -> SIXTEENTH, anything else ->
                // NONE). Swing is placement metadata only — nothing here shifts
                // a note; the value rides along on GpBarInfo.
                tripletFeel = reader.ReadByte() switch
                {
                    1 => TripletFeelEighth,
                    2 => TripletFeelSixteenth,
                    _ => ""
                };
            }

            if (num <= 0) num = 4;
            if (den <= 0) den = 4;
            _measureHeaders.Add(new MeasureHeader
            {
                Numerator = num,
                Denominator = den,
                TripletFeel = tripletFeel,
                RepeatStart = repeatStart,
                RepeatEnd = repeatEnd,
                RepeatCount = repeatCount,
                AlternateEndingsMask = alternateEndings
            });
        }
    }

    /// <summary>
    ///     Runs the SHARED repeat unfold over the measure headers and caches the
    ///     three things the bar walk needs: the playback order, each unfolded
    ///     bar's chart start, and authored-index → unfolded-slots.
    /// </summary>
    private void BuildUnfoldedBarSequence()
    {
        var markers = new List<GpMasterBarSequence.BarRepeat>(_measureHeaders.Count);
        foreach (var mh in _measureHeaders)
        {
            markers.Add(new GpMasterBarSequence.BarRepeat(
                mh.RepeatStart, mh.RepeatEnd, mh.RepeatCount, mh.AlternateEndingsMask));
        }

        _unfolded = GpMasterBarSequence.Unfold(markers);

        _unfoldedBarStarts = new FractionalPosition[_unfolded.Count];
        var pos = FractionalPosition.Zero;
        for (var u = 0; u < _unfolded.Count; u++)
        {
            _unfoldedBarStarts[u] = pos;
            var mh = _measureHeaders[_unfolded[u].OriginalIndex];
            pos += new FractionalPosition(0, new Fraction(mh.Numerator * 4, mh.Denominator));
        }

        _unfoldedByOriginal = new List<int>[_measureHeaders.Count];
        for (var m = 0; m < _unfoldedByOriginal.Length; m++) _unfoldedByOriginal[m] = [];
        for (var u = 0; u < _unfolded.Count; u++) _unfoldedByOriginal[_unfolded[u].OriginalIndex].Add(u);
    }

    // ---- TRACKS ----------------------------------------------------------

    private void ReadTracks(GpLegacyBinaryReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var bitmask = reader.ReadByte();

            // GP5: 1-byte sentinel before the name on the very first track,
            // and (for v5.0 only) before every subsequent track too.
            if (_majorVersion >= 5 && (i == 0 || !IsAfter50)) reader.Skip(1);

            // Normalized at the one read (whitespace runs collapse, ends trim)
            // so GetTracks and BuildArrangement cannot disagree — real GP5
            // files carry doubled spaces in the track-name bytes.
            var name = GpTrackNames.Normalize(reader.ReadStringByte(41));
            var stringCount = reader.ReadInt32();
            if (stringCount is < 1 or > 7) stringCount = 6;

            var tuning = new int[7];
            for (var s = 0; s < 7; s++) tuning[s] = reader.ReadInt32();

            reader.ReadInt32(); // midi port
            reader.ReadInt32(); // midi channel
            reader.ReadInt32(); // midi effect channel
            var frets = reader.ReadInt32();
            var capo = reader.ReadInt32();
            reader.ReadInt32(); // color

            if (_majorVersion == 5)
            {
                reader.Skip(IsAfter50 ? 49 : 44);
                if (IsAfter50)
                {
                    reader.ReadStringInt(); // effect 1
                    reader.ReadStringInt(); // effect 2
                }
            }

            _tracks.Add(new TrackHeader
            {
                Id = i + 1,
                // `name` is already GpTrackNames-normalized at the read above —
                // the one place; a second normalizer here was merge duplication.
                Name = name.Length > 0 ? name : $"Track {i + 1}",
                StringCount = stringCount,
                Tuning = tuning,
                // Sanitized HERE, at the one read, so the note path and
                // BuildArrangement cannot disagree: the field is a raw
                // ReadInt32, a negative one would make the fret conversion
                // SUBTRACT, and one past the neck overflowed
                // GpCapoFrets.ToAbsolute's sum into a large negative fret.
                Capo = GpCapoFrets.Sanitize(capo),
                Frets = frets,
                IsDrums = (bitmask & 0x01) != 0
            });
        }

        // Trailing padding after the track block: 2 bytes for v5.0, 1 byte for v5.10.
        if (_majorVersion >= 5) reader.Skip(IsAfter50 ? 1 : 2);
    }

    // ---- BEATS / NOTES ---------------------------------------------------

    private void ReadBeats(GpLegacyBinaryReader reader, ParseResult result)
    {
        var voicesPerTrack = _majorVersion >= 5 ? 2 : 1;

        for (var measureIdx = 0; measureIdx < _measureHeaders.Count; measureIdx++)
        {
            var mh = _measureHeaders[measureIdx];
            var measureLengthQuarters = new Fraction(mh.Numerator * 4, mh.Denominator);
            // The BYTES are one block per AUTHORED measure; the CHART follows
            // the unfolded playback order. Read the block once at the measure's
            // first unfolded start, then replay what it produced at the later
            // ones. (A bar with no slot at all is an alternate ending no pass
            // reaches: its bytes still have to be consumed, but nothing of it
            // is emitted.)
            var slots = _unfoldedByOriginal[measureIdx];
            var measureStart = slots.Count > 0 ? _unfoldedBarStarts[slots[0]] : FractionalPosition.Zero;

            for (var trackIdx = 0; trackIdx < _tracks.Count; trackIdx++)
            {
                var track = _tracks[trackIdx];
                var level = _levelByTrack[track.Id];

                for (var voice = 0; voice < voicesPerTrack; voice++)
                {
                    // GP5 lane 0 is the playable chart; lane 1 (GP5's second
                    // voice) becomes a NOTATION-ONLY sidecar on the arrangement —
                    // the same slot-1 handling the gpif importer gives voices 2-4.
                    var sidecar = voice == 0 ? null : GetOrCreateSidecar(track.Id, voice);
                    var beat = new GpLegacyBeatContext(track, level, voice, measureIdx, measureStart,
                        measureLengthQuarters, measureStart, sidecar?.Notes ?? level.Notes,
                        sidecar?.Chords ?? level.Chords);

                    var notesFrom = beat.Notes.Count;
                    var chordsFrom = beat.Chords.Count;

                    var beatsInVoice = reader.ReadInt32();
                    for (var b = 0; b < beatsInVoice; b++)
                    {
                        var sustain = ReadBeat(reader, beat, result);
                        beat = beat with { Position = beat.Position + new FractionalPosition(0, sustain) };
                    }

                    ReplayMeasureOnLaterPasses(slots, measureStart, beat.Notes, beat.Chords, notesFrom, chordsFrom);
                }

                // GP5 sentinel byte after each (measure, track) block, except the very last one in the file.
                var isLast = measureIdx == _measureHeaders.Count - 1 && trackIdx == _tracks.Count - 1;
                if (_majorVersion >= 5 && !isLast) reader.ReadByte();
            }
        }
    }

    /// <summary>
    ///     Copies the sounds a measure just produced to every LATER unfolded
    ///     start of the same measure — the repeat passes. The byte stream only
    ///     ever carries the measure once, so this is what makes
    ///     <c>|: 8 bars :|</c> sixteen bars of chart instead of eight.
    ///     <para>
    ///         Positions shift by the whole-bar delta; sustains and every other
    ///         span are relative and ride along untouched. Ties and let-ring
    ///         carries still attach to the FIRST pass's sound (the parse state
    ///         holds it), which is the same approximation the gpif path makes
    ///         when a tie crosses a repeat boundary.
    ///     </para>
    /// </summary>
    private void ReplayMeasureOnLaterPasses(List<int> slots, FractionalPosition measureStart,
        IList<GuitarNote> notesOut, IList<DotChord> chordsOut, int notesFrom, int chordsFrom)
    {
        if (slots.Count < 2) return;

        var notesTo = notesOut.Count;
        var chordsTo = chordsOut.Count;
        if (notesTo == notesFrom && chordsTo == chordsFrom) return;

        for (var pass = 1; pass < slots.Count; pass++)
        {
            var delta = _unfoldedBarStarts[slots[pass]] - measureStart;

            for (var n = notesFrom; n < notesTo; n++)
            {
                var copy = notesOut[n].Clone();
                copy.Position += delta;
                notesOut.Add(copy);
            }

            for (var c = chordsFrom; c < chordsTo; c++)
            {
                var copy = chordsOut[c].Clone();
                copy.Position += delta;
                chordsOut.Add(copy);
            }
        }
    }

    private Fraction ReadBeat(GpLegacyBinaryReader reader, in GpLegacyBeatContext beat, ParseResult result)
    {
        var bitmask = reader.ReadByte();
        var isRest = (bitmask & 0x40) != 0;
        if (isRest) reader.ReadByte(); // rest type

        var sustain = ReadBeatDuration(reader, bitmask);
        var chordDiagramName = (bitmask & 0x02) != 0 ? ReadChordDiagram(reader, beat.Track.StringCount) : null;
        var beatText = (bitmask & 0x04) != 0 ? reader.ReadStringInt() : null;
        var effects = (bitmask & 0x08) != 0 ? ReadBeatEffects(reader) : default;
        if ((bitmask & 0x10) != 0) ReadMixTableTempo(reader, beat, result);

        var notesOnBeat = ReadBeatNotes(reader, beat.Track, keep: !isRest);

        if (_majorVersion >= 5)
        {
            var transposeBitmask = reader.ReadInt16();
            if ((transposeBitmask & 0x800) != 0) reader.ReadByte();
        }

        // EOF beat-text import (playable voice only): "T"/"S"/"P" beat text →
        // tap / slap / pop on the beat's note(s).
        GpMarkerImport.BeatTextTechnique? technique = beat.IsPlayable && _settings.BeatTextTechniques &&
                                                      GpMarkerImport.TryParseTechnique(beatText, out var beatTech)
            ? beatTech
            : null;

        if (notesOnBeat.Count == 0) return sustain;

        foreach (var entry in notesOnBeat) PlaceBeatNote(beat, entry, sustain, effects);

        // Promote per-note let-ring (any string in the beat) to a beat-level
        // marker; downstream HandShape grouping looks at beat positions. Only
        // the playable voice captures let-ring — sidecar (notation-only)
        // voices get no hand shapes, matching the gpif sidecar passes.
        var beatLetRing = notesOnBeat.Any(e => _state.PendingLetRingNotes.Contains(e.Note));
        foreach (var e in notesOnBeat) _state.PendingLetRingNotes.Remove(e.Note);
        if (beatLetRing && beat.IsPlayable) _state.GetLetRingSet(beat.Level).Add(beat.Position);

        if (notesOnBeat.Count == 1)
            EmitBeatNote(beat, notesOnBeat[0].Note, sustain, technique);
        else
            EmitBeatChord(beat, notesOnBeat, sustain, technique, chordDiagramName);

        return sustain;
    }

    /// <summary>Reads the beat's duration byte (and its tuplet, when flagged) and
    /// folds it into one quarter-note-unit fraction.</summary>
    private static Fraction ReadBeatDuration(GpLegacyBinaryReader reader, byte bitmask)
    {
        var duration = reader.ReadSByte();
        var isTuplet = (bitmask & 0x20) != 0;
        var isDotted = (bitmask & 0x01) != 0;

        var baseValue = duration switch
        {
            -2 => new Fraction(4, 1),
            -1 => new Fraction(2, 1),
            0 => new Fraction(1, 1),
            1 => new Fraction(1, 2),
            2 => new Fraction(1, 4),
            3 => new Fraction(1, 8),
            4 => new Fraction(1, 16),
            _ => new Fraction(1, 1)
        };

        var tupletNum = 0;
        var tupletDen = 0;
        if (isTuplet)
        {
            // Raw int32 straight off the wire. The gpif reader has always bounded
            // the same ratio (GpFormatBounds.MaxTupletValue) because it is the one
            // unbounded factor in a rhythm's duration; this path never did, so a
            // hostile `.gp4`/`.gp5` could size a bar arbitrarily — and at or above
            // 2^30 it also hung PreviousPowerOfTwo outright.
            var nTuplet = Math.Min(reader.ReadInt32(), GpFormatBounds.MaxTupletValue);
            if (nTuplet > 1)
            {
                tupletNum = nTuplet;
                tupletDen = PreviousPowerOfTwo(nTuplet);
            }
        }

        // Fold dots + tuplet into ONE quarter-note-unit fraction — the same
        // value the gpif Rhythms parser yields. It is BOTH the amount the chart
        // cursor advances AND the authored notation duration hint captured below
        // (before GpPostProcessor.AdjustSustains trims short display sustains).
        return GpRhythmFraction.Fold(baseValue, isDotted ? 1 : 0, tupletNum, tupletDen);
    }

    /// <summary>Reads the beat's mix-table change and records its tempo, if it sets one.</summary>
    private void ReadMixTableTempo(GpLegacyBinaryReader reader, in GpLegacyBeatContext beat, ParseResult result)
    {
        var newTempo = ReadMixTableChange(reader);
        if (newTempo <= 0) return;

        var barLenQ = (double)beat.MeasureLength.Numerator / beat.MeasureLength.Denominator;
        var posQ = (beat.Position - beat.MeasureStart).DoubleValue;
        var pos = barLenQ > 0 ? posQ / barLenQ : 0.0;
        // BarIndex is an index into the UNFOLDED bar list, so a tempo
        // change inside a repeated section has to fire on every pass —
        // the same mapping GpMetadataParser does for gpif automations.
        foreach (var slot in _unfoldedByOriginal[beat.MeasureIndex])
        {
            result.TempoPoints.Add(new GpTempoPoint
            {
                BarIndex = slot,
                BarPosition = Math.Clamp(pos, 0, 0.999),
                Bpm = newTempo
            });
        }
    }

    /// <summary>Reads every note the beat's string mask names; keeps the ones on a
    /// real string of the track unless the beat is a rest.</summary>
    private List<(int StringIndex, GuitarNote Note)> ReadBeatNotes(GpLegacyBinaryReader reader, TrackHeader track,
        bool keep)
    {
        var stringsMask = reader.ReadByte();
        var notesOnBeat = new List<(int StringIndex, GuitarNote Note)>();

        // GP encodes strings highest-pitch first. Bit 6 = string 1 (high e),
        // bit (7 - track.StringCount) = lowest played string.
        for (var stringNo = 1; stringNo <= 7; stringNo++)
        {
            var bit = 1 << (7 - stringNo);
            if ((stringsMask & bit) == 0) continue;

            // GP frets are RELATIVE to the track capo and ours are absolute, so
            // the track's capo has to reach the note read itself (see
            // GpCapoFrets). Every string is read — even ones past the track's
            // string count, which only advance the stream — so the conversion
            // rides along with the read rather than being applied afterwards.
            var note = ReadNote(reader, track.StringCount - stringNo, track.Capo);
            if (stringNo <= track.StringCount && keep)
                notesOnBeat.Add((track.StringCount - stringNo, note));
        }

        return notesOnBeat;
    }

    /// <summary>Positions one note of the beat and folds the beat-level effects onto it.</summary>
    private void PlaceBeatNote(in GpLegacyBeatContext beat, (int StringIndex, GuitarNote Note) entry,
        Fraction sustain, (BassPickingType BassPicking, bool isTap, bool hasVibrato, bool hasHarmonic) effects)
    {
        // Position notes at the beat and apply per-note attributes that
        // depend on knowing the absolute position (bends).
        var note = entry.Note;
        note.Position = beat.Position;
        note.StringIndex = entry.StringIndex;
        note.Sustain = new FractionalPosition(0, sustain);
        // GP's beat-level tap (string-effect type 1) is RIGHT-hand
        // tapping — the same technique gpif spells as the note property
        // `Tapped`, which GpNoteParser maps to HopoType.Tap. Importing it
        // as a hammer-on cost three things: the SNG never set
        // RsNoteMask.Tap (SngFromDomainMapper keys on HopoType.Tap), the
        // FHP generator's whole tap-run rule (FretAnchorGenerationService:
        // `sounds.Any(s => s.Hopo == HopoType.Tap)`) was dead for every
        // .gp3/4/5 tapping passage, and AdjustSustains re-derived every
        // DESCENDING tapped note into a PullOff — the exact defect the
        // gpif path's LeftHandTappedNotes exemption exists to prevent.
        // Tap needs no such tapOriginated exemption: only a HammerOn is
        // ever re-derived, and gpif passes that set solely because it
        // normalises its LEFT-hand taps to hammer-ons on purpose.
        if (effects.isTap && note.Hopo == HopoType.None)
            note.Hopo = HopoType.Tap;
        // Beat-level vibrato/harmonic status bits fold onto every note
        // of the beat (EOF gp_import.c:3268-3286) — chord vibrato is
        // usually authored at the BEAT in GP binary, not per note.
        if (effects.hasVibrato) note.Vibrato = true;
        if (effects.hasHarmonic && note.Harmonic == HarmonicType.None)
            note.Harmonic = HarmonicType.Normal;
        // Beat-level slap/pop (string-effect type 2/3) folds the same way.
        if (effects.BassPicking != BassPickingType.None && note.BassPicking == BassPickingType.None)
            note.BassPicking = effects.BassPicking;
        // The note's position is finally known — move its authored
        // left-hand finger into the (string, position) table the
        // chord-template builder reads. Playable voice only; the
        // notation-only sidecars never build templates.
        if (beat.IsPlayable && _pendingFingers.Remove(note, out var authoredFinger))
            AuthoredFingersFor(beat.Track.Id).Set(entry.StringIndex, beat.Position, authoredFinger);
    }

    private void EmitBeatNote(in GpLegacyBeatContext beat, GuitarNote note, Fraction sustain,
        GpMarkerImport.BeatTextTechnique? technique)
    {
        note.NotationDuration = sustain;
        if (technique is { } tech) GpMarkerImport.ApplyTechnique(note, tech);
        MaterializeBends(note, note, beat.Position, sustain);

        // A trill on the PLAYABLE voice expands into the alternating
        // HO/PO run RS needs, through the same helper the gpif path uses.
        // A notation-only sidecar voice keeps the single note with the
        // vibrato glyph instead — again exactly like the gpif path.
        var hasTrill = _pendingTrills.Remove(note, out var trillUpperFret);
        if (hasTrill && beat.IsPlayable)
        {
            var barEnd = beat.MeasureStart + new FractionalPosition(0, beat.MeasureLength);
            GpArrangementParser.ExpandTrill(beat.Notes, note, trillUpperFret, barEnd);
        }
        else
        {
            if (hasTrill) note.Vibrato = true;
            beat.Notes.Add(note);
        }

        _state.PendingBendOffsets.Remove(note);
        RetargetTieAndTrack(beat.Track.Id, beat.Voice, note.StringIndex, note, note);
    }

    private void EmitBeatChord(in GpLegacyBeatContext beat, List<(int StringIndex, GuitarNote Note)> notesOnBeat,
        Fraction sustain, GpMarkerImport.BeatTextTechnique? technique, string? chordDiagramName)
    {
        var chord = new DotChord { Position = beat.Position, NotationDuration = sustain };
        foreach (var entry in notesOnBeat)
        {
            var cn = new ChordNote
            {
                StringIndex = entry.StringIndex,
                Fret = entry.Note.Fret,
                Sustain = entry.Note.Sustain,
                Hopo = entry.Note.Hopo,
                Vibrato = entry.Note.Vibrato,
                Tremolo = entry.Note.Tremolo,
                Ignore = entry.Note.Ignore,
                Mute = entry.Note.Mute,
                Harmonic = entry.Note.Harmonic,
                Accent = entry.Note.Accent,
                BassPicking = entry.Note.BassPicking,
                SlideTo = entry.Note.SlideTo,
                UnpitchedSlide = entry.Note.UnpitchedSlide
            };
            MaterializeBends(entry.Note, cn, beat.Position, sustain);
            chord.ChordNotes.Add(cn);
            _state.PendingBendOffsets.Remove(entry.Note);
            RetargetTieAndTrack(beat.Track.Id, beat.Voice, entry.StringIndex, entry.Note, cn);
        }

        // An accent on any string accents the strum (chord-level accent
        // is what RS XML / rscp serialize).
        chord.Accent = chord.ChordNotes.Any(cn => cn.Accent);

        if (technique is { } tech)
        {
            foreach (var cn in chord.ChordNotes)
                GpMarkerImport.ApplyTechnique(cn, tech);
        }

        // Grace ornaments must be consumed BEFORE the strum joins the
        // output list — the placement scan below looks for the last sound
        // already emitted on each string and the target itself is not one.
        ApplyChordGraces(chord, notesOnBeat, beat.Notes, beat.Chords, _onBeatGraces);
        beat.Chords.Add(chord);

        // The beat's chord diagram carries the arranger's own name for
        // this strum. The PLAYABLE voice only — the notation-only
        // sidecars never build templates — and per track, because the
        // key is a position and two tracks strum together constantly.
        if (beat.IsPlayable && chordDiagramName != null)
            AuthoredChordNamesFor(beat.Track.Id).Set(beat.Position, chordDiagramName);
    }

    /// <summary>
    ///     Consumes the grace ornaments of a CHORD beat. Guitar Pro's binary
    ///     formats hang a grace on the note it decorates (a note-effect block per
    ///     string), so a graced strum arrives as one grace per chord note — and
    ///     until now every one of them was dropped, because the chord builder
    ///     copied fret/technique fields but not the ornament.
    ///     <para>
    ///     Two outcomes, exactly as <c>GpArrangementParser.ApplyPendingGrace</c>
    ///     gives the GPIF path for the same score:
    ///     <list type="bullet">
    ///     <item>a "sl." grace (transition byte 1) that travels to a DIFFERENT
    ///     fretted position materializes a real origin CHORD that slides into the
    ///     strum — what EOF and Log Charter both emit;</item>
    ///     <item>anything else stays the notation-only <c>ChordNote.GraceFret</c>
    ///     hint on its own string.</item>
    ///     </list>
    ///     Only the graced strings take part in the origin chord. Placement is the
    ///     shared <see cref="GpPostProcessor.PlaceGrace"/>, given the ornament's
    ///     AUTHORED flavour: GP5's grace flag bit <c>0x02</c> means the ornament
    ///     sits ON the beat and pushes the strum, a cleared bit (and every GP3/GP4
    ///     grace, which carry no such bit) means BEFORE the beat. The gpif path
    ///     carries one flavour per grace BEAT
    ///     (<c>&lt;GraceNotes&gt;BeforeBeat|OnBeat&lt;/GraceNotes&gt;</c>) while the
    ///     binary format hangs one flag per graced NOTE, so a strum is treated as
    ///     on-beat when ANY of its sliding ornaments is — the two vocabularies then
    ///     agree for every score Guitar Pro can actually author, where a beat's
    ///     grace notes share a flavour. <see cref="GpPostProcessor.PlaceGrace"/>
    ///     still falls back to on-beat by itself when a before-beat ornament has no
    ///     room; if no room can be made at all the ornament degrades to a hint
    ///     instead of being lost.
    ///     </para>
    /// </summary>
    /// <param name="chord">The strum the ornaments decorate. Its position and
    /// sustains are shifted when an ornament takes time from it.</param>
    /// <param name="notesOnBeat">The parsed notes of this beat, which is where the
    /// binary format's per-string grace hints live.</param>
    /// <param name="notesOut">Notes emitted so far for this voice — scanned for
    /// the last sound on each graced string.</param>
    /// <param name="chordsOut">Chords emitted so far for this voice; the origin
    /// chord is appended here. Must NOT yet contain <paramref name="chord"/>.</param>
    /// <param name="onBeatGraces">The notes whose grace ornament was authored
    /// ON-beat (GP5 grace flag bit <c>0x02</c>), by reference identity.</param>
    private static void ApplyChordGraces(DotChord chord,
        List<(int StringIndex, GuitarNote Note)> notesOnBeat,
        IList<GuitarNote> notesOut, IList<DotChord> chordsOut,
        IReadOnlySet<GuitarNote> onBeatGraces)
    {
        List<(int Fret, ChordNote Target)>? pairs = null;
        var beforeBeat = true;

        foreach (var entry in notesOnBeat)
        {
            if (entry.Note.GraceFret == null) continue;
            var graceFret = entry.Note.GraceFret.Value;

            ChordNote? target = null;
            foreach (var cn in chord.ChordNotes)
            {
                if (cn.StringIndex == entry.StringIndex)
                {
                    target = cn;
                    break;
                }
            }

            if (target == null) continue;

            // Same rule as the single-note path: only a "sl." grace that travels
            // to a different FRETTED position can become a real sliding note.
            if (entry.Note.GraceSlide && graceFret > 0 && graceFret != target.Fret)
            {
                (pairs ??= []).Add((graceFret, target));
                // One flavour per ornament in gpif, one per NOTE in the binary
                // format: any on-beat member makes the whole strum's ornament
                // on-beat, so the origin chord stays a single sound.
                if (onBeatGraces.Contains(entry.Note)) beforeBeat = false;
            }
            else
            {
                target.GraceFret = graceFret;
            }
        }

        if (pairs == null) return;

        var earliest = FractionalPosition.Zero;
        foreach (var pair in pairs)
        {
            var end = LastEndOnString(notesOut, chordsOut, pair.Target.StringIndex);
            if (end.CompareTo(earliest) > 0) earliest = end;
        }

        var place = GpPostProcessor.PlaceGrace(chord.Position, chord.GetMaxSustain(),
            GpPostProcessor.GraceLeadIn, beforeBeat, earliest);
        if (place.GraceLength.CompareTo(FractionalPosition.Zero) <= 0)
        {
            foreach (var pair in pairs) pair.Target.GraceFret = pair.Fret;
            return;
        }

        var graceChord = new DotChord
        {
            Position = place.GracePosition,
            NotationDuration = GpPostProcessor.GraceLeadIn
        };
        foreach (var pair in pairs)
        {
            graceChord.ChordNotes.Add(new ChordNote
            {
                StringIndex = pair.Target.StringIndex,
                Fret = pair.Fret,
                Sustain = place.GraceLength,
                SlideTo = pair.Target.Fret,
                LinkNext = true
            });
        }

        chordsOut.Add(graceChord);
        chord.Position += place.TargetShift;
        foreach (var cn in chord.ChordNotes)
            cn.Sustain = GpPostProcessor.ShortenSustain(cn.Sustain, place.TargetShift);
    }

    /// <summary>
    ///     The end of the last sound already emitted on a string — the point a
    ///     BEFORE-beat ornament may not reach back past. Mirrors
    ///     <c>GpArrangementParser.LastEndOnString</c>.
    /// </summary>
    /// <param name="notesOut">Single notes emitted so far for this voice.</param>
    /// <param name="chordsOut">Chords emitted so far for this voice.</param>
    /// <param name="stringIndex">The string to scan.</param>
    private static FractionalPosition LastEndOnString(IList<GuitarNote> notesOut, IList<DotChord> chordsOut,
        int stringIndex)
    {
        var end = FractionalPosition.Zero;
        foreach (var note in notesOut)
        {
            if (note.StringIndex == stringIndex && note.EndPosition.CompareTo(end) > 0)
                end = note.EndPosition;
        }

        foreach (var chord in chordsOut)
        {
            foreach (var cn in chord.ChordNotes)
            {
                if (cn.StringIndex == stringIndex)
                {
                    var chordEnd = chord.Position + cn.Sustain;
                    if (chordEnd.CompareTo(end) > 0) end = chordEnd;
                }
            }
        }

        return end;
    }

    /// <summary>
    ///     Beat-flush half of the tie handling: if <paramref name="parsedNote"/>
    ///     was a binary tie continuation, the PREVIOUS emitted sound on the same
    ///     (track, voice, string) gets <c>LinkNext</c> — the chart-model tie
    ///     origin — and the stored sound becomes the new "previous" either way.
    ///     <c>GpPostProcessor.MergeTiedNotes</c> then folds the chain into one
    ///     long note unless techniques differ per segment.
    /// </summary>
    private void RetargetTieAndTrack(int trackId, int voice, int stringIndex, GuitarNote parsedNote,
        IGuitarSound storedSound)
    {
        var key = (trackId, voice, stringIndex);
        if (_state.TiedContinuations.Remove(parsedNote) &&
            _state.LastSoundOnString.TryGetValue(key, out var previous))
        {
            previous.LinkNext = true;
        }

        _state.LastSoundOnString[key] = storedSound;
    }

    /// <summary>
    ///     Reads one note's binary block. The fret arrives capo-RELATIVE (Guitar
    ///     Pro's convention) and is stored ABSOLUTE via
    ///     <see cref="GpCapoFrets.ToAbsolute"/> — which also subsumes the old
    ///     <c>Math.Max(0, fret)</c> dead-note clamp. The conversion happens BEFORE
    ///     <see cref="ReadNoteEffects"/> runs, because the unpitched slide-out
    ///     targets it derives (<c>GpLegacyNoteEffectsHelpers.ApplySlide</c>,
    ///     <c>fret ± 5</c>) must be computed off the absolute fret.
    /// </summary>
    /// <param name="reader">The binary reader, positioned at the note block.</param>
    /// <param name="stringIndex">The chart string index (low-pitch-first) the note
    /// belongs to.</param>
    /// <param name="capo">The owning TRACK's capo fret (already clamped
    /// non-negative). <c>0</c> ⇒ no capo ⇒ frets are stored verbatim.</param>
    private GuitarNote ReadNote(GpLegacyBinaryReader reader, int stringIndex, int capo)
    {
        var bitmask = reader.ReadByte();
        // Flag-only header bits: 0x40 = accent (all versions); GP5 adds 0x02 =
        // heavy accent. EOF maps both to ACCENT (gp_import.c:4029-4043).
        var isAccent = (bitmask & 0x40) != 0 || (_majorVersion >= 5 && (bitmask & 0x02) != 0);
        var isTie = false;
        var isDead = false;
        if ((bitmask & 0x20) != 0)
        {
            var noteType = reader.ReadByte();
            if (noteType == 2) isTie = true;
            else if (noteType == 3) isDead = true;
        }

        if (_majorVersion < 5 && (bitmask & 0x01) != 0) reader.Skip(2); // time-independent duration
        if ((bitmask & 0x10) != 0) reader.ReadByte(); // dynamic

        var fret = 0;
        if ((bitmask & 0x20) != 0) fret = reader.ReadSByte();

        int? authoredFinger = null;
        if ((bitmask & 0x80) != 0)
        {
            // AUTHORED fingering: left hand, then right hand (-1/255 = none).
            // A bare Skip(2) would import every chord template
            // with a GUESSED fingering while the file says what the hand did.
            // The right hand has no model home, so it is read and dropped.
            authoredFinger = GpAuthoredFingering.MapBinaryFinger(reader.ReadSByte());
            reader.ReadSByte();
        }

        if (_majorVersion >= 5 && (bitmask & 0x01) != 0) reader.Skip(8); // time-independent duration v5
        if (_majorVersion >= 5) reader.ReadByte(); // unknown padding

        var note = new GuitarNote
        {
            Fret = GpCapoFrets.ToAbsolute(fret, capo),
            StringIndex = stringIndex,
            Accent = isAccent,
            Mute = isDead ? MuteType.Full : MuteType.None
        };
        // Binary note-type 2 ties this note to the PREVIOUS one; the flag is
        // retargeted onto the previous sound's LinkNext at beat flush (the
        // chart model marks ties on the origin, not the continuation).
        if (isTie) _state.TiedContinuations.Add(note);
        // The note's chart POSITION is only known at beat flush, so the authored
        // finger rides along by reference identity until then.
        if (authoredFinger is { } finger) _pendingFingers[note] = finger;

        if ((bitmask & 0x08) != 0) ReadNoteEffects(reader, note, capo);

        return note;
    }

    /// <summary>
    ///     Reads a note's effect block onto <paramref name="note"/>.
    /// </summary>
    /// <param name="reader">The binary reader, positioned at the effect block.</param>
    /// <param name="note">The note the effects apply to. Its
    /// <see cref="GuitarNote.Fret"/> must already be ABSOLUTE — the slide helper
    /// derives unpitched slide targets from it.</param>
    /// <param name="capo">The owning track's capo fret, needed because the grace
    /// ornament's fret is capo-RELATIVE like every other GP fret.</param>
    private void ReadNoteEffects(GpLegacyBinaryReader reader, GuitarNote note, int capo)
    {
        var fx1 = reader.ReadByte();
        var fx2 = _majorVersion >= 4 ? reader.ReadByte() : (byte)0;

        if ((fx1 & 0x01) != 0) ReadBendInto(reader, note);
        if ((fx1 & 0x02) != 0) note.Hopo = HopoType.HammerOn; // hammer-on/pull-off

        if ((fx1 & 0x08) != 0) _state.PendingLetRingNotes.Add(note);

        if (_majorVersion < 4 && (fx1 & 0x04) != 0)
        {
            // GP3 slide flag (no value follows in GP3).
            note.SlideTo = -1;
        }

        if ((fx1 & 0x10) != 0)
        {
            // Grace ornament: keep its fret as a notation hint on the SAME string.
            // Gameplay ignores GraceFret; only the sheet renders it (as a grace
            // glyph). The ornament's fret is capo-RELATIVE like every other GP
            // fret, so it converts the same way — which also keeps the dead-grace
            // clamp (a dead grace encodes a NEGATIVE fret and reads as 0/open,
            // matching how ReadNote clamps a dead main note).
            var graceFret = reader.ReadSByte(); // fret
            note.GraceFret = GpCapoFrets.ToAbsolute(graceFret, capo);
            reader.ReadByte(); // dynamic
            var graceTransition = reader.ReadByte(); // 0=none, 1=slide, 2=bend, 3=legato/hammer
            reader.ReadByte(); // duration
            if (_majorVersion >= 5)
            {
                // GP5 alone appends a grace FLAG byte (Log Charter
                // GP5FileReader.readGrace, the `version >= 500` branch): bit 0x01 =
                // dead grace, bit 0x02 = the ornament sits ON the beat — Charter
                // spells it `graceBefore = (flags & 0x02) == 0`. Discarding this
                // byte was why an on-beat .gp5 grace imported AHEAD of the beat
                // while the identical score saved as .gp imported ON it (the gpif
                // path reads <GraceNotes>BeforeBeat|OnBeat</GraceNotes>). GP3/GP4
                // have no such byte at all, so those versions keep Charter's
                // pre-500 default of BEFORE-beat, which is the absence of an entry
                // in this set.
                var graceFlags = reader.ReadByte();
                if ((graceFlags & 0x02) != 0) _onBeatGraces.Add(note);
            }

            // A slide grace ("sl.") becomes a real note that slides into the
            // target downstream (EOF gp_import gracetrans==1). Every version
            // stores the transition byte — Charter reads it outside its version
            // branch — so a GP3/GP4 "sl." grace slides too.
            if (graceTransition == 1) note.GraceSlide = true;
        }

        if (_majorVersion >= 4)
        {
            // NOTE-level fx2 0x04 is tremolo PICKING (the byte is the speed), NOT
            // the beat's tremolo BAR. The speed has no model home; the flag does.
            if ((fx2 & 0x04) != 0)
            {
                reader.ReadByte(); // picking speed (unused)
                note.Tremolo = true;
            }

            // The capo travels: the unpitched out-slide's `fret - 5` landing is a
            // SYNTHESIZED absolute fret and must floor at the capo, exactly as it
            // does on the gpif path (GpNoteParser.ApplySlideFlags).
            // A GP5 pick scrape (slide bits 0x40/0x80) is charted unmuted;
            // the palm-mute bit below must not mute it again.
            var pickScrape = (fx2 & 0x08) != 0
                && GpLegacyNoteEffectsHelpers.ApplySlide(reader.ReadSByte(), note, IsVersion5, capo);
            if ((fx2 & 0x10) != 0) GpLegacyNoteEffectsHelpers.ReadHarmonic(reader, note);
            if ((fx2 & 0x20) != 0)
            {
                // Trill: upper fret + period. The fret is capo-RELATIVE like every
                // other GP fret, so it is converted HERE (the capo rule: at the
                // parse point, before anything reads the value). The expansion
                // itself needs the note's final position/sustain and the bar end,
                // so it happens at beat flush — see _pendingTrills.
                var trillFret = GpCapoFrets.ToAbsolute(reader.ReadSByte(), capo);
                reader.ReadByte(); // period (unused: ExpandTrill uses GP's 32nd default)
                if (trillFret >= 0) _pendingTrills[note] = trillFret;
            }
            if ((fx2 & 0x40) != 0) note.Vibrato = true;
            // GP's authored STACCATO. Rocksmith has no staccato mark, so the
            // ACCENT is this codebase's staccato articulation — playback shortens
            // exactly the accented notes (JuceAudioPlaybackService's note-end
            // rule) — and the bit was parsed and thrown away, so an arranger's
            // staccato reached nothing at all. Ungated, like every other accent on
            // both readers: the note header's own accent bits above and gpif's
            // <Accent> element are both read with no setting in front of them, and
            // a rule applied on one tab path only is what the legacy-vs-gpif
            // parity rule forbids.
            if ((fx2 & 0x01) != 0) note.Accent = true;
            if ((fx2 & 0x02) != 0 && !pickScrape) note.Mute = MuteType.Palm;
        }
    }

    private void ReadBendInto(GpLegacyBinaryReader reader, GuitarNote note)
        => _state.AppendBendPoints(reader, note);

    private void MaterializeBends(GuitarNote sourceNote, IGuitarSound target,
        FractionalPosition absolutePos, Fraction sustainFraction)
    {
        if (_state.PendingBendOffsets.TryGetValue(sourceNote, out var points))
            GpLegacyNoteEffectsHelpers.MaterializeBends(target, points, absolutePos, sustainFraction);
    }

    // ---- BEAT EFFECTS ----------------------------------------------------

    /// <summary>
    ///     Reads a beat's effect block.
    ///     <para>
    ///     Note the two DIFFERENT meanings of bit <c>0x04</c> in the two fx2
    ///     bytes: at the BEAT it is the tremolo BAR (whammy, followed by a bend
    ///     chunk), at the NOTE it is tremolo PICKING (followed by a speed byte).
    ///     They must not be crossed — a whammy stamped onto every note of
    ///     the beat as <c>Tremolo</c> while the real picking flag is read and
    ///     dropped — would import a whammy dip as tremolo picking and a
    ///     tremolo-picked note clean. The gpif path is
    ///     right (<c>&lt;Tremolo&gt;</c> on the beat IS the picking speed), so the
    ///     same score would import differently as .gp and as .gp5.
    ///     </para>
    ///     The whammy's bend chunk is consumed (stream sync) but not
    ///     mapped: <c>IGuitarSound</c> has no bar-bend concept, and converting the
    ///     chunk into per-string <c>BendValues</c> is a separate decision.
    /// </summary>
    private (BassPickingType BassPicking, bool isTap, bool hasVibrato, bool hasHarmonic) ReadBeatEffects(
        GpLegacyBinaryReader reader)
    {
        var fx1 = reader.ReadByte();
        var fx2 = _majorVersion >= 4 ? reader.ReadByte() : (byte)0;

        // Flag-only status bits, identical in every format version (EOF
        // gp_import.c:3268-3286): 0x01 vibrato, 0x02 wide vibrato, 0x04
        // natural harmonic, 0x08 artificial harmonic.
        var hasVibrato = (fx1 & 0x03) != 0;
        var hasHarmonic = (fx1 & 0x0C) != 0;

        var isTap = false;
        var bassPicking = BassPickingType.None;
        if (_majorVersion < 4)
        {
            if ((fx1 & 0x20) != 0)
            {
                reader.ReadByte();
                reader.ReadInt32();
            }
        }
        else if ((fx1 & 0x20) != 0)
        {
            // GP4/5 string-effect type: 1 = tapping, 2 = slap, 3 = pop. Slap and
            // pop must not be dropped, on exactly the arrangements that
            // need them most (bass) — the "beat text S/P" feature is a
            // manual work-around for a flag that is right there in the file.
            var stringEffect = reader.ReadByte();
            if (stringEffect == 1) isTap = true;
            else if (stringEffect == 2) bassPicking = BassPickingType.Slap;
            else if (stringEffect == 3) bassPicking = BassPickingType.Pop;
        }

        // Tremolo BAR (whammy): consume its bend chunk so the stream stays in
        // sync, but do not map it onto the notes — see the summary above.
        if (_majorVersion >= 4 && (fx2 & 0x04) != 0) GpBendChunkReader.Skip(reader);

        if ((fx1 & 0x40) != 0) reader.Skip(2); // stroke effect (up/down speeds)
        if (_majorVersion >= 4 && (fx2 & 0x02) != 0) reader.ReadByte(); // pickstroke

        return (bassPicking, isTap, hasVibrato, hasHarmonic);
    }

    private int ReadMixTableChange(GpLegacyBinaryReader reader)
    {
        reader.ReadSByte(); // instrument (unused)
        if (_majorVersion >= 5)
            reader.Skip(16); // 4×int RSE-related + 4 unknown

        var volume = reader.ReadSByte();
        var pan = reader.ReadSByte();
        var chorus = reader.ReadSByte();
        var reverb = reader.ReadSByte();
        var phaser = reader.ReadSByte();
        var tremolo = reader.ReadSByte();

        if (_majorVersion >= 5) reader.ReadStringInt(); // tempo text

        var newTempo = reader.ReadInt32();

        if (volume != -1) reader.ReadByte();
        if (pan != -1) reader.ReadByte();
        if (chorus != -1) reader.ReadByte();
        if (reverb != -1) reader.ReadByte();
        if (phaser != -1) reader.ReadByte();
        if (tremolo != -1) reader.ReadByte();
        if (newTempo != -1)
        {
            reader.ReadByte();
            if (IsAfter50) reader.ReadByte(); // tempo text hidden flag
        }

        if (_majorVersion >= 4) reader.ReadByte(); // applied tracks bitmask
        if (_majorVersion >= 5) reader.ReadByte(); // padding
        if (IsAfter50)
        {
            reader.ReadStringInt(); // effect 2
            reader.ReadStringInt(); // effect 1
        }

        return newTempo;
    }

    /// <summary>
    ///     Consumes the beat's chord diagram block and returns the arranger's
    ///     chord NAME (null when the diagram carries none).
    ///     <para>
    ///     It is not a <c>SkipChordDiagram</c> — a skip would only keep the
    ///     beat stream in sync, so the name would be read and dropped on
    ///     the floor and <see cref="ChordHandShapeBuilder" /> would rename every
    ///     imported chord from <c>ChordNameSuggester</c>'s pitch-class guess. The
    ///     byte layout is the one whose 5-byte
    ///     name→base-fret gap, read as 4, desyncs the whole beat stream.
    ///     </para>
    /// </summary>
    private string? ReadChordDiagram(GpLegacyBinaryReader reader, int stringCount)
    {
        string? name;
        var format = reader.ReadByte();
        if (format == 0)
        {
            // Old (GP3-style) diagram: name + base fret, then — only when the
            // base fret is non-zero — one fret int per TRACK string (EOF's
            // gp_import.c reads string-count ints, not a fixed 6).
            name = reader.ReadStringInt();
            var firstFret = reader.ReadInt32();
            if (firstFret != 0)
                for (var s = 0; s < Math.Clamp(stringCount, 1, 7); s++) reader.ReadInt32();
        }
        else
        {
            // GP4/GP5 "new format": 106 bytes total following the format byte
            // (107 including it) — the layout EOF's gp_import.c:3071-3181 and
            // Charter's GP5FileReader.readVersion500Chord both consume. The
            // 5-byte gap after the name (2 unknown + tonality of the fifth/
            // ninth/eleventh) must be skipped as 5: skipping it as 4 desyncs the beat
            // stream by one byte at every chord diagram.
            reader.Skip(16);                  // sharp flag, root, type, extension, bass, tonality
            name = reader.ReadStringByte(21); // chord name (1 length byte + 20 chars)
            reader.Skip(5);                   // 2 unknown + fifth/ninth/eleventh tonality
            reader.ReadInt32();               // base fret
            reader.Skip(7 * 4);               // fret per string
            reader.Skip(16);                  // barre count + 5 barre frets/starts/stops
            reader.Skip(8);                   // 7 interval/omission bytes + 1 unknown
            reader.Skip(7);                   // fingering per string
            reader.ReadByte();                // display-fingering flag
        }

        return GpAuthoredChordNames.Normalize(name);
    }

    private static int PreviousPowerOfTwo(int n) => GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo(n);

    private NotationVoice GetOrCreateSidecar(int trackId, int voiceSlot)
    {
        if (!_sidecarByTrack.TryGetValue(trackId, out var sidecar))
            _sidecarByTrack[trackId] = sidecar = new NotationVoice { VoiceIndex = voiceSlot };
        return sidecar;
    }

    /// <summary>
    ///     Attaches the track's collected GP5 second-voice sidecar (if any) to the
    ///     arrangement. It gets the NOTATION-relevant passes only — tied-beat merge
    ///     (which sums the authored <see cref="GuitarNote.NotationDuration"/>) and
    ///     slide linking — but NEVER <see cref="GpPostProcessor.AdjustSustains"/>:
    ///     the sidecar keeps its authored rhythm verbatim, exactly like the gpif
    ///     voices-2-4 path.
    /// </summary>
    private void AttachSidecar(TrackHeader track, Arrangement arr)
    {
        if (!_sidecarByTrack.TryGetValue(track.Id, out var sidecar)) return;
        if (sidecar.Notes.Count == 0 && sidecar.Chords.Count == 0) return;

        var shim = new ChartLevel();
        foreach (var n in sidecar.Notes) shim.Notes.Add(n);
        foreach (var c in sidecar.Chords) shim.Chords.Add(c);
        GpPostProcessor.MergeTiedNotes(shim, arr.StringCount);
        GpPostProcessor.LinkSlides(shim, arr.StringCount);
        sidecar.Notes.Clear();
        foreach (var n in shim.Notes) sidecar.Notes.Add(n);
        sidecar.Chords.Clear();
        foreach (var c in shim.Chords) sidecar.Chords.Add(c);

        arr.NotationVoices.Add(sidecar);
    }

    private Arrangement BuildArrangement(TrackHeader track, ChartLevel _)
    {
        var arr = new Arrangement
        {
            Name = track.Name,
            // Name first (bass/rhythm/combo/vocal/lead — the shared tab-import
            // heuristic), string count only as the keyword-less fallback. The
            // old `isBass && StringCount <= 5` also typed a 6-string track
            // NAMED "Bass" as Lead; a 6-string bass is still a bass.
            ArrangementType = ArrangementTypeHeuristics.FromTrackName(track.Name,
                track.StringCount <= 5 ? ArrangementType.Bass : ArrangementType.Lead),
            StringCount = track.StringCount,
            // Already sanitized at the track-header read (the one place), so
            // the note path and this cannot disagree.
            Capo = track.Capo
        };

        // The ONE reference table — see the note in GpArrangementParser and
        // TuningUtilities.GetStandardMidiPitches. 5 is the sanctioned override.
        int[] standardBass5 = [23, 28, 33, 38, 43];
        var standard = track.StringCount == 5
            ? standardBass5
            : TuningUtilities.GetStandardMidiPitches(track.StringCount);

        arr.TuningPitches.Clear();
        // GP stores tunings highest pitch first; convert to lowest-first ordering.
        var offsets = new int[track.StringCount];
        for (var i = 0; i < track.StringCount; i++)
        {
            var gpIndex = track.StringCount - 1 - i;
            var midi = track.Tuning[gpIndex];
            var offset = i < standard.Length ? midi - standard[i] : 0;
            offsets[i] = offset;
            arr.TuningPitches.Add(new TuningOffset(offset));
        }

        // Keep the display/naming tuning in sync with the offsets (chord
        // naming + tuning chips read Arrangement.Tuning; without this a legacy
        // GP import keeps the E-standard default names).
        arr.Tuning = TuningUtilities.GetTuningNames(track.StringCount, offsets);

        return arr;
    }

}
