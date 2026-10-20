using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     Reads the native <c>rsamp</c> table blob (JuceSharp <c>dcdsp_rsamp_table.h</c>,
///     ABI v45) INDEPENDENTLY of the writer, into one canonical line per record: what the
///     engine receives, comparable by hash with the chain header JuceSharp compiled in
///     until #25 (the same text was taken from that header the day it was removed).
///     Per row, in row order: <c>M key knobs= slots= curves=</c>, a <c>K</c> line per knob,
///     an <c>S G|F|C</c> line per slot with its block inlined, and a <c>V</c> line per curve
///     with its points inlined. Floats are their IEEE bits in hex (-0 as +0), integers decimal.
///     The text of the player's own install is never written to the repository or a test log.
/// </summary>
internal static class NativeAmpTableText
{
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Canonical(byte[] blob)
    {
        var r = new Reader(blob);
        Assert.Equal("DCRSAMPT"u8.ToArray(), r.Bytes(8));
        Assert.Equal(1u, r.U32());
        var (rows, gdCount, futzCount, compCount, pointCount, curveCount) = (r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.U32());
        var rowList = new List<Row>();
        for (var i = 0; i < rows; i++)
        {
            var row = new Row { Index = r.U8(), Key = Encoding.ASCII.GetString(r.Bytes(r.U8())) };
            var knobs = r.U8();
            for (var k = 0; k < knobs; k++) row.Knobs.Add($"K {r.F32()} {r.F32()} {r.F32()} {r.U8()}");
            var slots = r.U8();
            for (var s = 0; s < slots; s++) row.Slots.Add((r.U8(), r.U16()));
            row.FirstCurve = r.U16();
            row.Curves = r.U16();
            rowList.Add(row);
        }

        var gd = Enumerable.Range(0, (int)gdCount).Select(_ =>
        {
            var bands = string.Join(' ', Enumerable.Range(0, 6).Select(_ =>
            {
                var type = r.U32();
                var on = r.U8();
                return $"{type} {on} {r.F32()} {r.F32()} {r.F32()}";
            }));
            return $"S G {bands} {r.U32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()}";
        }).ToList();
        var futz = Enumerable.Range(0, (int)futzCount).Select(_ =>
            $"S F {r.U8()} {r.F32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()} {r.U8()} {r.U32()} {r.F32()} {r.F32()} {r.F32()} " +
            $"{r.U8()} {r.U32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()}").ToList();
        var comp = Enumerable.Range(0, (int)compCount).Select(_ => $"S C {r.F32()} {r.F32()} {r.F32()} {r.F32()} {r.F32()}").ToList();
        var points = Enumerable.Range(0, (int)pointCount).Select(_ => $"{r.F32()} {r.F32()} {r.U8()}").ToList();
        var curves = Enumerable.Range(0, (int)curveCount).Select(_ =>
        {
            var (knob, slot, param, scaling) = (r.U8(), r.U8(), r.U8(), r.U8());
            var first = r.U16();
            var n = r.U8();
            return $"V {knob} {slot} {param} {scaling} {n} " + string.Join(' ', points.Skip(first).Take(n));
        }).ToList();
        Assert.True(r.AtEnd, "trailing bytes in the native amp table");

        var lines = new List<string>();
        foreach (var row in rowList.OrderBy(x => x.Index))
        {
            lines.Add($"M {row.Key} knobs={row.Knobs.Count} slots={row.Slots.Count} curves={row.Curves}");
            lines.AddRange(row.Knobs);
            foreach (var (kind, index) in row.Slots)
                lines.Add(kind switch { 1 => gd[index], 2 => futz[index], 3 => comp[index], _ => throw new InvalidDataException($"slot kind {kind}") });
            lines.AddRange(curves.Skip(row.FirstCurve).Take(row.Curves));
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The rows' keys in row order.</summary>
    public static IReadOnlyList<string> Keys(byte[] blob)
        => Canonical(blob).Split('\n').Where(l => l.StartsWith("M ", StringComparison.Ordinal)).Select(l => l.Split(' ')[1]).ToList();

    private sealed class Row
    {
        public int Index;
        public string Key = "";
        public readonly List<string> Knobs = [];
        public readonly List<(int Kind, int Index)> Slots = [];
        public int FirstCurve;
        public int Curves;
    }

    private sealed class Reader(byte[] b)
    {
        private int _o;

        public bool AtEnd => _o == b.Length;

        public byte[] Bytes(int n)
        {
            var v = b.AsSpan(_o, n).ToArray();
            _o += n;
            return v;
        }

        public int U8() => b[_o++];

        public int U16()
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(_o));
            _o += 2;
            return v;
        }

        public uint U32()
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(_o));
            _o += 4;
            return v;
        }

        public string F32()
        {
            var bits = U32();
            if (bits == 0x80000000u) bits = 0;
            return bits.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
