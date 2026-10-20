using System.Globalization;

namespace DotCharter.Services.Parsing.UltraStar;

/// <summary>
/// Bridges the pure <see cref="UltraStarSong"/> model (native beat units) and
/// DotCharter's beat-grid vocal model (<see cref="VocalTrack"/> /
/// <see cref="LyricPhrase"/> / <see cref="LyricEvent"/>), using the project
/// beat timeline for ms↔<see cref="FractionalPosition"/> conversion.
/// <para>
/// UltraStar time model: one file beat = a quarter of a musical beat, so
/// <c>beatMs = 60000 / (BPM * 4)</c> and <c>timeMs = GAP + beat * beatMs</c>
/// (this is the widely-used UltraStar Deluxe convention where the loader
/// multiplies the header BPM by four). Pitch maps to MIDI by
/// <see cref="MidiPitchOffset"/> (file pitch 0 = MIDI 60 / middle C), matching
/// the MIDI note numbers DotCharter stores for Rocksmith vocals. Word
/// boundaries are carried by a trailing space on the syllable text.
/// </para>
/// </summary>
public static class UltraStarVocalConverter
{
    public const int MidiPitchOffset = 60;
    public const double DefaultBpm = 120.0;

    internal static double BeatDurationMs(double bpm)
    {
        if (bpm <= 0) bpm = DefaultBpm;
        return 60000.0 / (bpm * 4.0);
    }

    /// <summary>
    /// Converts a parsed UltraStar song into a vocal track positioned against
    /// <paramref name="beats"/>. For a duet this is the FIRST voice only — the
    /// voices are never merged; <see cref="ToVocalTracks"/> returns all of them.
    /// </summary>
    public static VocalTrack ToVocalTrack(UltraStarSong song, IList<BeatMarker> beats, string? trackName = null)
        => ToVocalTracks(song, beats, trackName)[0];

    /// <summary>
    /// Converts a parsed UltraStar song into one vocal track per voice: a single
    /// track for a solo song, one per <c>P1</c>/<c>P2</c> voice for a duet, in the
    /// order the voices first appear. A <c>#RELATIVE:YES</c> song is resolved to
    /// absolute beats first (<see cref="UltraStarSong.ToAbsoluteBeats"/>).
    /// <para>
    /// The two singers' lines overlap in time, and a vocal track is one syllable
    /// at a time (the lyric lane, the karaoke line and the Rocksmith vocals
    /// export all read it that way), so merging them into one track interleaved
    /// both singers' phrases. A duet track is named from the voice's
    /// <c>#P1</c>/<c>#P2</c> header, else its legacy <c>#DUETSINGERP1</c>/
    /// <c>#DUETSINGERP2</c> alias, else <c>"&lt;trackName&gt; P1"</c>.
    /// </para>
    /// </summary>
    /// <returns>At least one track (an empty one for a song with no notes).</returns>
    public static IReadOnlyList<VocalTrack> ToVocalTracks(UltraStarSong song, IList<BeatMarker> beats,
        string? trackName = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(beats);

        var absolute = song.ToAbsoluteBeats();
        var baseName = string.IsNullOrWhiteSpace(trackName) ? "Vocals" : trackName;
        if (!absolute.IsDuet)
            return [BuildTrack(absolute, absolute.Lines, beats, baseName)];

        // Notes before the first voice change are P1's (the v1 spec's implicit P1).
        var tracks = absolute.Lines
            .GroupBy(l => Math.Max(1, l.Voice))
            .Select(voice => BuildTrack(absolute, voice, beats, VoiceName(absolute, voice.Key, baseName)))
            .Where(t => t.Phrases.Count > 0)
            .ToList();
        return tracks.Count > 0 ? tracks : [new VocalTrack { Name = baseName }];
    }

    private static string VoiceName(UltraStarSong song, int voice, string baseName)
    {
        var number = voice.ToString(CultureInfo.InvariantCulture);
        var singer = song.GetHeader("P" + number);
        if (string.IsNullOrWhiteSpace(singer)) singer = song.GetHeader("DUETSINGERP" + number);
        return string.IsNullOrWhiteSpace(singer) ? $"{baseName} P{number}" : singer.Trim();
    }

    private static VocalTrack BuildTrack(UltraStarSong song, IEnumerable<UltraStarLine> lines,
        IList<BeatMarker> beats, string name)
    {
        var bpm = song.Bpm > 0 ? song.Bpm : DefaultBpm;
        var gap = song.GapMs;
        var beatMs = BeatDurationMs(bpm);

        var track = new VocalTrack { Name = name };

        foreach (var line in lines)
        {
            if (line.Notes.Count == 0) continue;
            var phrase = new LyricPhrase();

            for (var i = 0; i < line.Notes.Count; i++)
            {
                var note = line.Notes[i];
                var isLast = i == line.Notes.Count - 1;
                var startMs = gap + note.StartBeat * beatMs;
                var endMs = gap + (note.StartBeat + Math.Max(0, note.LengthBeats)) * beatMs;

                var pos = beats.Count > 0 ? TimelineMath.PositionFromMs(startMs, beats) : FractionalPosition.Zero;
                var endPos = beats.Count > 0 ? TimelineMath.PositionFromMs(endMs, beats) : FractionalPosition.Zero;

                // A trailing space ends the current word; its absence joins the
                // next syllable. The final note of a line always ends a word.
                var joinsNext = !isLast && !note.Text.EndsWith(' ');

                phrase.Lyrics.Add(new LyricEvent
                {
                    Position = pos,
                    EndPosition = endPos,
                    Text = note.Text.Trim(),
                    Note = ToMidi(note.Pitch),
                    IsWordPart = joinsNext,
                    IsPhraseEnd = isLast
                });
            }

            phrase.Position = phrase.Lyrics[0].Position;
            phrase.EndPosition = phrase.Lyrics[^1].EndPosition;
            track.Phrases.Add(phrase);
        }

        return track;
    }

    /// <summary>
    /// Converts a vocal track into an UltraStar song at the given <paramref name="bpm"/>/<paramref name="gapMs"/>.
    /// Optional metadata populates the standard header tags.
    /// </summary>
    public static UltraStarSong ToSong(VocalTrack track, IList<BeatMarker> beats,
        double bpm, double gapMs,
        string? title = null, string? artist = null, string? audioFile = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(beats);
        if (bpm <= 0) bpm = DefaultBpm;
        var beatMs = BeatDurationMs(bpm);

        var song = new UltraStarSong();
        if (!string.IsNullOrWhiteSpace(title)) song.SetHeader("TITLE", title.Trim());
        if (!string.IsNullOrWhiteSpace(artist)) song.SetHeader("ARTIST", artist.Trim());
        if (!string.IsNullOrWhiteSpace(audioFile)) song.SetHeader("MP3", audioFile.Trim());
        song.SetHeader("BPM", bpm.ToString(CultureInfo.InvariantCulture));
        song.SetHeader("GAP", Math.Round(gapMs).ToString(CultureInfo.InvariantCulture));

        var orderedPhrases = track.Phrases
            .Where(p => p.Lyrics.Any(l => !string.IsNullOrWhiteSpace(l.Text)))
            .OrderBy(p => p.Position)
            .ToList();

        foreach (var phrase in orderedPhrases)
        {
            var lyrics = phrase.Lyrics
                .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .OrderBy(l => l.Position)
                .ToList();
            if (lyrics.Count == 0) continue;

            var line = new UltraStarLine();
            for (var i = 0; i < lyrics.Count; i++)
            {
                var lyric = lyrics[i];
                var isLast = i == lyrics.Count - 1;
                var startMs = TimelineMath.CalculateTimeMs(lyric.Position, beats);
                var endMs = TimelineMath.CalculateTimeMs(lyric.EndPosition, beats);

                var startBeat = (int)Math.Round((startMs - gapMs) / beatMs, MidpointRounding.AwayFromZero);
                var length = (int)Math.Round((endMs - startMs) / beatMs, MidpointRounding.AwayFromZero);
                if (length < 1) length = 1;

                var text = lyric.Text.Trim() + (lyric.IsWordPart && !isLast ? "" : " ");
                line.Notes.Add(new UltraStarNote(UltraStarNoteType.Normal, startBeat, length, ToPitch(lyric.Note), text));
            }

            song.Lines.Add(line);
        }

        // Fill line-break beats: each line (except the last) breaks at the first
        // beat of the following line.
        for (var i = 0; i < song.Lines.Count - 1; i++)
        {
            var next = song.Lines[i + 1];
            if (next.Notes.Count > 0)
                song.Lines[i].BreakBeat = next.Notes[0].StartBeat;
        }

        return song;
    }

    private static int ToMidi(int pitch)
    {
        var midi = pitch + MidiPitchOffset;
        if (midi < 0) midi = 0;
        if (midi > 127) midi = 127;
        return midi;
    }

    private static int ToPitch(int note)
    {
        // 255 = "no pitch" sentinel; UltraStar has no such concept, emit a
        // neutral pitch (file 0 = MIDI 60). Real pitches map back exactly.
        if (note is <= 0 or >= 255) return 0;
        return note - MidiPitchOffset;
    }
}
