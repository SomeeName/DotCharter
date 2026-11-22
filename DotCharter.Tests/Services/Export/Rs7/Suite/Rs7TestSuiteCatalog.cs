using static DotCharter.Tests.Services.Export.Rs7.Suite.Rs7SuiteContent;
using static DotCharter.Tests.Services.Export.Rs7.Suite.Rs7SuiteSpec;

namespace DotCharter.Tests.Services.Export.Rs7.Suite;

/// <summary>The declarative list of the suite's charts (#307): one <see cref="ChartSpec" /> per package.</summary>
internal static class Rs7TestSuiteCatalog
{
    private static readonly Inst[] Extended = [Inst.B5, Inst.B6, Inst.G7, Inst.G8];

    public static IReadOnlyList<ChartSpec> All { get; } = [.. Controls(), .. Tunings(), .. Content(), .. Multi()];

    private static Part Tour(Inst inst, int[] tuning) => new(inst, tuning, Rs7SuiteContent.Tour(inst));

    private static ChartSpec One(string name, string tests, Part part) => new(name, tests, [part]);

    private static IEnumerable<ChartSpec> Controls()
    {
        yield return One("g6_control_standard", "Control: 6-string guitar, E standard, all strings and the chord ladder. No RS7X block, no rs7string log line.",
            new Part(Inst.G6, Zero(Inst.G6), ChordLadder(Singles(new Seq(), Inst.G6, AllFrets), Inst.G6).Done()));
        yield return One("b4_control_standard", "Control: 4-string bass, E standard, all strings and the chord ladder. No RS7X block, no rs7string log line.",
            new Part(Inst.B4, Zero(Inst.B4), ChordLadder(Singles(new Seq(), Inst.B4, AllFrets), Inst.B4).Done()));
    }

    private static IEnumerable<ChartSpec> Tunings()
    {
        var (b5, b6, g7, g8) = (Inst.B5, Inst.B6, Inst.G7, Inst.G8);
        yield return One("b5_b_standard", "5-string bass, B standard: tour of every string, chords with the low B in columns 4/5.", Tour(b5, Zero(b5)));
        yield return One("b5_drop_a", "5-string bass, low B dropped to A (Drop A).", Tour(b5, With(b5, 0, 4, -2)));
        yield return One("b5_a_standard_step_down", "5-string bass one step down = A standard (low B = A0, MIDI 21).", Tour(b5, All(b5, -2)));
        yield return One("b5_drop_g_lowest", "5-string bass, Drop G: the lowest allowed pitch, G0 (MIDI 19).", Tour(b5, With(b5, 0, 4, -4)));
        yield return One("b5_custom", "5-string bass, custom tuning (no rs7string name).", Tour(b5, [0, -1, 0, 0, -3]));
        yield return One("b6_b_standard", "6-string bass, B standard with the high C.", Tour(b6, Zero(b6)));
        yield return One("b6_drop_a", "6-string bass, low B dropped to A.", Tour(b6, With(b6, 0, 4, -2)));
        yield return One("b6_a_standard_step_down", "6-string bass one step down = A standard.", Tour(b6, All(b6, -2)));
        yield return One("b6_custom", "6-string bass, custom tuning.", Tour(b6, [0, 0, -2, 0, -3, 1]));
        yield return One("g7_b_standard", "7-string guitar, B standard.", Tour(g7, Zero(g7)));
        yield return One("g7_drop_a", "7-string guitar, Drop A (low B to A).", Tour(g7, With(g7, 0, 6, -2)));
        yield return One("g7_a_standard_step_down", "7-string guitar one step down = A standard.", Tour(g7, All(g7, -2)));
        yield return One("g7_extended_drop_d", "7-string guitar, Extended Drop D (a named alternate tuning).", Tour(g7, [-2, 0, 0, 0, 0, 0, -2]));
        yield return One("g7_custom", "7-string guitar, custom tuning.", Tour(g7, [0, 0, -1, 0, 0, 0, -3]));
        yield return One("g8_fsharp_standard", "8-string guitar, F# standard.", Tour(g8, Zero(g8)));
        yield return One("g8_drop_e", "8-string guitar, Drop E (low F# to E).", Tour(g8, With(g8, 0, 7, -2)));
        yield return One("g8_e_standard_step_down", "8-string guitar one step down = E standard.", Tour(g8, All(g8, -2)));
        yield return One("g8_b_standard_lowest", "8-string guitar tuned down to B0 (MIDI 23), the lowest 8-string tuning.", Tour(g8, All(g8, -7)));
        yield return One("g8_custom", "8-string guitar, custom tuning.", Tour(g8, [-1, 0, -2, 0, 0, -1, 0, -3]));
    }

    private static string ExtraName(Inst inst, int s) =>
        inst.Bass ? (s == 4 ? "low_b" : "high_c") : inst.Strings == 7 ? "low_b" : s == 6 ? "low_b" : "low_fsharp";

    private static IEnumerable<ChartSpec> Content()
    {
        foreach (var inst in Extended)
        {
            var z = Zero(inst);
            var k = inst.Key;
            yield return One($"{k}_all_strings_all_frets", $"Single notes on every string of the {k}: open, low frets and frets 12, 19, 24.",
                new Part(inst, z, Singles(new Seq(), inst, AllFrets).Done()));
            yield return One($"{k}_chords", $"{k} chords: open, fretted, double stop, extra string(s) plus one normal string, full chord, finger numbers, a chord repeated four times, stock-only chord, then all barred up.",
                new Part(inst, z, ChordLadder(new Seq(), inst).Done()));
            foreach (var extra in inst.Extras)
            {
                yield return One($"{k}_techniques_alone_{ExtraName(inst, extra)}",
                    $"Every technique as a single note on package string {extra} and on a normal string.",
                    new Part(inst, z, TechniquesAlone(inst, extra)));
            }

            yield return One($"{k}_techniques_in_chords", $"Every technique inside a chord with the extra string(s) and inside a stock-only chord{(inst.Bass ? " (bass: the low B / high C are chord columns 4/5)" : "")}.",
                new Part(inst, z, TechniquesInChords(inst)));
            var (events, sections) = Sectioned(inst);
            yield return One($"{k}_sections_levels", $"{k}: five sections (intro and outro without extra-string notes) and three difficulty levels.",
                new Part(inst, z, events, sections, 3));
        }
    }

    private static Part Short(Inst inst, ArrangementType role, int[]? tuning = null) =>
        new(inst, role, tuning ?? Zero(inst), Rs7SuiteContent.Short(inst));

    private static IEnumerable<ChartSpec> Multi()
    {
        var (lead, rhythm, bass) = (ArrangementType.Lead, ArrangementType.Rhythm, ArrangementType.Bass);
        var five = new[]
        {
            Short(Inst.G8, lead), Short(Inst.G7, rhythm), Short(Inst.B5, bass), Short(Inst.B6, bass), Short(Inst.G6, lead)
        };
        var eight = new[]
        {
            Short(Inst.G7, lead), Short(Inst.G8, lead), Short(Inst.G7, rhythm), Short(Inst.G8, rhythm),
            Short(Inst.B5, bass), Short(Inst.B6, bass), Short(Inst.G6, lead), Short(Inst.B4, bass)
        };
        yield return new ChartSpec("multi_5_arrangements", "One song, 5 arrangements: 8-, 7- and 6-string guitar, 5- and 6-string bass.", five);
        yield return new ChartSpec("multi_8_arrangements", "One song, 8 arrangements mixing guitar and bass, extended and stock.", eight);
        yield return new ChartSpec("multi_10_arrangements", "One song, 10 arrangements (more than 8), guitar and bass, two extra tunings.",
            [.. eight, Short(Inst.G7, lead, With(Inst.G7, 0, 6, -2)), Short(Inst.G8, rhythm, With(Inst.G8, 0, 7, -2))]);
        yield return new ChartSpec("coop_g7_b5", "Co-op pair 7-string guitar + 5-string bass.", [Short(Inst.G7, lead), Short(Inst.B5, bass)]);
        yield return new ChartSpec("coop_g8_b6", "Co-op pair 8-string guitar + 6-string bass.", [Short(Inst.G8, lead), Short(Inst.B6, bass)]);
        yield return new ChartSpec("coop_g6_b4", "Co-op pair 6-string guitar + 4-string bass (controls).", [Short(Inst.G6, lead), Short(Inst.B4, bass)]);
    }
}
