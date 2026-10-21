using System.Text;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

// A byte-level writer for the binary Guitar Pro 3/4/5 (.gp3/.gp4/.gp5) score
// format, authored to mirror EXACTLY the shipped reader
// (DotCharter.Services.Parsing.GuitarPro.GpLegacyBinaryReader/GpLegacyImporter)
// field-for-field. Field order, the version-branched header skips, the GP5
// per-(measure,track) sentinel bytes and the note-effect sub-blocks all match
// what that reader consumes; the second reference is PyGuitarPro / TuxGuitar's
// documentation of the same layout.
//
// No real .gp3/.gp4 files existed on the build machine, so these versions are
// covered by fixtures. The tests self-validate the writer by asserting the
// reader reads back exactly what was written (a closed loop); the GP5 path is
// additionally validated against a real v5.10 file where one is present.

// Slide is the RAW on-wire slide value (note-effects fx2 bit 0x08, GP4+ only):
// a bitmask in GP5 (0x01 shift, 0x02 legato, 0x04 out-down, 0x08 out-up,
// 0x10 in-from-below, 0x20 in-from-above) and one signed discrete value in GP4
// (1..4 out, -1 in-from-below, -2 in-from-above). GraceSlide sets the grace
// block's transition byte to 1 ("sl."), which is what makes a grace materialize
// into a real sliding note instead of staying a notation hint. GraceOnBeat sets
// bit 0x02 of the GP5-only grace FLAG byte — Log Charter reads that byte as
// `graceBefore = (flags & 0x02) == 0`, so setting it authors an ON-beat ornament
// (one that sits on the beat and pushes its target) instead of a before-beat one.
// GP3/GP4 have no flag byte, so the writer ignores it for those versions exactly
// as the reader does.
// TremoloPicking sets NOTE-effects fx2 bit 0x04 (followed by a speed byte) —
// the picking flag, NOT the beat's whammy bar (which is the same bit number in
// the BEAT's fx2 and is followed by a bend chunk). TrillUpperFret sets note fx2
// bit 0x20 and writes the trill's upper fret + period. Both are GP4+ only, like
// every other fx2 effect.
// Staccato sets NOTE-effects fx2 bit 0x01 — a FLAG-only bit (no payload byte
// follows it, unlike 0x04/0x08/0x10/0x20), which the reader turns into the chart
// model's Accent because Rocksmith has no staccato mark. GP4+ only, like the rest
// of fx2. Accent (the record's own flag) is a different bit entirely: the note
// HEADER's 0x40.
// Dead writes note type 3, the "x" note the reader imports as MuteType.Full.
// HammerOrigin sets NOTE-effects fx1 bit 0x02 — the slur's ORIGIN note (flag-only);
// the destination is the next note on the same string.
// PalmMute sets NOTE-effects fx2 bit 0x02 — flag-only like Staccato, GP4+ only.
internal readonly record struct FxNote(int StringIndex, int Fret, int? GraceFret = null, bool Tied = false,
    bool Accent = false, bool LetRing = false, int? Slide = null, bool GraceSlide = false,
    bool GraceOnBeat = false, bool TremoloPicking = false, int? TrillUpperFret = null,
    int? LeftFinger = null, bool Staccato = false, bool Dead = false, bool PalmMute = false,
    bool HammerOrigin = false);

internal sealed class FxBeat
{
    public int Duration;      // -2 whole, -1 half, 0 quarter, 1 eighth, 2 16th, 3 32nd, 4 64th
    public bool Dotted;
    public int Tuplet;        // 0 = none, else n (3 = triplet, 5 = quintuplet, ...)
    public bool Rest;
    public string? ChordDiagramName; // non-null => beat carries a new-format chord diagram
    public bool BeatVibrato;         // beat-effects fx1 bit 0x01 (flag-only)
    public bool BeatNaturalHarmonic; // beat-effects fx1 bit 0x04 (flag-only)

    /// <summary>GP4/5 string-effect type behind beat-effects fx1 bit 0x20:
    /// 0 = absent, 1 = tapping, 2 = slap, 3 = pop.</summary>
    public int BeatStringEffect;

    /// <summary>Beat-effects fx2 bit 0x04 — the tremolo BAR (whammy), followed by
    /// a bend chunk. Deliberately the same bit NUMBER as the note-level tremolo
    /// PICKING flag, which is exactly the pair the importer used to cross.</summary>
    public bool BeatTremoloBar;

    public List<FxNote> Notes = [];

    public static FxBeat Note(int duration, int stringIndex, int fret, bool dotted = false, int tuplet = 0,
        int? grace = null) =>
        new()
        {
            Duration = duration,
            Dotted = dotted,
            Tuplet = tuplet,
            Notes = { new FxNote(stringIndex, fret, grace) }
        };

    public static FxBeat Chord(int duration, params FxNote[] notes) =>
        new() { Duration = duration, Notes = notes.ToList() };

    public static FxBeat RestBeat(int duration) => new() { Duration = duration, Rest = true };
}

internal sealed class FxMeasure
{
    public int Num = 4;
    public int Den = 4;

    /// <summary>GP5's per-measure-header triplet-feel byte: 0 none, 1 eighth,
    /// 2 sixteenth. Ignored for GP3/GP4, which carry ONE song-wide shuffle
    /// boolean instead (see the writer's <c>globalShuffle</c> argument).</summary>
    public int TripletFeel;

    /// <summary>Measure-header bit 0x04 — the `|:` open. Carries no payload
    /// byte in any version.</summary>
    public bool RepeatStart;

    /// <summary>Measure-header bit 0x08 — the `:|` close, followed by a count
    /// byte. This is the TOTAL number of passes; the writer converts to the
    /// on-wire spelling, which is that number for GP5 and one LESS for GP3/GP4
    /// (whose byte counts only the extra passes). 0 = no close here.</summary>
    public int RepeatTotalPasses;

    /// <summary>Measure-header bit 0x10 — the alternate-ending / volta byte, a
    /// bitmask over passes with bit 0 = pass 1. 0 = absent. GP3/GP4 write it
    /// immediately after the repeat count; GP5 writes it after the marker and
    /// then SKIPS the padding byte that a voltaless bar carries.</summary>
    public int AlternateEndingsMask;

    public List<List<FxBeat>> Voices = [];

    public FxMeasure AddVoice(params FxBeat[] beats)
    {
        Voices.Add(beats.ToList());
        return this;
    }
}

internal sealed class LegacyGpFixtureBuilder
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    private readonly int _major;
    private readonly int _minor;
    private readonly List<byte> _bytes = [];

    // Song-attributes header metadata (empty = the field is written blank,
    // which is also what GP itself writes for an unfilled score property).
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _tab = "";

    // The track header's fret count (GP's own default is 24).
    private int _frets = 24;

    // The track header's tuning, highest pitch first (null = 6-string E standard).
    private int[]? _tuningHighFirst;

    private LegacyGpFixtureBuilder(int major, int minor)
    {
        _major = major;
        _minor = minor;
    }

    private bool IsAfter50 => _major > 5 || (_major == 5 && _minor >= 10);
    private bool IsV5 => _major >= 5;
    private bool IsV4Plus => _major >= 4;

    /// <summary>Writes a complete single-track fixture and returns its path
    /// (deleted by the caller). Version is e.g. "3.00", "4.06", "5.00", "5.10".
    /// The optional title/artist/album/tab land in the song-attributes header
    /// exactly where the reader's <c>ReadSongAttributes</c> consumes them.</summary>
    public static string WriteFile(string version, IReadOnlyList<FxMeasure> measures,
        string trackName = "Guitar", int stringCount = 6, int capo = 0, bool globalShuffle = false,
        string title = "", string artist = "", string album = "", string tab = "", int frets = 24,
        int[]? tuningHighFirst = null)
    {
        var dot = version.IndexOf('.');
        var major = int.Parse(version[..dot]);
        var minor = int.Parse(version[(dot + 1)..]);
        var builder = new LegacyGpFixtureBuilder(major, minor)
        {
            _title = title,
            _artist = artist,
            _album = album,
            _tab = tab,
            _frets = frets,
            _tuningHighFirst = tuningHighFirst
        };
        var bytes = builder.Build(version, measures, trackName, stringCount, capo, globalShuffle,
            extraTracks: null);

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gplegacy-{Guid.NewGuid():N}.gp{major}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    ///     Writes a MULTI-track fixture: every track carries its own bars, but
    ///     the measure HEADERS (count + time signatures) are the file's, so all
    ///     tracks must supply the same number of measures. Exists because several
    ///     import side channels are keyed by chart POSITION and used to be shared
    ///     across the whole file — a bug only a second track can show.
    /// </summary>
    public static string WriteMultiTrackFile(string version,
        IReadOnlyList<(string Name, IReadOnlyList<FxMeasure> Measures)> tracks,
        int stringCount = 6, int capo = 0)
    {
        var dot = version.IndexOf('.');
        var major = int.Parse(version[..dot]);
        var minor = int.Parse(version[(dot + 1)..]);
        var builder = new LegacyGpFixtureBuilder(major, minor);
        var bytes = builder.Build(version, tracks[0].Measures, tracks[0].Name, stringCount, capo,
            globalShuffle: false, extraTracks: tracks.Skip(1).ToList());

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gplegacy-{Guid.NewGuid():N}.gp{major}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private byte[] Build(string version, IReadOnlyList<FxMeasure> measures, string trackName, int stringCount,
        int capo, bool globalShuffle,
        IReadOnlyList<(string Name, IReadOnlyList<FxMeasure> Measures)>? extraTracks = null)
    {
        // ---- version banner (ReadStringByte(31)) ----
        WStringByte($"FICHIER GUITAR PRO v{version}", 31);

        // ---- song attributes ----
        // Reader order (GpLegacyImporter.ReadSongAttributes): title, subtitle,
        // artist, album, [words gp5-only], music, copyright, tab, instructions.
        WStringInt(_title);
        WStringInt(""); // subtitle
        WStringInt(_artist);
        WStringInt(_album);
        if (_major >= 5) WStringInt(""); // words (lyricist)
        WStringInt(""); // music
        WStringInt(""); // copyright
        WStringInt(_tab);
        WStringInt(""); // instructions
        W32(0); // notices count

        // ---- lyrics / pre-song ----
        if (_major <= 4) W8(globalShuffle ? 1 : 0); // song-wide shuffle feel (gp3/gp4)
        if (_major >= 4)
        {
            W32(0); // associated lyric track
            for (var i = 0; i < 5; i++)
            {
                W32(0); // start bar
                W32(0); // line length (0 => no text)
            }
        }

        if (_major >= 5)
        {
            for (var i = 0; i < (IsAfter50 ? 49 : 30); i++) W8(0); // page setup block
            for (var i = 0; i < 11; i++) WStringInt(""); // page setup strings
        }

        // ---- tempo + key ----
        W32(120); // initial bpm
        if (IsAfter50) W8(0);
        if (IsV4Plus)
        {
            W8(0); // key accidentals
            W8(0);
            W8(0);
            W8(0); // 3 padding
            W8(0); // octave
        }
        else
        {
            W32(0); // key accidentals
        }

        // ---- channels (64 × 12 bytes) ----
        for (var c = 0; c < 64; c++)
        {
            W32(0); // patch
            for (var k = 0; k < 6; k++) W8(0); // vol/pan/chorus/reverb/phaser/tremolo
            W8(0);
            W8(0); // padding
        }

        if (IsV5)
        {
            for (var i = 0; i < 38; i++) W8(0); // musical directions
            W32(0); // master reverb / unknown
        }

        // ---- counts ----
        var allTracks = new List<(string Name, IReadOnlyList<FxMeasure> Measures)> { (trackName, measures) };
        if (extraTracks != null) allTracks.AddRange(extraTracks);

        W32(measures.Count);
        W32(allTracks.Count);

        WriteMeasureHeaders(measures);
        for (var t = 0; t < allTracks.Count; t++) WriteTrack(allTracks[t].Name, stringCount, capo, t);
        // Trailing padding after the WHOLE track block — once, not once per
        // track (the reader skips it after its own track loop).
        if (_major >= 5) for (var i = 0; i < (IsAfter50 ? 1 : 2); i++) W8(0);
        WriteBeats(allTracks, stringCount);

        return _bytes.ToArray();
    }

    private void WriteMeasureHeaders(IReadOnlyList<FxMeasure> measures)
    {
        var runningNum = 4;
        var runningDen = 4;

        for (var i = 0; i < measures.Count; i++)
        {
            if (_major >= 5 && i > 0) W8(0); // inter-header sentinel

            var m = measures[i];
            var sigChanged = m.Num != runningNum || m.Den != runningDen;
            var bitmask = sigChanged ? 0x03 : 0x00;
            if (m.RepeatStart) bitmask |= 0x04;
            if (m.RepeatTotalPasses > 0) bitmask |= 0x08;
            if (m.AlternateEndingsMask != 0) bitmask |= 0x10;
            W8(bitmask);
            if (sigChanged)
            {
                W8(m.Num);
                W8(m.Den);
                runningNum = m.Num;
                runningDen = m.Den;
            }

            // 0x04 carries no payload. 0x08's count byte is the TOTAL passes in
            // GP5 and the number of EXTRA passes in GP3/GP4.
            if ((bitmask & 0x08) != 0) W8(_major >= 5 ? m.RepeatTotalPasses : m.RepeatTotalPasses - 1);

            if (_major < 5)
            {
                // 0x20 (marker) and 0x40 (key) stay unset here.
                if ((bitmask & 0x10) != 0) W8(m.AlternateEndingsMask);
            }
            else
            {
                // 0x20 (marker) and 0x40 (key) stay unset here.
                if ((bitmask & 0x10) != 0) W8(m.AlternateEndingsMask); // volta
                if ((bitmask & 0x03) != 0) for (var k = 0; k < 4; k++) W8(0); // unknown-4 on sig change
                if ((bitmask & 0x10) == 0) W8(0); // padding when no volta
                W8(m.TripletFeel); // triple feel (0 none, 1 eighth, 2 sixteenth)
            }
        }
    }

    private void WriteTrack(string trackName, int stringCount, int capo, int trackIndex = 0)
    {
        W8(0); // track bitmask (bit0 drums = off)
        // Pre-name sentinel: v5.10 writes it only for track 0, v5.0 for every track.
        if (_major >= 5 && (trackIndex == 0 || !IsAfter50)) W8(0);

        WStringByte(trackName, 41);
        W32(stringCount);

        // GP stores tunings highest-pitch first; standard 6-string E-standard.
        var tuningHighFirst = _tuningHighFirst ?? [64, 59, 55, 50, 45, 40, 0];
        for (var s = 0; s < 7; s++) W32(s < tuningHighFirst.Length ? tuningHighFirst[s] : 0);

        W32(0); // midi port
        W32(0); // midi channel
        W32(0); // midi effect channel
        W32(_frets); // frets
        W32(capo);
        W32(0); // color

        if (_major == 5)
        {
            for (var i = 0; i < (IsAfter50 ? 49 : 44); i++) W8(0);
            if (IsAfter50)
            {
                WStringInt(""); // effect 1
                WStringInt(""); // effect 2
            }
        }

    }

    private void WriteBeats(IReadOnlyList<(string Name, IReadOnlyList<FxMeasure> Measures)> tracks, int stringCount)
    {
        var voicesPerTrack = _major >= 5 ? 2 : 1;
        var measureCount = tracks[0].Measures.Count;

        // The reader's loop nesting: measure OUTER, track INNER.
        for (var measureIdx = 0; measureIdx < measureCount; measureIdx++)
        {
            for (var trackIdx = 0; trackIdx < tracks.Count; trackIdx++)
            {
                var m = tracks[trackIdx].Measures[measureIdx];
                for (var voice = 0; voice < voicesPerTrack; voice++)
                {
                    var beats = voice < m.Voices.Count ? m.Voices[voice] : [];
                    W32(beats.Count);
                    foreach (var beat in beats) WriteBeat(beat, stringCount);
                }

                var isLast = measureIdx == measureCount - 1 && trackIdx == tracks.Count - 1;
                if (_major >= 5 && !isLast) W8(0); // per-(measure,track) sentinel
            }
        }
    }

    private void WriteBeat(FxBeat beat, int stringCount)
    {
        var hasBeatFx = beat.BeatVibrato || beat.BeatNaturalHarmonic ||
                        beat.BeatStringEffect > 0 || beat.BeatTremoloBar;
        var bitmask = 0;
        if (beat.Rest) bitmask |= 0x40;
        if (beat.Dotted) bitmask |= 0x01;
        if (beat.Tuplet > 0) bitmask |= 0x20;
        if (beat.ChordDiagramName != null) bitmask |= 0x02;
        if (hasBeatFx) bitmask |= 0x08;
        W8(bitmask);

        if (beat.Rest) W8(0); // rest type

        W8((byte)(sbyte)beat.Duration);

        if (beat.Tuplet > 0) W32(beat.Tuplet);

        if (beat.ChordDiagramName != null) WriteChordDiagram(beat.ChordDiagramName);

        if (hasBeatFx)
        {
            var fx1 = (beat.BeatVibrato ? 0x01 : 0) | (beat.BeatNaturalHarmonic ? 0x04 : 0) |
                      (beat.BeatStringEffect > 0 ? 0x20 : 0);
            var fx2 = _major >= 4 && beat.BeatTremoloBar ? 0x04 : 0;
            W8(fx1);
            if (_major >= 4) W8(fx2);

            // Reader order: string effect (fx1 0x20) → tremolo bar (fx2 0x04) →
            // stroke (fx1 0x40) → pickstroke (fx2 0x02).
            if (beat.BeatStringEffect > 0)
            {
                if (_major < 4)
                {
                    W8(beat.BeatStringEffect);
                    W32(0);
                }
                else
                {
                    W8(beat.BeatStringEffect); // 1 tapping, 2 slap, 3 pop
                }
            }

            if ((fx2 & 0x04) != 0) WriteBendChunk();
        }

        // strings mask: GP bit (7 - stringNo); stringNo = stringCount - stringIndex.
        var stringsMask = 0;
        foreach (var n in beat.Notes)
        {
            var stringNo = stringCount - n.StringIndex;
            stringsMask |= 1 << (7 - stringNo);
        }

        W8(stringsMask);

        // Notes are read in ascending stringNo (= descending string index).
        foreach (var n in beat.Notes.OrderByDescending(n => n.StringIndex))
            WriteNote(n);

        if (_major >= 5) W16(0); // transpose bitmask (0x800 unset)
    }

    private void WriteNote(FxNote note)
    {
        var hasEffects = note.GraceFret.HasValue || note.LetRing || note.Slide.HasValue ||
                         note.TremoloPicking || note.TrillUpperFret.HasValue || note.Staccato || note.PalmMute ||
                         note.HammerOrigin;
        var bitmask = 0x20 | (hasEffects ? 0x08 : 0); // 0x20 => note type + fret present
        if (note.Accent) bitmask |= 0x40;             // flag-only accent bit
        if (note.LeftFinger.HasValue) bitmask |= 0x80; // fingering block present
        W8(bitmask);

        W8(note.Dead ? 3 : note.Tied ? 2 : 1); // note type: 1 normal, 2 = tied to previous, 3 = dead
        W8((byte)(sbyte)note.Fret); // fret

        // Fingering: left hand then right hand, -1 = none. Sits between the fret
        // and the v5 padding byte, exactly where the reader consumes it.
        if (note.LeftFinger.HasValue)
        {
            W8((byte)(sbyte)note.LeftFinger.Value);
            W8(0xFF); // right hand: none
        }

        if (_major >= 5) W8(0); // note padding byte

        if (hasEffects) WriteNoteEffects(note);
    }

    /// <summary>
    /// The GP4/GP5 "new format" chord diagram: a fixed 107-byte block
    /// (format byte + 106) — the exact layout EOF's gp_import.c:3071-3181 and
    /// Charter's GP5FileReader.readVersion500Chord consume. The 5-byte gap
    /// after the name (2 unknown + fifth/ninth/eleventh tonality) is the byte
    /// the importer used to under-skip, desyncing the whole beat stream.
    /// </summary>
    private void WriteChordDiagram(string name)
    {
        W8(1); // new-format marker
        for (var i = 0; i < 16; i++) W8(0);        // sharp/root/type/extension/bass/tonality
        WStringByte(name, 21);                     // chord name (1 length byte + 20 chars)
        for (var i = 0; i < 5; i++) W8(0);         // 2 unknown + fifth/ninth/eleventh tonality
        W32(7);                                    // base fret
        for (var s = 0; s < 7; s++) W32(-1);       // fret per string
        for (var i = 0; i < 16; i++) W8(0);        // barre count + barre frets/starts/stops
        for (var i = 0; i < 8; i++) W8(0);         // interval/omission bytes + unknown
        for (var i = 0; i < 7; i++) W8(0xFF);      // fingering per string
        W8(0);                                     // display-fingering flag
    }

    private void WriteNoteEffects(FxNote note)
    {
        var fx1 = (note.GraceFret.HasValue ? 0x10 : 0) | (note.LetRing ? 0x08 : 0) | (note.HammerOrigin ? 0x02 : 0);
        // The slide VALUE lives behind fx2 bit 0x08 and only exists from GP4 on
        // (GP3 has a flag-only slide in fx1 and no value byte at all). Tremolo
        // PICKING (0x04) and the trill (0x20) are GP4+ only for the same reason.
        var fx2 = 0;
        if (_major >= 4)
        {
            if (note.Staccato) fx2 |= 0x01; // flag-only: no payload follows
            if (note.PalmMute) fx2 |= 0x02; // flag-only: no payload follows
            if (note.TremoloPicking) fx2 |= 0x04;
            if (note.Slide.HasValue) fx2 |= 0x08;
            if (note.TrillUpperFret.HasValue) fx2 |= 0x20;
        }

        W8(fx1);
        if (_major >= 4) W8(fx2);

        if ((fx1 & 0x10) != 0)
        {
            W8((byte)(sbyte)note.GraceFret!.Value); // grace fret
            W8(0); // grace dynamic
            W8(note.GraceSlide ? 1 : 0); // grace transition (1 = "sl.")
            W8(0); // grace duration
            // GP5-only grace flag byte: 0x01 dead grace, 0x02 = ON the beat.
            if (_major >= 5) W8(note.GraceOnBeat ? 0x02 : 0x00);
        }

        // Read order inside the reader's fx2 block: tremolo picking (0x04), then
        // the slide value (0x08), then harmonic (0x10), then the trill (0x20).
        if ((fx2 & 0x04) != 0) W8(2);                               // picking speed (8th/16th/32nd)
        if ((fx2 & 0x08) != 0) W8((byte)(sbyte)note.Slide!.Value);
        if ((fx2 & 0x20) != 0)
        {
            W8((byte)(sbyte)note.TrillUpperFret!.Value); // trill upper fret (capo-relative)
            W8(2);                                       // trill period
        }
    }

    /// <summary>The bend chunk both the note-bend reader and the beat's tremolo
    /// bar consume: type, height, then a point count and that many
    /// (offset, value, vibrato) triples.</summary>
    private void WriteBendChunk()
    {
        W8(0);  // bend type
        W32(0); // bend height
        W32(2); // point count
        W32(0);
        W32(0);
        W8(0);
        W32(60);
        W32(-100);
        W8(0);
    }

    // ---- primitive writers (little-endian) ----

    private void W8(int v) => _bytes.Add((byte)v);

    private void W16(int v)
    {
        _bytes.Add((byte)(v & 0xFF));
        _bytes.Add((byte)((v >> 8) & 0xFF));
    }

    private void W32(int v)
    {
        _bytes.Add((byte)(v & 0xFF));
        _bytes.Add((byte)((v >> 8) & 0xFF));
        _bytes.Add((byte)((v >> 16) & 0xFF));
        _bytes.Add((byte)((v >> 24) & 0xFF));
    }

    private void WStringInt(string s)
    {
        var raw = Latin1.GetBytes(s);
        W32(raw.Length + 1); // block size = string length + length byte
        W8(raw.Length);
        _bytes.AddRange(raw);
    }

    private void WStringByte(string s, int totalChunkBytes)
    {
        var raw = Latin1.GetBytes(s);
        var len = Math.Min(raw.Length, totalChunkBytes - 1);
        W8(len);
        for (var i = 0; i < len; i++) _bytes.Add(raw[i]);
        for (var i = 0; i < totalChunkBytes - 1 - len; i++) _bytes.Add(0);
    }
}
