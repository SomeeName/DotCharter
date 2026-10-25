using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     The in-memory <c>Reshape allowzero</c> strip that lets a lyric model with it run on
///     DirectML: exactly the Reshape nodes' <c>allowzero</c> attributes go, every
///     other byte stays, and the enclosing length prefixes are rewritten. The models are
///     built here as raw protobuf, field by field, the way an ONNX export serializes them.
/// </summary>
public sealed class OnnxReshapeAllowZeroTests
{
    private static readonly byte[] AllowZero = Message(Text(1, "allowzero"), Varint(3, 1), Varint(20, 2));
    private static readonly byte[] Axis = Message(Text(1, "axis"), Varint(3, 0), Varint(20, 2));

    [Fact]
    public void Strip_drops_allowzero_from_Reshape_nodes_and_keeps_every_other_byte()
    {
        var model = Model(Node("Reshape", AllowZero, Axis), Node("Unsqueeze", AllowZero));
        var expected = Model(Node("Reshape", Axis), Node("Unsqueeze", AllowZero));

        Assert.Equal(expected, OnnxReshapeAllowZero.Strip(model));
    }

    [Fact]
    public void Strip_reaches_Reshapes_inside_a_subgraph_attribute()
    {
        var body = Graph(Node("Reshape", AllowZero));
        var model = Model(Node("If", Message(Text(1, "then_branch"), Field(6, body), Varint(20, 5))));
        var expected = Model(Node("If", Message(Text(1, "then_branch"), Field(6, Graph(Node("Reshape"))), Varint(20, 5))));

        Assert.Equal(expected, OnnxReshapeAllowZero.Strip(model));
    }

    [Fact]
    public void Strip_leaves_a_clean_model_byte_identical()
    {
        var model = Model(Node("Reshape", Axis), Node("Gemm"));

        Assert.Equal(model, OnnxReshapeAllowZero.Strip(model));
    }

    [Fact]
    public void Strip_refuses_a_truncated_model()
    {
        var model = Model(Node("Reshape", AllowZero));

        Assert.Throws<InvalidDataException>(() => OnnxReshapeAllowZero.Strip(model[..^3]));
    }

    [Fact]
    public void Contains_finds_the_name_across_a_read_boundary()
    {
        var bytes = new byte[(1 << 20) + 100];
        "allowzero"u8.CopyTo(bytes.AsSpan((1 << 20) - 4));

        Assert.True(OnnxReshapeAllowZero.Contains(new MemoryStream(bytes)));
        Assert.False(OnnxReshapeAllowZero.Contains(new MemoryStream(Encoding.ASCII.GetBytes("Reshape allow zeros"))));
    }

    /// <summary>
    ///     The real thing, when both exports are on disk: stripping a v1 model gives
    ///     exactly the published v2 file. <c>DOTCHARTER_LYRIC_STRIP_PAIRS</c> lists
    ///     <c>v1-model.onnx|v2-model.onnx</c> pairs, path-separated.
    /// </summary>
    [Fact]
    public void Stripping_a_v1_model_gives_the_published_v2_bytes()
    {
        var pairs = Environment.GetEnvironmentVariable("DOTCHARTER_LYRIC_STRIP_PAIRS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairs), "DOTCHARTER_LYRIC_STRIP_PAIRS is not set.");

        foreach (var pair in pairs!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var (v1, v2) = (pair.Split('|')[0], pair.Split('|')[1]);
            var stripped = OnnxReshapeAllowZero.StripIfPresent(v1);
            Assert.NotNull(stripped);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(v2))), Convert.ToHexString(SHA256.HashData(stripped)));
            Assert.Null(OnnxReshapeAllowZero.StripIfPresent(v2));
        }
    }

    // A ModelProto: ir_version (1), graph (7), producer_name (2) after it.
    private static byte[] Model(params byte[][] nodes) =>
        Message(Varint(1, 8), Field(7, Graph(nodes)), Text(2, "pytorch"));

    // A GraphProto: its nodes (1), an initializer (5) big enough for a two-byte length, a name (2).
    private static byte[] Graph(params byte[][] nodes) =>
        Message([.. nodes.Select(n => Field(1, n)), Field(5, new byte[300]), Text(2, "main_graph")]);

    // A NodeProto: input (1), output (2), op_type (4), attributes (5).
    private static byte[] Node(string opType, params byte[][] attributes) =>
        Message([Text(1, "x"), Text(2, "y"), Text(4, opType), .. attributes.Select(a => Field(5, a))]);

    private static byte[] Message(params byte[][] fields) => [.. fields.SelectMany(f => f)];

    private static byte[] Text(int field, string value) => Field(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Field(int field, byte[] payload) => [.. Encode((ulong)((field << 3) | 2)), .. Encode((ulong)payload.Length), .. payload];

    private static byte[] Varint(int field, ulong value) => [.. Encode((ulong)(field << 3)), .. Encode(value)];

    private static byte[] Encode(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? b : (byte)(b | 0x80));
        }
        while (value != 0);

        return [.. bytes];
    }
}
