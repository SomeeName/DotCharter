// RsGameTablesJson: the on-disk cache form of RsGameTables (versioned, stamped with the install fingerprint it was built from).
using System.Text.Json;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     Writes and reads the cache of <see cref="RsGameTables" /> kept in the user data
///     folder. The file carries <see cref="FormatVersion" /> and the fingerprint of the
///     install files it was built from; <see cref="TryRead" /> refuses a file whose
///     version or fingerprint differs, so a game update or a reader change rebuilds it.
///     Hand-written over <see cref="Utf8JsonWriter" /> / <see cref="JsonDocument" />: no
///     reflection, and floats round-trip exactly.
/// </summary>
internal static class RsGameTablesJson
{
    /// <summary>Bump when the reader or the table shape changes: every cache rebuilds.
    /// 2: the native amp table (<see cref="RsGameTables.NativeAmpTable" />).
    /// 3: the MB Comp's fitted model (<see cref="RsMbCompModel" />).</summary>
    public const int FormatVersion = 3;

    public static byte[] Write(RsGameTables tables, string fingerprint)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FormatVersion);
            w.WriteString("fingerprint", fingerprint);
            w.WriteStartArray("amps");
            foreach (var m in tables.AmpModels)
            {
                w.WriteStartObject();
                w.WriteString("key", m.Key);
                w.WriteString("name", m.Name);
                w.WriteString("bank", m.Bank);
                w.WriteNumber("slots", m.Slots);
                w.WriteNumber("macSwaps", m.MacSwaps);
                w.WriteStartArray("knobs");
                foreach (var k in m.Knobs)
                {
                    w.WriteStartObject();
                    w.WriteString("key", k.Key);
                    w.WriteNumber("min", k.Min);
                    w.WriteNumber("max", k.Max);
                    w.WriteNumber("default", k.Default);
                    w.WriteBoolean("discrete", k.Discrete);
                    w.WriteNumber("stages", (int)k.Stages);
                    w.WriteBoolean("unpublished", k.UnpublishedShape);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (tables.NativeAmpTable is { } native) w.WriteBase64String("nativeAmpTable", native);
            var c = tables.Curves;
            w.WriteStartObject("curves");
            if (c.StudioEq is { } eq) Set(w, "studioEq", eq.BassFreqHz, eq.LoMidFreqHz, eq.HiMidFreqHz, eq.TrebleFreqHz, eq.Q);
            if (c.Compression is { } co) Set(w, "compression", co.ThresholdDb, co.Ratio, co.OutputGainDb, co.AttackMs, co.ReleaseMs);
            if (c.LoFi is { } lf) Set(w, "loFi", lf.LoCutHz, lf.HiCutHz);
            if (c.BassEmulator is { } be) Set(w, "bassEmulator", be.BodyDb, be.ToneHz);
            if (c.BitCruncher is { } bc)
            {
                Set(w, "bitCruncher", bc.FilterPct, bc.RectifyPct, bc.MixPct);
                Floats(w, "bitCruncherBlock", bc.InputDb, bc.OutputDb, bc.DrivePct, bc.IntensityPct);
            }

            if (c.BassOverdrive is { } od) Set(w, "bassOverdrive", od.DrivePct, od.WetPct, od.OutputDb, od.TrebleDb);
            if (c.Acoustic is { } ac) Set(w, "acoustic", ac.MidGainDb, ac.MidFreqHz);
            if (c.MbComp is { } mb)
            {
                Set(w, "mbCompIn", mb.InputDb);
                Set(w, "mbCompOut", mb.OutputDb);
                Set(w, "mbCompAttack", mb.AttackMs);
                Floats(w, "mbCompFit", mb.FixedDb, mb.CutLowDb, mb.ReleaseMs, mb.Rms ? 1f : 0f);
                Floats(w, "mbCompCut", mb.Cut);
            }

            w.WriteStartArray("choruses");
            foreach (var ch in c.Choruses.Values.OrderBy(x => x.GearKey, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("key", ch.GearKey);
                Floats(w, "block", ch.DelayMs, ch.Voices, ch.DryLevel, ch.FeedForward);
                Set(w, "curves", ch.MixPercent, ch.DepthPercent, ch.RateHz);
                if (ch.DelayKnobMs is { } d) Set(w, "delay", d);
                if (ch.StereoDegrees is { } s) Set(w, "stereo", s);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>The tables in <paramref name="json" />, or null when it is not a cache of
    /// this <see cref="FormatVersion" /> built from <paramref name="fingerprint" />.</summary>
    public static RsGameTables? TryRead(ReadOnlySpan<byte> json, string fingerprint)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != FormatVersion) return null;
            if (root.GetProperty("fingerprint").GetString() != fingerprint) return null;

            var amps = new List<RsAmpModel>();
            foreach (var m in root.GetProperty("amps").EnumerateArray())
            {
                var knobs = m.GetProperty("knobs").EnumerateArray().Select(k => new RsAmpKnob(
                    k.GetProperty("key").GetString()!, k.GetProperty("min").GetSingle(), k.GetProperty("max").GetSingle(),
                    k.GetProperty("default").GetSingle(), k.GetProperty("discrete").GetBoolean(),
                    (RsAmpStage)k.GetProperty("stages").GetInt32(), k.GetProperty("unpublished").GetBoolean())).ToList();
                amps.Add(new RsAmpModel(m.GetProperty("key").GetString()!, m.GetProperty("name").GetString()!,
                    m.GetProperty("bank").GetString()!, m.GetProperty("slots").GetInt32(), m.GetProperty("macSwaps").GetInt32(), knobs));
            }

            var nativeAmpTable = root.TryGetProperty("nativeAmpTable", out var n) ? n.GetBytesFromBase64() : null;
            // The index and the native chains are one group (the plugin's game-table builder): a cache
            // holding amps without their chains is not one this version wrote.
            if (amps.Count > 0 && nativeAmpTable is null) return null;
            var c = root.GetProperty("curves");
            var choruses = new Dictionary<string, RsFlangerChorus>(StringComparer.Ordinal);
            foreach (var ch in c.GetProperty("choruses").EnumerateArray())
            {
                var key = ch.GetProperty("key").GetString()!;
                var block = FloatsOf(ch.GetProperty("block"));
                var cv = CurvesOf(ch.GetProperty("curves"));
                choruses[key] = new RsFlangerChorus(key, block[0], (int)block[1], block[2], block[3], cv[0], cv[1], cv[2],
                    ch.TryGetProperty("delay", out var d) ? CurvesOf(d)[0] : null,
                    ch.TryGetProperty("stereo", out var s) ? CurvesOf(s)[0] : null);
            }

            var set = new RsGameCurveSet(
                Opt(c, "studioEq", x => new RsStudioEqCurves(x[0], x[1], x[2], x[3], x[4])),
                Opt(c, "compression", x => new RsCompressionCurves(x[0], x[1], x[2], x[3], x[4])),
                Opt(c, "loFi", x => new RsLoFiCurves(x[0], x[1])),
                Opt(c, "bassEmulator", x => new RsBassEmulatorCurves(x[0], x[1])),
                Opt(c, "bitCruncher", x =>
                {
                    var b = FloatsOf(c.GetProperty("bitCruncherBlock"));
                    return new RsBitCruncherCurves(x[0], x[1], x[2], b[0], b[1], b[2], b[3]);
                }),
                Opt(c, "bassOverdrive", x => new RsBassOverdriveCurves(x[0], x[1], x[2], x[3])),
                Opt(c, "acoustic", x => new RsAcousticCurves(x[0], x[1])),
                choruses,
                MbCompOf(c));
            return new RsGameTables(amps, set, nativeAmpTable);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException
                                       or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static RsMbCompModel? MbCompOf(JsonElement c)
    {
        if (!c.TryGetProperty("mbCompFit", out var fit)) return null;
        var f = FloatsOf(fit);
        return new RsMbCompModel(CurvesOf(c.GetProperty("mbCompIn")), CurvesOf(c.GetProperty("mbCompOut")), f[0], f[1],
            FloatsOf(c.GetProperty("mbCompCut")), CurvesOf(c.GetProperty("mbCompAttack"))[0], f[2], f[3] != 0f);
    }

    private static T? Opt<T>(JsonElement e, string name, Func<WwiseCurve[], T> make) where T : class
        => e.TryGetProperty(name, out var v) ? make(CurvesOf(v)) : null;

    private static void Set(Utf8JsonWriter w, string name, params WwiseCurve[] curves)
    {
        w.WriteStartArray(name);
        foreach (var c in curves)
        {
            w.WriteStartArray();
            w.WriteNumberValue(c.X0);
            w.WriteNumberValue(c.StoredY0);
            w.WriteNumberValue(c.X1);
            w.WriteNumberValue(c.StoredY1);
            w.WriteNumberValue((int)c.Shape);
            w.WriteNumberValue((int)c.Scaling);
            w.WriteEndArray();
        }

        w.WriteEndArray();
    }

    private static void Floats(Utf8JsonWriter w, string name, params float[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static float[] FloatsOf(JsonElement e) => e.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    private static WwiseCurve[] CurvesOf(JsonElement e) => e.EnumerateArray().Select(c =>
    {
        var a = c.EnumerateArray().ToArray();
        return new WwiseCurve(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle(),
            (WwiseCurveShape)a[4].GetInt32(), (WwiseCurveScaling)a[5].GetInt32());
    }).ToArray();
}
