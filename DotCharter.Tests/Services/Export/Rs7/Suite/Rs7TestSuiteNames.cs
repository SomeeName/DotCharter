namespace DotCharter.Tests.Services.Export.Rs7.Suite;

/// <summary>
///     The tuning name rs7string should show, ported from rs7string 4696d1f
///     <c>src/features/tuner/tuningname.cpp</c> (<c>Name7</c>, <c>Name8</c>, <c>NameBass</c>) and the default
///     <c>[tuner] name_suffix=7Str</c> (the digit becomes the string count). Offsets are in package order.
/// </summary>
internal static class Rs7TuningNames
{
    private static readonly (int K, string Std, string Drop)[] Families7 =
    [
        (1, "C Standard", "C Drop Bb"), (0, "B Standard", "Drop A"), (-1, "Bb Standard", "Bb Drop Ab"),
        (-2, "A Standard", "A Drop G"), (-3, "Ab Standard", "Ab Drop Gb"), (-4, "G Standard", "G Drop F"),
        (-5, "Gb Standard", "Gb Drop E"), (-6, "F Standard", "F Drop Eb"), (-7, "E Standard", "E Drop D"),
        (-8, "Eb Standard", "Eb Drop Db"), (-9, "D Standard", "D Drop C")
    ];

    private static readonly (int K, string Std, string Drop)[] Families8 =
    [
        (1, "G Standard", "G Drop F"), (0, "F# Standard", "Drop E"), (-1, "F Standard", "F Drop Eb"),
        (-2, "E Standard", "E Drop D"), (-3, "Eb Standard", "Eb Drop Db"), (-4, "D Standard", "D Drop C"),
        (-5, "Db Standard", "Db Drop B"), (-6, "C Standard", "C Drop Bb"), (-7, "B Standard", "B Drop A"),
        (-8, "Bb Standard", "Bb Drop Ab"), (-9, "A Standard", "A Drop G")
    ];

    private static readonly (int[] T, string Name)[] Alternates7 =
    [
        ([-2, 0, 0, 0, 0, 0, -2], "Extended Drop D"), ([-4, -2, -2, -2, -2, -2, -4], "Extended Drop C"),
        ([-5, -3, -3, -3, -3, -3, -5], "Extended Drop B"), ([5, 5, 5, 4, 3, -2, 3], "Double Drop D"),
        ([-2, -2, -3, -5, -6, -9, -4], "Open G7")
    ];

    private static readonly string[] Notes = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    /// <summary>The expected on-screen name, or an "unknown" line naming the rule that was looked for.</summary>
    public static string Expected(Part p)
    {
        var inst = p.Inst;
        if (!inst.Extended)
            return "not renamed by rs7string (the game's own stock name; this is a control)";
        var name = inst.Bass ? Bass(p.Tuning, inst.Strings) : inst.Strings == 7 ? Name7(p.Tuning) : Name8(p.Tuning);
        if (name is null)
        {
            return "unknown: no rs7string name family matches (rule looked for: tuningname.cpp " +
                   (inst.Bass ? "NameBass" : inst.Strings == 7 ? "Name7" : "Name8") +
                   " needs every stock string at one offset k and the extra string(s) at k, k-2 or k-3..5, or an " +
                   "exact alternate); the game shows its own name or CUSTOM TUNING with the " + inst.Strings + "Str mark";
        }

        return $"{name} {inst.Strings}Str";
    }

    private static string? Name7(int[] t)
    {
        foreach (var (a, name) in Alternates7)
        {
            if (a.SequenceEqual(t)) return name;
        }

        return t.Skip(1).Take(5).All(x => x == t[0]) ? Family(Families7, 11, t[0], t[0] - t[6]) : null;
    }

    private static string? Name8(int[] t) =>
        t.Skip(1).Take(6).All(x => x == t[0]) ? Family(Families8, 6, t[0], t[0] - t[7]) : null;

    private static string? Bass(int[] t, int strings)
    {
        var k = t[0];
        for (var s = 1; s < strings; s++)
        {
            if (s != 4 && t[s] != k) return null;
        }

        return Name7([k, k, k, k, k, k, t[4]]);
    }

    private static string? Family((int K, string Std, string Drop)[] families, int lowPc, int k, int d)
    {
        foreach (var f in families)
        {
            if (f.K != k) continue;
            if (d == 0) return f.Std;
            if (d == 2) return f.Drop;
            if (d is < 3 or > 5) return null;
            var low = Notes[((lowPc + k - d) % 12 + 12) % 12];
            return k == 0 ? "Drop " + low : f.Std[..f.Std.IndexOf(' ')] + " Drop " + low;
        }

        return null;
    }
}
