// RsGameTablesCanonical: a stable text form of the game tables, one block per gear group, so a test can compare tables by SHA-256 without holding their numbers.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     The canonical text of the tables built from a Rocksmith install
///     (<see cref="RsGameTables" />), one block per group. The repository keeps only
///     each block's SHA-256 (<c>LocalRsGameTablesTest</c>): enough to prove a build
///     from the player's own install reproduces what the rig was tuned against,
///     without a single game number in the tree. Curve endpoints are printed to four
///     significant figures in the parameter's own unit, the precision the curves were
///     first transcribed at; the amp rows are exact.
/// </summary>
internal static class RsGameTablesCanonical
{
    /// <summary>The group names, in their canonical order.</summary>
    public static readonly string[] Groups =
        ["amps", "studioeq", "compression", "lofi", "bassemulator", "bitcruncher", "bassoverdrive", "acoustic", "chorus"];

    /// <summary>Each group's canonical text; a group the tables lack is left out.</summary>
    public static IReadOnlyDictionary<string, string> Texts(RsGameTables tables)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tables.AmpModels.Count > 0) d["amps"] = Amps(tables.AmpModels);
        var c = tables.Curves;
        if (c.StudioEq is { } eq)
        {
            d["studioeq"] = Curves(("BassFreqHz", eq.BassFreqHz), ("LoMidFreqHz", eq.LoMidFreqHz), ("HiMidFreqHz", eq.HiMidFreqHz),
                ("TrebleFreqHz", eq.TrebleFreqHz), ("Q", eq.Q));
        }

        if (c.Compression is { } comp)
        {
            d["compression"] = Curves(("ThresholdDb", comp.ThresholdDb), ("Ratio", comp.Ratio), ("OutputGainDb", comp.OutputGainDb),
                ("AttackMs", comp.AttackMs), ("ReleaseMs", comp.ReleaseMs));
        }

        if (c.LoFi is { } lofi) d["lofi"] = Curves(("LoCutHz", lofi.LoCutHz), ("HiCutHz", lofi.HiCutHz));
        if (c.BassEmulator is { } be) d["bassemulator"] = Curves(("BodyDb", be.BodyDb), ("ToneHz", be.ToneHz));
        if (c.BitCruncher is { } bc)
        {
            d["bitcruncher"] = Curves(("FilterPct", bc.FilterPct), ("RectifyPct", bc.RectifyPct), ("MixPct", bc.MixPct))
                               + Constants(("InputDb", bc.InputDb), ("OutputDb", bc.OutputDb), ("DrivePct", bc.DrivePct),
                                   ("IntensityPct", bc.IntensityPct));
        }

        if (c.BassOverdrive is { } od)
        {
            d["bassoverdrive"] = Curves(("DrivePct", od.DrivePct), ("WetPct", od.WetPct), ("OutputDb", od.OutputDb),
                ("TrebleDb", od.TrebleDb));
        }

        if (c.Acoustic is { } ac) d["acoustic"] = Curves(("MidGainDb", ac.MidGainDb), ("MidFreqHz", ac.MidFreqHz));
        if (c.Choruses.Count > 0) d["chorus"] = Choruses(c.Choruses.Values);
        return d;
    }

    /// <summary>The amp rows, in table order, every field exact.</summary>
    public static string Amps(IEnumerable<RsAmpModel> models)
    {
        var sb = new StringBuilder();
        foreach (var m in models)
        {
            sb.Append(CultureInfo.InvariantCulture, $"amp {m.Key}|{m.Name}|{m.Bank}|{m.Slots}|{m.MacSwaps}\n");
            foreach (var k in m.Knobs)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $" knob {k.Key}|{R(k.Min)}|{R(k.Max)}|{R(k.Default)}|{k.Discrete}|{(int)k.Stages}|{k.UnpublishedShape}\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>Named curves, one line each.</summary>
    public static string Curves(params (string Name, WwiseCurve Curve)[] curves)
    {
        var sb = new StringBuilder();
        foreach (var (name, c) in curves) sb.Append(CurveLine(name, c)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Named constants, one line each.</summary>
    public static string Constants(params (string Name, float Value)[] constants)
    {
        var sb = new StringBuilder();
        foreach (var (name, v) in constants) sb.Append(CultureInfo.InvariantCulture, $"const {name}|{G4(v)}\n");
        return sb.ToString();
    }

    /// <summary>The chorus gear, by key.</summary>
    public static string Choruses(IEnumerable<RsFlangerChorus> choruses)
    {
        var sb = new StringBuilder();
        foreach (var c in choruses.OrderBy(c => c.GearKey, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"chorus {c.GearKey}|{G4(c.DelayMs)}|{c.Voices}|{G4(c.DryLevel)}|{G4(c.FeedForward)}\n");
            sb.Append(CurveLine(" Mix", c.MixPercent)).Append('\n');
            sb.Append(CurveLine(" Depth", c.DepthPercent)).Append('\n');
            sb.Append(CurveLine(" Rate", c.RateHz)).Append('\n');
            if (c.DelayKnobMs is { } delay) sb.Append(CurveLine(" Delay", delay)).Append('\n');
            if (c.StereoDegrees is { } stereo) sb.Append(CurveLine(" Stereo", stereo)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="text" /> (UTF-8).</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string CurveLine(string name, WwiseCurve c)
        => string.Create(CultureInfo.InvariantCulture,
            $"curve {name}|{G4(c.X0)}|{G4(c.Evaluate(c.X0))}|{G4(c.X1)}|{G4(c.Evaluate(c.X1))}|{(int)c.Shape}|{(int)c.Scaling}");

    private static string R(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    // +0.0 for -0.0: a sign bit is not a difference between two tables.
    private static string G4(float v) => (v == 0f ? 0f : v).ToString("G4", CultureInfo.InvariantCulture);
}
