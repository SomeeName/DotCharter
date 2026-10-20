using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     An <see cref="Arrangement" />'s tuning as a player would say it — "Drop D",
///     "Eb Standard" — falling back to the raw note letters when the tuning has no
///     known name.
///     <para>
///         The resolution order is the one <c>PlayModeViewModel.TuningDisplay</c>
///         already uses, and it matters: OFFSETS first, because they are the
///         spelling-proof canonical form and the array's own length is the
///         authoritative string count (4 bass / 6 guitar), then the note letters,
///         then the raw label. Resolving from the letters alone mis-reads a bass
///         (see v0.42.1 — Rocksmith stores every tuning in six slots regardless of
///         instrument, so 122 of the owner's songs were filed under a tuning no
///         guitar plays).
///     </para>
///     <para>
///         A converter rather than a view-model projection because the arrangement
///         pickers bind the DOMAIN objects directly
///         (<c>ChartEditor.Arrangements</c>), and wrapping them for one label
///         would mean keeping a parallel collection in sync with the live one.
///     </para>
/// </summary>
public sealed class ArrangementTuningConverter : IValueConverter
{
    public static readonly ArrangementTuningConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Arrangement arrangement) return string.Empty;

        var text = TextFor(arrangement);

        // Graphite prints a tuning as written ("E Standard"); the only variant
        // is the abbreviated cell a narrow field asks for.
        return (parameter as string) switch
        {
            // "E STD" — the mockup's secondary cell inside a 150 px field, where
            // the primary value already spends most of the room. Abbreviating is
            // the drawing's own call, not a truncation.
            "short" => Shorten(text),
            _ => text
        };
    }

    /// <summary>"E Standard" → "E STD"; anything else is upper-cased whole,
    /// because a named non-standard tuning ("Drop D", "Open G") is already
    /// short and abbreviating it would lose which one it is.</summary>
    // Memoized per arrangement on the values the text is a function of — the
    // pitch offsets and the note letters — compared element-wise on every call,
    // so a changed tuning recomputes and an unchanged one costs no allocation.
    // The converter is bound per row in the song browser and the Play Mode song
    // list, and a virtualized list re-evaluates it on every recycle (LINQ +
    // the resolver + a Join per realized row would be paid each time).
    // The path and the tuning reference are inputs too: they decide whether a
    // guitar-path arrangement is read as a bass.
    private sealed class Memo(int[] offsets, string[] tuning, ArrangementType type, double centOffset, string text)
    {
        public readonly int[] Offsets = offsets;
        public readonly string[] Tuning = tuning;
        public readonly ArrangementType Type = type;
        public readonly double CentOffset = centOffset;
        public readonly string Text = text;

        public bool Matches(Arrangement arrangement)
        {
            if (arrangement.ArrangementType != Type || !arrangement.CentOffset.Equals(CentOffset)) return false;
            var pitches = arrangement.TuningPitches;
            if (pitches.Count != Offsets.Length) return false;
            for (var i = 0; i < Offsets.Length; i++)
            {
                if (pitches[i].Step != Offsets[i])
                    return false;
            }

            var letters = arrangement.Tuning ?? [];
            if (letters.Length != Tuning.Length) return false;
            for (var i = 0; i < Tuning.Length; i++)
            {
                if (!string.Equals(letters[i], Tuning[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Arrangement, Memo> Memos = [];

    internal static string TextFor(Arrangement arrangement)
    {
        if (Memos.TryGetValue(arrangement, out var memo) && memo.Matches(arrangement)) return memo.Text;

        var offsets = arrangement.TuningPitches.Select(p => p.Step).ToArray();
        var name = TuningNameResolver.ResolveFor(arrangement);
        // Last resort is the note letters themselves — 27 of the tunings in the
        // owner's 4,321-song library are deliberately UNNAMED (one-off, or one
        // string away from a named one), so "no name" is an ordinary answer and
        // must not render as an empty row.
        var letters = arrangement.Tuning ?? [];
        var text = name ?? string.Join(' ', letters);

        Memos.AddOrUpdate(arrangement,
            new Memo(offsets, (string[])letters.Clone(), arrangement.ArrangementType, arrangement.CentOffset, text));
        return text;
    }

    private static string Shorten(string text)
    {
        const string standard = "standard";
        var idx = text.IndexOf(standard, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text.ToUpperInvariant();
        return (text[..idx] + "STD").ToUpperInvariant();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Tuning names are display-only.");
}
