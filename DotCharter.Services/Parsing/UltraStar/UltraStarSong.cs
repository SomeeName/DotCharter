using System.Globalization;

namespace DotCharter.Services.Parsing.UltraStar;

/// <summary>The kind of an UltraStar note, encoded by the leading symbol of a note line.</summary>
public enum UltraStarNoteType
{
    /// <summary><c>:</c> — a normal sung note.</summary>
    Normal,
    /// <summary><c>*</c> — a golden note (worth bonus points).</summary>
    Golden,
    /// <summary><c>F</c> — a freestyle note (any pitch scores).</summary>
    Freestyle,
    /// <summary><c>R</c> — a rap note.</summary>
    Rap,
    /// <summary><c>G</c> — a golden rap note.</summary>
    RapGolden
}

/// <summary>
/// One UltraStar note line: <c>&lt;type&gt; &lt;startBeat&gt; &lt;lengthBeats&gt; &lt;pitch&gt; &lt;text&gt;</c>.
/// <see cref="Text"/> is kept verbatim (including significant trailing spaces
/// that encode word boundaries) so the codec round-trips losslessly.
/// </summary>
public sealed record UltraStarNote(UltraStarNoteType Type, int StartBeat, int LengthBeats, int Pitch, string Text);

/// <summary>
/// A single lyric line (the run of notes between two <c>-</c> line breaks).
/// <see cref="BreakBeat"/> holds the beat printed on the trailing <c>-</c>
/// marker; it is null for the final line (and for a duet voice's last line).
/// <see cref="BreakBeat2"/> preserves the two-number line-break form
/// (<c>- endBeat relOffset</c>), which only a <c>#RELATIVE:YES</c> file uses.
/// </summary>
public sealed class UltraStarLine
{
    public List<UltraStarNote> Notes { get; } = [];
    public int? BreakBeat { get; set; }
    public int? BreakBeat2 { get; set; }

    /// <summary>
    /// The duet voice the line belongs to: the number of the last <c>P1</c> /
    /// <c>P2</c> voice change before it, or 0 in a song with no voice change.
    /// </summary>
    public int Voice { get; set; }
}

/// <summary>
/// Parsed representation of an UltraStar <c>.txt</c> song file: the ordered
/// header tags plus the note lines grouped by line break. This model uses the
/// file's native <em>beat</em> units (integers) so the pure codec round-trips
/// exactly; conversion to/from DotCharter's beat-grid domain model lives in
/// <see cref="UltraStarVocalConverter"/>.
/// </summary>
public sealed class UltraStarSong
{
    /// <summary>Header tags in file order (key upper-cased, value verbatim). Duplicates preserved.</summary>
    public List<KeyValuePair<string, string>> Headers { get; } = [];

    public List<UltraStarLine> Lines { get; } = [];

    public string? GetHeader(string key)
    {
        foreach (var kv in Headers)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    public void SetHeader(string key, string value)
    {
        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                Headers[i] = new KeyValuePair<string, string>(Headers[i].Key, value);
                return;
            }
        }

        Headers.Add(new KeyValuePair<string, string>(key.ToUpperInvariant(), value));
    }

    /// <summary>The song BPM as stated in the <c>#BPM</c> header (0 when missing/invalid). Accepts <c>,</c> or <c>.</c> decimals.</summary>
    public double Bpm => ParseNumber(GetHeader("BPM"));

    /// <summary>The <c>#GAP</c> (lead-in) in milliseconds (0 when missing).</summary>
    public double GapMs => ParseNumber(GetHeader("GAP"));

    /// <summary><c>#RELATIVE:YES</c> — every line's beats count from the previous line break.</summary>
    public bool IsRelative => string.Equals(GetHeader("RELATIVE")?.Trim(), "YES", StringComparison.OrdinalIgnoreCase);

    /// <summary>The song has <c>P1</c>/<c>P2</c> voice changes (a duet).</summary>
    public bool IsDuet => Lines.Any(l => l.Voice > 0);

    /// <summary>
    /// The same song with every beat ABSOLUTE. A non-relative song is returned
    /// as is; a <c>#RELATIVE:YES</c> one is resolved into a copy without that
    /// header, so the converter only ever sees absolute beats while the parsed
    /// model itself stays the file's exact text (the writer round-trips it).
    /// <para>
    /// Relative mode, as UltraStar Deluxe reads it (<c>USong.pas</c>
    /// <c>NewSentence</c>) and the format spec's Appendix A defines it: each
    /// voice keeps a running offset <c>rel</c>, starting at 0; a note's or a
    /// line break's beat is <c>rel + beat</c>; and after a line break
    /// <c>- beat relOffset</c>, <c>rel</c> grows by <c>relOffset</c>. A break
    /// written with one number (malformed in relative mode — USDX rejects it)
    /// is read as <c>- beat beat</c>, so the next line still counts from it.
    /// </para>
    /// </summary>
    public UltraStarSong ToAbsoluteBeats()
    {
        if (!IsRelative) return this;

        var song = new UltraStarSong();
        foreach (var header in Headers)
        {
            if (!string.Equals(header.Key, "RELATIVE", StringComparison.OrdinalIgnoreCase))
                song.Headers.Add(header);
        }

        var rel = new Dictionary<int, int>();
        foreach (var line in Lines)
        {
            var offset = rel.GetValueOrDefault(line.Voice);
            var absolute = new UltraStarLine { Voice = line.Voice };
            foreach (var note in line.Notes)
                absolute.Notes.Add(note with { StartBeat = note.StartBeat + offset });

            if (line.BreakBeat is { } breakBeat)
            {
                absolute.BreakBeat = breakBeat + offset;
                rel[line.Voice] = offset + (line.BreakBeat2 ?? breakBeat);
            }

            song.Lines.Add(absolute);
        }

        return song;
    }

    internal static double ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var normalized = raw.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
