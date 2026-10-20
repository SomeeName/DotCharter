using System.Globalization;
using System.Text;

namespace DotCharter.Services.Parsing.UltraStar;

/// <summary>
/// Formats an <see cref="UltraStarSong"/> back into UltraStar <c>.txt</c> text.
/// Round-trips exactly with <see cref="UltraStarLyricsParser"/> for the header
/// tags and note beats/pitches/text it produced. Pure: no I/O.
/// </summary>
public sealed class UltraStarLyricsWriter
{
    public string Write(UltraStarSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var sb = new StringBuilder();

        foreach (var header in song.Headers)
            sb.Append('#').Append(header.Key).Append(':').Append(header.Value).Append('\n');

        var voice = 0;
        foreach (var line in song.Lines)
        {
            // A duet's voice change, written once where the voice switches.
            if (line.Voice > 0 && line.Voice != voice)
                sb.Append('P').Append(line.Voice.ToString(CultureInfo.InvariantCulture)).Append('\n');
            voice = line.Voice;

            foreach (var note in line.Notes)
            {
                sb.Append(Symbol(note.Type)).Append(' ')
                    .Append(note.StartBeat.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(note.LengthBeats.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(note.Pitch.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(note.Text).Append('\n');
            }

            if (line.BreakBeat.HasValue)
            {
                sb.Append("- ").Append(line.BreakBeat.Value.ToString(CultureInfo.InvariantCulture));
                if (line.BreakBeat2.HasValue)
                    sb.Append(' ').Append(line.BreakBeat2.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
        }

        sb.Append('E').Append('\n');
        return sb.ToString();
    }

    private static char Symbol(UltraStarNoteType type) => type switch
    {
        UltraStarNoteType.Normal => ':',
        UltraStarNoteType.Golden => '*',
        UltraStarNoteType.Freestyle => 'F',
        UltraStarNoteType.Rap => 'R',
        UltraStarNoteType.RapGolden => 'G',
        _ => ':'
    };
}
