using System.Globalization;

namespace DotCharter.Presentation.Fakes.Library;

/// <summary>
///     A large library's Tuning popover: every tuning a real scan names (the guitar standards,
///     the drop family down to Drop F, the open tunings, the bass names, letters-only customs)
///     labelled the way <c>LibraryFacetOptions</c> labels them, "Name (letters)", the unknown
///     bucket last, then the capo options the Library's Tuning column shares its popover with.
/// </summary>
internal static class FakeLibraryTunings
{
    /// <summary>The English text for the four strings this popover localizes; a layout test passes the resx ones.</summary>
    internal static string English(string key) => key switch
    {
        "SongBrowser_Facet_Unknown" => "Unknown",
        "SongBrowser_Facet_Capo" => "Capo",
        "Dialog_SongBrowser_CapoNone" => "No capo",
        "SongBrowser_Facet_CapoAt" => "Capo {0}",
        _ => key
    };

    // (described label as the facet prints it, the row's cell label, capo; null = unknown).
    private static readonly (string Described, string Cell, int? Capo)[] Tunings =
    [
        ("5-String Bass Eb Standard (A# D# G# C# F#)", "5-str Eb Std", 0),
        ("5-String Bass Standard (B E A D G)", "5-str Std", 0),
        ("6-String Bass Standard (B E A D G C)", "6-str Std", null),
        ("A Standard (A D G C E A)", "A Std", 0),
        ("B Standard (B E A D F# B)", "B Std", 0),
        ("Bb Standard (A# D# G# C# F A#)", "Bb Std", 0),
        ("C Standard (C F A# D# G C)", "C Std", 0),
        ("C G C F A C", "C G C F A C", 0),
        ("C# G# C# F# G# C#", "C# G# C# F# G# C#", 0),
        ("D Standard (D G C F A D)", "D Std", 2),
        ("D# G# C# F# A# C#", "D# G# C# F# A# C#", 0),
        ("DADGAD (D A D G A D)", "DADGAD", 0),
        ("Db Standard (C# F# B E G# C#)", "C# Std", 3),
        ("Double Drop D (D A D G B D)", "Double Drop D", 0),
        ("Drop A (A E A D F# B)", "Drop A", 0),
        ("Drop A# (A# F A# D# G C)", "Drop A#", 0),
        ("Drop B (B F# B E G# C#)", "Drop B", 0),
        ("Drop C (C G C F A D)", "Drop C", 0),
        ("Drop C# (C# G# C# F# A# D#)", "Drop C#", 0),
        ("Drop D (D A D G B E)", "Drop D", 1),
        ("Drop F (F C F A# D G)", "Drop F", 0),
        ("Drop G# (G# D# G# C# F A#)", "Drop G#", 0),
        ("E Standard", "", 4),
        ("E Standard (E A D G B E)", "", 0),
        ("Eb Standard (D# G# C# F# A# D#)", "Eb Std", 0),
        // Stress row: no name resolves, so the label is the summary as written, and this one is
        // wider than the popover may grow; it is what makes the trim and its tooltip testable.
        ("Eb2 Ab2 Db3 Gb3 Bb3 Eb4 (as written in the archive's custom tuning field)", "Custom", 0),
        ("F Standard (F A# D# G# C F)", "F Std", 0),
        ("Open D (D A D F# A D)", "Open D", 0),
        ("Open D Minor (D A D F A D)", "Open D Minor", 5),
        ("Open E (E B E G# B E)", "Open E", 7),
        ("Open G (D G D G B D)", "Open G", 0),
        ("", "", 0)
    ];

    internal static IReadOnlyList<FakeLibraryRow> Rows() =>
    [
        .. Tunings.Select((t, i) => new FakeLibraryRow
        {
            Artist = $"Artist {i + 1:00}", Title = $"Song in {(t.Described.Length > 0 ? t.Described : "an unknown tuning")}",
            Album = "Tuning sampler", YearLabel = (1970 + i).ToString(CultureInfo.InvariantCulture), LengthLabel = $"{3 + (i % 4)}:{i % 60:00}",
            TuningLabel = t.Cell, TuningNeedsAttention = t.Cell.Length > 0, TuningTip = t.Described, TuningOption = t.Described, Capo = t.Capo
        })
    ];

    /// <summary>The tuning options (unknown last), then the capo options prefixed with the capo caption.</summary>
    internal static IReadOnlyList<FakeLibraryFilterOption> Options(IReadOnlyList<FakeLibraryRow> rows, Func<string, string> text)
    {
        var options = rows.Select(r => r.TuningOption).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new FakeLibraryFilterOption(t, r => r.TuningOption == t))
            .ToList();
        if (rows.Any(r => r.TuningOption.Length == 0))
            options.Add(new FakeLibraryFilterOption(text("SongBrowser_Facet_Unknown"), r => r.TuningOption.Length == 0));

        var prefix = text("SongBrowser_Facet_Capo");
        foreach (var capo in rows.Where(r => r.Capo != null).Select(r => r.Capo!.Value).Distinct().Order())
        {
            var label = capo == 0
                ? text("Dialog_SongBrowser_CapoNone")
                : string.Format(CultureInfo.CurrentCulture, text("SongBrowser_Facet_CapoAt"), capo);
            options.Add(new FakeLibraryFilterOption($"{prefix} · {label}", r => r.Capo == capo));
        }

        if (rows.Any(r => r.Capo == null))
            options.Add(new FakeLibraryFilterOption($"{prefix} · {text("SongBrowser_Facet_Unknown")}", r => r.Capo == null));
        return options;
    }
}
