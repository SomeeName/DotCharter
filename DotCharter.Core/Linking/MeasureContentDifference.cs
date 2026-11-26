using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Linking;

/// <summary>What kind of content two runs of measures first differ in.</summary>
public enum MeasureContentDifferenceKind
{
    /// <summary>Time signature or beat count of a measure.</summary>
    Measures,

    /// <summary>A note or chord: onset, string, fret, technique or shape.</summary>
    Sound,

    /// <summary>The same note or chord with a different sustain.</summary>
    Sustain,

    /// <summary>The fret-hand position (anchor) in effect.</summary>
    FretHandPosition,

    /// <summary>A hand shape.</summary>
    HandShape,

    /// <summary>A fret-hand mute.</summary>
    FretHandMute,

    /// <summary>Anything else the key compares (settings, stored notation detail).</summary>
    Other
}

/// <summary>
///     The first place two <see cref="MeasureContentKey" />s differ, in charter terms.
///     <c>Measure</c> is 0-based from the run's first measure; <c>Beat</c> is the beat inside that
///     measure (0-based, with its fraction) or null when the difference is the measure itself.
///     <c>Reference</c> and <c>Other</c> hold the two values (a fret, "-" for none, or a sustain
///     token); <c>SustainChange</c> is <c>Other - Reference</c> in WHOLE NOTES (1/16 = a sixteenth)
///     when both sustains have a length, null when one is held to the next note.
/// </summary>
public sealed record MeasureContentDifference(
    MeasureContentDifferenceKind Kind,
    int Measure,
    Fraction? Beat,
    string Reference,
    string Other)
{
    public Fraction? SustainChange { get; init; }

    private static readonly Regex SustainPattern = new(@"~(~|L?\d+(?:\+\d+/\d+)?)", RegexOptions.CultureInvariant);

    /// <summary>
    ///     The first difference between <paramref name="reference" /> and <paramref name="other" />,
    ///     null when they are equal. This reads the canonical text <see cref="MeasureContentIndex" />
    ///     writes (the one place outside it that does; both live in this assembly and change together).
    /// </summary>
    public static MeasureContentDifference? Between(MeasureContentKey reference, MeasureContentKey other)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(other);
        if (reference.Equals(other)) return null;
        var a = Parsed.From(reference.Canonical);
        var b = Parsed.From(other.Canonical);
        if (a.Header != b.Header) return new MeasureContentDifference(MeasureContentDifferenceKind.Other, 0, null, a.Header, b.Header);
        for (var m = 0; m < Math.Max(a.Measures.Count, b.Measures.Count); m++)
        {
            var left = m < a.Measures.Count ? a.Measures[m].Text : "-";
            var right = m < b.Measures.Count ? b.Measures[m].Text : "-";
            if (left != right) return new MeasureContentDifference(MeasureContentDifferenceKind.Measures, m, null, left, right);
        }

        return new[]
            {
                First(a, a.Sounds, b.Sounds, SoundDifference),
                First(a, a.Anchors, b.Anchors, AnchorDifference),
                First(a, a.Shapes, b.Shapes, (x, y) => (MeasureContentDifferenceKind.HandShape, x?.Text ?? "-", y?.Text ?? "-", null)),
                First(a, a.Mutes, b.Mutes, (x, y) => (MeasureContentDifferenceKind.FretHandMute, x?.Text ?? "-", y?.Text ?? "-", null)),
                First(a, a.Rest, b.Rest, (x, y) => (MeasureContentDifferenceKind.Other, x?.Text ?? "-", y?.Text ?? "-", null))
            }
            .OfType<(Fraction At, MeasureContentDifference Difference)>()
            .OrderBy(d => d.At)
            .Select(d => d.Difference)
            .FirstOrDefault() ?? new MeasureContentDifference(MeasureContentDifferenceKind.Other, 0, null, "", "");
    }

    private static (Fraction At, MeasureContentDifference Difference)? First(Parsed layout, List<Token> a, List<Token> b,
        Func<Token?, Token?, (MeasureContentDifferenceKind Kind, string Reference, string Other, Fraction? Change)> describe)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : null;
            var y = i < b.Count ? b[i] : null;
            if (x?.Text == y?.Text) continue;
            var at = x == null ? y!.At : y == null ? x.At : x.At < y.At ? x.At : y.At;
            var (kind, reference, other, change) = describe(x, y);
            var (measure, beat) = layout.Locate(at);
            var unit = measure < layout.Measures.Count ? layout.Measures[measure].Denominator : 4;
            return (at, new MeasureContentDifference(kind, measure, beat, reference, other)
            {
                SustainChange = change is { } beats ? beats / new Fraction(unit, 1) : null
            });
        }

        return null;
    }

    private static (MeasureContentDifferenceKind, string, string, Fraction?) SoundDifference(Token? x, Token? y)
    {
        if (x == null || y == null || SustainPattern.Replace(x.Text, "~") != SustainPattern.Replace(y.Text, "~"))
            return (MeasureContentDifferenceKind.Sound, x?.Text ?? "-", y?.Text ?? "-", null);
        var left = SustainPattern.Matches(x.Text);
        var right = SustainPattern.Matches(y.Text);
        for (var i = 0; i < left.Count; i++)
        {
            var l = left[i].Groups[1].Value;
            var r = right[i].Groups[1].Value;
            if (l == r) continue;
            Fraction? change = TryBeats(l, out var lb) && TryBeats(r, out var rb) ? rb - lb : null;
            return (MeasureContentDifferenceKind.Sustain, l, r, change);
        }

        return (MeasureContentDifferenceKind.Sound, x.Text, y.Text, null);
    }

    private static (MeasureContentDifferenceKind, string, string, Fraction?) AnchorDifference(Token? x, Token? y) =>
        (MeasureContentDifferenceKind.FretHandPosition, Fret(x), Fret(y), null);

    /// <summary>"12" from <c>A0:12/4</c>, with the width when the frets match is not the point: "-" for none.</summary>
    private static string Fret(Token? anchor)
    {
        if (anchor == null) return "-";
        var state = anchor.Text[(anchor.Text.IndexOf(':', StringComparison.Ordinal) + 1)..];
        return state.Split('/')[0];
    }

    /// <summary>Beats as written by <c>MeasureContentIndex.Rel</c>: "3" or "3+1/4".</summary>
    private static bool TryBeats(string text, out Fraction beats)
    {
        beats = Fraction.Zero;
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (!int.TryParse(plus < 0 ? text : text[..plus], NumberStyles.None, CultureInfo.InvariantCulture, out var whole)) return false;
        if (plus < 0)
        {
            beats = whole;
            return true;
        }

        if (!Fraction.TryParse(text[(plus + 1)..], out var part)) return false;
        beats = part + whole;
        return true;
    }

    private sealed record Token(Fraction At, string Text);

    private sealed record MeasureToken(string Text, int BeatCount, int Denominator);

    /// <summary>The canonical text split into its parts, each with its onset in beats from the run start.</summary>
    private sealed class Parsed
    {
        public string Header { get; private init; } = "";
        public List<MeasureToken> Measures { get; } = [];
        public List<Token> Sounds { get; } = [];
        public List<Token> Anchors { get; } = [];
        public List<Token> Shapes { get; } = [];
        public List<Token> Mutes { get; } = [];
        public List<Token> Rest { get; } = [];

        public static Parsed From(string canonical)
        {
            var parts = canonical.Split('|');
            var parsed = new Parsed { Header = parts[0] };
            foreach (var part in parts.Skip(1))
            {
                if (part.Length == 0) continue;
                var body = part[0] == 'V' ? part.TrimStart('V', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9') : part;
                if (part[0] == 't' && Measure(part) is { } measure)
                {
                    parsed.Measures.Add(measure);
                    continue;
                }

                var list = body.Length == 0 ? parsed.Rest : body[0] switch
                {
                    'N' or 'C' => parsed.Sounds,
                    'A' => parsed.Anchors,
                    'H' => parsed.Shapes,
                    'M' => parsed.Mutes,
                    _ => parsed.Rest
                };
                list.Add(new Token(Onset(body), part));
            }

            return parsed;
        }

        /// <summary>The 0-based measure and beat inside it of <paramref name="beats" /> from the run start.</summary>
        public (int Measure, Fraction Beat) Locate(Fraction beats)
        {
            var start = Fraction.Zero;
            for (var m = 0; m < Measures.Count; m++)
            {
                var end = start + Measures[m].BeatCount;
                if (beats < end || m == Measures.Count - 1) return (m, beats - start);
                start = end;
            }

            return (0, beats);
        }

        private static MeasureToken? Measure(string token)
        {
            var x = token.IndexOf('x', StringComparison.Ordinal);
            var slash = token.IndexOf('/', StringComparison.Ordinal);
            if (x < 0 || slash < 0 || slash > x) return null;
            return int.TryParse(token.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var beats) &&
                int.TryParse(token.AsSpan(slash + 1, x - slash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
                ? new MeasureToken(token, beats, denominator)
                : null;
        }

        private static Fraction Onset(string body)
        {
            if (body.Length < 2) return Fraction.Zero;
            var end = body.IndexOfAny([':', '-'], 1);
            return TryBeats(end < 0 ? body[1..] : body[1..end], out var beats) ? beats : Fraction.Zero;
        }
    }
}
