using System.Globalization;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Parsing.Lrc;

/// <summary>
/// Parses classic line-timed and enhanced word-timed <c>.lrc</c> lyric files
/// into DotCharter's vocal phrase model.
/// </summary>
public sealed class LrcLyricsParser
{
    private const double DefaultLineDurationMs = 1000.0;
    private const double DefaultTokenDurationMs = 250.0;
    private static readonly Regex TimestampRegex = new(@"\[(?<stamp>\d+:\d{1,2}(?:[.:]\d{1,3})?)\]",
        RegexOptions.Compiled);
    private static readonly Regex OffsetRegex = new(@"\[offset:(?<value>[+-]?\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnhancedTimestampRegex = new(@"<(?<stamp>\d+:\d{1,2}(?:[.:]\d{1,3})?)>",
        RegexOptions.Compiled);

    public IReadOnlyList<LyricPhrase> Parse(string? content, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        if (string.IsNullOrWhiteSpace(content) || beats.Count == 0)
            return Array.Empty<LyricPhrase>();

        var normalized = content.Replace("\r\n", "\n");
        var offsetMs = ParseOffsetMs(normalized);
        var lines = ParseTimestampedLines(normalized, offsetMs)
            .OrderBy(l => l.TimeMs)
            .ThenBy(l => l.SourceLineIndex)
            .ToList();

        var phrases = new List<LyricPhrase>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var nextTimeMs = i + 1 < lines.Count ? lines[i + 1].TimeMs : (double?)null;
            var phrase = BuildPhrase(line, nextTimeMs, offsetMs, beats);
            if (phrase != null)
                phrases.Add(phrase);
        }

        return phrases;
    }

    /// <summary>
    ///     The <c>[offset:]</c> tag, in milliseconds. <b>A positive value makes the
    ///     lyrics appear SOONER</b>: the effective time is <c>stamp - offset</c>.
    ///     <para>
    ///         That is the format's own definition (the LRC write-ups: "+ causing
    ///         lyrics to appear sooner") and what the players that honour the tag
    ///         do — foo_openlyrics returns <c>timestamp - timestamp_offset</c>, and
    ///         LyricsX looks a line up at <c>position + offset</c>. The parser used
    ///         to ADD it, so a file timed for a player imported twice the offset
    ///         away from where it plays.
    ///     </para>
    ///     <para>
    ///         It applies to EVERY stamp — the <c>[mm:ss.xx]</c> line stamps and the
    ///         <c>&lt;mm:ss.xx&gt;</c> word stamps of an enhanced line alike — or the
    ///         words of a shifted line do not sit under it.
    ///         <see cref="LrcLyricsWriter" /> never writes the tag.
    ///     </para>
    /// </summary>
    private static int ParseOffsetMs(string content)
    {
        var match = OffsetRegex.Match(content);
        return match.Success && int.TryParse(match.Groups["value"].Value, CultureInfo.InvariantCulture, out var offsetMs) ? offsetMs : 0;
    }

    private static List<TimestampedLine> ParseTimestampedLines(string content, int offsetMs)
    {
        var result = new List<TimestampedLine>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i].Trim();
            if (rawLine.Length == 0) continue;

            var timestamps = TimestampRegex.Matches(rawLine)
                .Select(m => TryParseTimestampMs(m.Groups["stamp"].Value))
                .Where(ms => ms.HasValue)
                .Select(ms => ms!.Value - offsetMs)
                .Where(ms => ms >= 0)
                .ToList();

            if (timestamps.Count == 0) continue;

            var lyricText = TimestampRegex.Replace(rawLine, "").Trim();
            if (lyricText.Length == 0) continue;

            foreach (var timeMs in timestamps)
                result.Add(new TimestampedLine(timeMs, lyricText, i));
        }

        return result;
    }

    private static LyricPhrase? BuildPhrase(TimestampedLine line, double? nextLineTimeMs, int offsetMs,
        IList<BeatMarker> beats)
    {
        var enhancedTokens = ParseEnhancedTokens(line.Text, offsetMs);
        if (enhancedTokens.Count > 0)
            return BuildEnhancedPhrase(line.TimeMs, line.Text, enhancedTokens, nextLineTimeMs, beats);

        var simpleTokens = TokenizeText(line.Text);
        if (simpleTokens.Count == 0) return null;

        var lineEndMs = nextLineTimeMs.HasValue && nextLineTimeMs.Value > line.TimeMs
            ? nextLineTimeMs.Value
            : line.TimeMs + Math.Max(DefaultLineDurationMs, simpleTokens.Count * DefaultTokenDurationMs);

        var phrase = new LyricPhrase
        {
            Position = TimelineMath.PositionFromMs(line.TimeMs, beats),
            EndPosition = TimelineMath.PositionFromMs(lineEndMs, beats)
        };

        var spanMs = Math.Max(1.0, lineEndMs - line.TimeMs);
        for (var i = 0; i < simpleTokens.Count; i++)
        {
            var startMs = line.TimeMs + spanMs * i / simpleTokens.Count;
            var endMs = line.TimeMs + spanMs * (i + 1) / simpleTokens.Count;
            phrase.Lyrics.Add(new LyricEvent
            {
                Text = simpleTokens[i].Text,
                IsWordPart = simpleTokens[i].IsWordPart,
                IsPhraseEnd = i == simpleTokens.Count - 1,
                Position = TimelineMath.PositionFromMs(startMs, beats),
                EndPosition = TimelineMath.PositionFromMs(endMs, beats)
            });
        }

        return phrase;
    }

    private static LyricPhrase? BuildEnhancedPhrase(
        double lineTimeMs,
        string lineText,
        IReadOnlyList<TimedToken> enhancedTokens,
        double? nextLineTimeMs,
        IList<BeatMarker> beats)
    {
        var lineEndMs = nextLineTimeMs.HasValue && nextLineTimeMs.Value > lineTimeMs
            ? nextLineTimeMs.Value
            : Math.Max(lineTimeMs + Math.Max(DefaultLineDurationMs, TokenizeText(lineText).Count * DefaultTokenDurationMs),
                enhancedTokens[^1].StartMs + DefaultTokenDurationMs);

        if (enhancedTokens.Count == 0) return null;

        var phrase = new LyricPhrase
        {
            Position = TimelineMath.PositionFromMs(Math.Min(lineTimeMs, enhancedTokens[0].StartMs), beats),
            EndPosition = TimelineMath.PositionFromMs(lineEndMs, beats)
        };

        for (var i = 0; i < enhancedTokens.Count; i++)
        {
            var token = enhancedTokens[i];
            var startMs = Math.Max(lineTimeMs, token.StartMs);
            var endMs = i + 1 < enhancedTokens.Count
                ? Math.Max(startMs, enhancedTokens[i + 1].StartMs)
                : Math.Max(startMs + 1.0, lineEndMs);

            phrase.Lyrics.Add(new LyricEvent
            {
                Text = token.Text,
                IsWordPart = token.IsWordPart,
                IsPhraseEnd = i == enhancedTokens.Count - 1,
                Position = TimelineMath.PositionFromMs(startMs, beats),
                EndPosition = TimelineMath.PositionFromMs(endMs, beats)
            });
        }

        return phrase;
    }

    private static List<TimedToken> ParseEnhancedTokens(string text, int offsetMs)
    {
        var matches = EnhancedTimestampRegex.Matches(text);
        if (matches.Count == 0) return [];

        var result = new List<TimedToken>();
        for (var i = 0; i < matches.Count; i++)
        {
            var stampMs = TryParseTimestampMs(matches[i].Groups["stamp"].Value);
            if (!stampMs.HasValue) continue;

            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var chunk = text[start..end];
            foreach (var token in TokenizeText(chunk))
                result.Add(new TimedToken(Math.Max(0, stampMs.Value - offsetMs), token.Text, token.IsWordPart));
        }

        return result;
    }

    private static List<TextToken> TokenizeText(string text)
    {
        var result = new List<TextToken>();
        foreach (var word in text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = word.Split('-');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0) continue;
                result.Add(new TextToken(part, i < parts.Length - 1));
            }
        }

        return result;
    }

    private static double? TryParseTimestampMs(string raw)
    {
        var parts = raw.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var minutes)) return null;

        var secondsPart = parts[1];
        var separatorIndex = secondsPart.IndexOfAny(['.', ':']);
        if (separatorIndex < 0)
        {
            if (!int.TryParse(secondsPart, CultureInfo.InvariantCulture, out var wholeSecondsOnly)) return null;
            return minutes * 60000.0 + wholeSecondsOnly * 1000.0;
        }

        var wholeSecondsText = secondsPart[..separatorIndex];
        var fractionText = secondsPart[(separatorIndex + 1)..];
        if (!int.TryParse(wholeSecondsText, CultureInfo.InvariantCulture, out var wholeSeconds)) return null;
        if (!int.TryParse(fractionText, CultureInfo.InvariantCulture, out var fractionValue)) return null;

        var fractionMs = fractionText.Length switch
        {
            1 => fractionValue * 100.0,
            2 => fractionValue * 10.0,
            _ => fractionValue
        };

        return minutes * 60000.0 + wholeSeconds * 1000.0 + fractionMs;
    }

    private readonly record struct TimestampedLine(double TimeMs, string Text, int SourceLineIndex);
    private readonly record struct TextToken(string Text, bool IsWordPart);
    private readonly record struct TimedToken(double StartMs, string Text, bool IsWordPart);
}
