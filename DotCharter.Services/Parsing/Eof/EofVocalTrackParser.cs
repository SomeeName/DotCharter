using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Parses EOF's vocal track (format 2, <c>PART VOCALS</c>) into the chart's
///     <see cref="VocalTrack" />s. The record layout mirrors <c>eof_load_song_pf</c>:
///     a tone-set byte, then per lyric its text, lyric set, pitch, position, length
///     and flags; then the track's sections, where type 5 is a lyric LINE.
///     <para>
///         EOF keeps lyrics in Rock Band syntax: a trailing <c>-</c> joins the next
///         syllable into a word, and a line is a section, not a marker on the text.
///         So the <c>-</c> becomes <see cref="LyricEvent.IsWordPart" /> and every
///         line becomes one <see cref="LyricPhrase" /> whose last syllable is the
///         phrase end. A file saved without lines falls back to the RS rule
///         (<see cref="RsVocalPhraseBuilder" />: a <c>+</c> or a long silence).
///     </para>
///     <para>
///         Each lyric set is its own track: set 0 is the lead vocal, any other set
///         (harmonies) becomes <c>Vocals (set N)</c>, so nothing in the file is
///         dropped on import.
///     </para>
/// </summary>
internal static class EofVocalTrackParser
{
    /// <summary>EOF's "no pitch" (a talkie); DotCharter's unpitched sentinel is 255.</summary>
    private const byte EofUnpitched = 0;

    private const int Unpitched = 255;

    /// <summary>A zero-length text (2), set and pitch (2), position, length and flags (12).</summary>
    private const int MinLyricBytes = 16;

    /// <summary>A zero-length name (2), difficulty (1) and start, end, flags (12).</summary>
    private const int MinSectionBytes = 15;

    private readonly record struct EofLyric(string Text, byte Set, byte Pitch, uint PosMs, uint LengthMs);

    private readonly record struct EofLine(uint StartMs, uint EndMs);

    public static List<VocalTrack> Parse(EofPackReader r, IList<BeatMarker> beats)
    {
        r.ReadU8(); // tone set
        var count = r.ReadU32();
        RequireRoomFor(r, count, MinLyricBytes, "lyrics");
        var lyrics = new List<EofLyric>((int)count);
        for (var i = 0; i < count; i++)
        {
            var text = r.ReadString();
            var set = r.ReadU8();
            var pitch = r.ReadU8();
            var pos = r.ReadU32();
            var length = r.ReadU32();
            var flags = r.ReadU32();
            if ((flags & EofConstants.NoteFlagExtended) != 0) r.ReadU32();
            lyrics.Add(new EofLyric(text, set, pitch, pos, length));
        }

        var lines = ReadLines(r);
        SkipCustomDataBlocks(r);

        return [.. lyrics.GroupBy(l => l.Set).OrderBy(g => g.Key)
            .Select(g => BuildTrack(g.Key, [.. g.OrderBy(l => l.PosMs)], lines, beats))
            .Where(t => t.Phrases.Count > 0)];
    }

    private static List<EofLine> ReadLines(EofPackReader r)
    {
        var lines = new List<EofLine>();
        var typeCount = r.ReadU16();
        for (var t = 0; t < typeCount; t++)
        {
            var type = r.ReadU16();
            var instances = r.ReadU32();
            RequireRoomFor(r, instances, MinSectionBytes, "sections");
            for (var i = 0; i < instances; i++)
            {
                r.ReadString(); // name
                r.ReadU8(); // difficulty
                var start = r.ReadU32();
                var end = r.ReadU32();
                r.ReadU32(); // flags
                if (type == EofConstants.SectionLyricPhrase) lines.Add(new EofLine(start, end));
            }
        }

        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return lines;
    }

    private static void SkipCustomDataBlocks(EofPackReader r)
    {
        var count = r.ReadU32();
        for (var i = 0; i < count; i++)
        {
            var size = r.ReadU32();
            r.ReadU32(); // id
            if (size == 0) continue;
            // Block size includes the 4-byte ID just consumed.
            r.Skip((int)Math.Max(0L, (long)size - 4));
        }
    }

    private static VocalTrack BuildTrack(byte set, List<EofLyric> lyrics, List<EofLine> lines, IList<BeatMarker> beats)
    {
        var track = new VocalTrack { Name = set == 0 ? "Vocals" : $"Vocals (set {set})" };
        FractionalPosition At(double ms) => TimelineMath.PositionFromImportedMs(ms, beats);

        if (lines.Count == 0)
        {
            var syllables = lyrics.Select(l =>
                new RsVocalSyllable(l.PosMs, (double)l.PosMs + l.LengthMs, l.Text, NoteOf(l.Pitch)));
            foreach (var phrase in RsVocalPhraseBuilder.Build(syllables, At)) track.Phrases.Add(phrase);
            return track;
        }

        // A lyric belongs to the line it starts in (EOF's own test: start <= pos <= end).
        // One that starts outside every line still imports: consecutive strays form a phrase.
        LyricPhrase? strays = null;
        var lineIndex = 0;
        LyricPhrase? current = null;
        foreach (var lyric in lyrics)
        {
            while (lineIndex < lines.Count && lines[lineIndex].EndMs < lyric.PosMs)
            {
                lineIndex++;
                current = null;
            }

            var inLine = lineIndex < lines.Count && lines[lineIndex].StartMs <= lyric.PosMs;
            LyricPhrase phrase;
            if (inLine)
            {
                strays = null;
                if (current == null)
                {
                    var line = lines[lineIndex];
                    current = new LyricPhrase { Position = At(line.StartMs), EndPosition = At(line.EndMs) };
                    track.Phrases.Add(current);
                }

                phrase = current;
            }
            else
            {
                if (strays == null)
                {
                    strays = new LyricPhrase { Position = At(lyric.PosMs) };
                    track.Phrases.Add(strays);
                }

                phrase = strays;
            }

            var ev = ToLyric(lyric, At);
            phrase.Lyrics.Add(ev);
            if (!inLine) phrase.EndPosition = ev.EndPosition;
        }

        foreach (var phrase in track.Phrases) phrase.Lyrics[^1].IsPhraseEnd = true;
        return track;
    }

    private static LyricEvent ToLyric(EofLyric lyric, Func<double, FractionalPosition> at)
    {
        // Only the word join is structure here: a line break is a section, and a bare
        // "+" (a pitch slide onto the previous syllable) is a lyric of its own.
        var text = lyric.Text;
        var isWordPart = text.Length > 1 && text.EndsWith('-');
        if (isWordPart) text = text[..^1];
        return new LyricEvent
        {
            Position = at(lyric.PosMs),
            EndPosition = at((double)lyric.PosMs + lyric.LengthMs),
            Note = NoteOf(lyric.Pitch),
            Text = text,
            IsWordPart = isWordPart
        };
    }

    private static int NoteOf(byte pitch) => pitch == EofUnpitched ? Unpitched : pitch;

    private static void RequireRoomFor(EofPackReader r, uint count, int minRecordBytes, string what)
    {
        if (count > (uint)(r.Remaining / minRecordBytes))
        {
            throw new InvalidDataException(
                $"EOF declares {count} {what} but only {r.Remaining} bytes remain - corrupt file.");
        }
    }
}
