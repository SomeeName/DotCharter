using System.Globalization;

namespace DotCharter.Services.Parsing.UltraStar;

/// <summary>
/// Parses an UltraStar <c>.txt</c> song into an <see cref="UltraStarSong"/>.
/// Pure and tolerant: header tags, note lines (<c>: * F R G</c>), line breaks
/// (<c>-</c>), duet voice changes (<c>P1</c>, <c>P2</c>) and the terminating
/// <c>E</c> are understood; unrecognized or malformed lines are skipped rather
/// than throwing. Note text is preserved verbatim (trailing spaces are
/// significant word-boundary markers), and beats are kept as WRITTEN — a
/// <c>#RELATIVE:YES</c> file is resolved by <see cref="UltraStarSong.ToAbsoluteBeats"/>.
/// </summary>
public sealed class UltraStarLyricsParser
{
    public UltraStarSong Parse(string? content)
    {
        var song = new UltraStarSong();
        if (string.IsNullOrWhiteSpace(content)) return song;

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = new UltraStarLine();
        var currentHasContent = false;
        var voice = 0;

        foreach (var raw in lines)
        {
            if (raw.Length == 0) continue;
            var trimmedStart = raw.TrimStart();
            if (trimmedStart.Length == 0) continue;

            var marker = trimmedStart[0];
            switch (marker)
            {
                case '#':
                    ParseHeader(trimmedStart, song);
                    break;

                case 'E' when IsEndMarker(trimmedStart):
                    // End of song. Flush any pending line and stop.
                    Flush(song, current, currentHasContent);
                    return song;

                case '-':
                    ParseLineBreak(trimmedStart, current);
                    song.Lines.Add(current);
                    current = new UltraStarLine { Voice = voice };
                    currentHasContent = false;
                    break;

                case 'P' when TryParseVoiceChange(trimmedStart, out var nextVoice):
                    // A voice change ends the previous voice's last line (it carries
                    // no "-" of its own), and every line after it is that voice's.
                    if (currentHasContent) song.Lines.Add(current);
                    voice = nextVoice;
                    current = new UltraStarLine { Voice = voice };
                    currentHasContent = false;
                    break;

                case ':':
                case '*':
                case 'F':
                case 'R':
                case 'G':
                    var note = ParseNote(trimmedStart, marker);
                    if (note != null)
                    {
                        current.Notes.Add(note);
                        currentHasContent = true;
                    }
                    break;

                    // Anything else is ignored.
            }
        }

        Flush(song, current, currentHasContent);
        return song;
    }

    private static void Flush(UltraStarSong song, UltraStarLine line, bool hasContent)
    {
        if (hasContent || line.BreakBeat.HasValue)
            song.Lines.Add(line);
    }

    private static bool IsEndMarker(string line)
    {
        // A lone 'E' (optionally with trailing whitespace) ends the song.
        // Guards against note-less lines that merely start with 'E'.
        for (var i = 1; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A voice change: <c>P</c> and the voice number (<c>P1</c>, <c>P2</c>; the
    /// v1 format allows up to <c>P9</c>), optionally with a space between as
    /// UltraStar Deluxe accepts, and nothing else on the line.
    /// </summary>
    private static bool TryParseVoiceChange(string line, out int voice)
    {
        voice = 0;
        var rest = line[1..].Trim();
        return rest.Length is > 0 and <= 2 && rest.All(char.IsAsciiDigit)
            && int.TryParse(rest, CultureInfo.InvariantCulture, out voice) && voice > 0;
    }

    private static void ParseHeader(string line, UltraStarSong song)
    {
        var colon = line.IndexOf(':');
        if (colon <= 1) return;
        var key = line.Substring(1, colon - 1).Trim().ToUpperInvariant();
        var value = line[(colon + 1)..].Trim();
        if (key.Length == 0) return;
        song.Headers.Add(new KeyValuePair<string, string>(key, value));
    }

    private static void ParseLineBreak(string line, UltraStarLine current)
    {
        // "- <beat>" or (rare) "- <endBeat> <nextBeat>".
        var tokens = line[1..].Split(' ', '\t');
        var nums = new List<int>();
        foreach (var tok in tokens)
        {
            if (int.TryParse(tok.Trim(), CultureInfo.InvariantCulture, out var v))
                nums.Add(v);
        }

        if (nums.Count >= 1) current.BreakBeat = nums[0];
        if (nums.Count >= 2) current.BreakBeat2 = nums[1];
    }

    private static UltraStarNote? ParseNote(string line, char marker)
    {
        var type = marker switch
        {
            ':' => UltraStarNoteType.Normal,
            '*' => UltraStarNoteType.Golden,
            'F' => UltraStarNoteType.Freestyle,
            'R' => UltraStarNoteType.Rap,
            'G' => UltraStarNoteType.RapGolden,
            _ => UltraStarNoteType.Normal
        };

        var i = 1; // skip the marker
        if (!TryReadInt(line, ref i, out var start)) return null;
        if (!TryReadInt(line, ref i, out var length)) return null;
        if (!TryReadInt(line, ref i, out var pitch)) return null;

        // Text is everything after the single delimiter space that follows the
        // pitch token; further leading spaces are significant word markers.
        string text;
        if (i < line.Length && line[i] == ' ')
            text = line[(i + 1)..];
        else if (i < line.Length && char.IsWhiteSpace(line[i]))
            text = line[(i + 1)..];
        else
            text = "";

        return new UltraStarNote(type, start, length, pitch, text);
    }

    /// <summary>
    /// Reads a leading-whitespace-skipped signed integer starting at
    /// <paramref name="i"/>, advancing it to the first char after the number.
    /// </summary>
    private static bool TryReadInt(string s, ref int i, out int value)
    {
        value = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        var start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        var digitsStart = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == digitsStart) { i = start; return false; }
        return int.TryParse(s.AsSpan(start, i - start), CultureInfo.InvariantCulture, out value);
    }
}
