namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     Removes the <c>allowzero</c> attribute from every <c>Reshape</c> node of an ONNX
///     model, in memory. torch.export writes <c>allowzero=1</c> on every Reshape and
///     DirectML fails the first run on it; no shape in the lyric models holds a zero, so
///     the attribute is a no-op on the CPU and dropping it lets the graph run on the GPU.
///     <para>
///         The published v1 archives still carry it, so this is how they reach the GPU
///         (a re-exported archive without it is described in tools/lyric-sync/export-models.md).
///         The file on disk is not touched.
///     </para>
///     <para>
///         A protobuf rewrite rather than an ONNX library: every field is copied byte
///         for byte except the dropped attributes, and only the length prefixes of the
///         messages that contain them change (model → graph → node, and the subgraphs
///         and functions that can hold nodes).
///     </para>
/// </summary>
internal static class OnnxReshapeAllowZero
{
    private static readonly byte[] Marker = "allowzero"u8.ToArray();

    private enum Kind
    {
        None,
        Model,
        Graph,
        Function,
        Node,
        Attribute
    }

    /// <summary>The model's bytes without the attribute, or null when the file never names it.</summary>
    public static byte[]? StripIfPresent(string modelPath)
    {
        using (var stream = File.OpenRead(modelPath))
        {
            if (!Contains(stream)) return null;
        }

        return Strip(File.ReadAllBytes(modelPath));
    }

    /// <summary>Whether the stream holds the attribute's name anywhere, read in blocks.</summary>
    public static bool Contains(Stream stream)
    {
        var buffer = new byte[1 << 20];
        var carry = 0;
        int read;
        while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var filled = carry + read;
            if (buffer.AsSpan(0, filled).IndexOf(Marker) >= 0) return true;
            carry = Math.Min(Marker.Length - 1, filled);
            buffer.AsSpan(filled - carry, carry).CopyTo(buffer);
        }

        return false;
    }

    /// <summary>A serialized <c>ModelProto</c> without any <c>Reshape</c> node's <c>allowzero</c>.</summary>
    public static byte[] Strip(byte[] model)
    {
        var output = new byte[Rewrite(model, Kind.Model, [], measure: true)];
        Rewrite(model, Kind.Model, output, measure: false);
        return output;
    }

    /// <summary>
    ///     Copies one message into <paramref name="destination" /> (or only counts its bytes
    ///     when <paramref name="measure" />), rewriting the fields that can hold a Reshape.
    /// </summary>
    private static int Rewrite(ReadOnlySpan<byte> source, Kind kind, Span<byte> destination, bool measure)
    {
        var reshape = kind == Kind.Node && IsReshape(source);
        var position = 0;
        var written = 0;
        while (position < source.Length)
        {
            var start = position;
            var tag = ReadField(source, ref position, out var payload);
            var field = (int)(tag >> 3);
            var child = (tag & 7) == 2 ? ChildKind(kind, field) : Kind.None;
            if (child == Kind.None)
            {
                written += Copy(source[start..position], destination, written, measure);
                continue;
            }

            if (reshape && field == 5 && IsAllowZero(payload)) continue;

            var length = Rewrite(payload, child, [], measure: true);
            written += WriteVarint(tag, destination, written, measure);
            written += WriteVarint((ulong)length, destination, written, measure);
            if (!measure) Rewrite(payload, child, destination.Slice(written, length), measure: false);
            written += length;
        }

        return written;
    }

    /// <summary>Which fields of a message hold a message this rewrite walks into.</summary>
    private static Kind ChildKind(Kind parent, int field) => (parent, field) switch
    {
        (Kind.Model, 7) => Kind.Graph,
        (Kind.Model, 25) => Kind.Function,
        (Kind.Graph, 1) => Kind.Node,
        (Kind.Function, 7) => Kind.Node,
        (Kind.Node, 5) => Kind.Attribute,
        (Kind.Attribute, 6) => Kind.Graph,
        (Kind.Attribute, 11) => Kind.Graph,
        _ => Kind.None
    };

    /// <summary>NodeProto field 4 is <c>op_type</c>.</summary>
    private static bool IsReshape(ReadOnlySpan<byte> node) => StringField(node, 4).SequenceEqual("Reshape"u8);

    /// <summary>AttributeProto field 1 is <c>name</c>.</summary>
    private static bool IsAllowZero(ReadOnlySpan<byte> attribute) => StringField(attribute, 1).SequenceEqual(Marker);

    private static ReadOnlySpan<byte> StringField(ReadOnlySpan<byte> message, int wanted)
    {
        var position = 0;
        while (position < message.Length)
        {
            var tag = ReadField(message, ref position, out var payload);
            if (tag >> 3 == (ulong)wanted && (tag & 7) == 2) return payload;
        }

        return [];
    }

    /// <summary>One field: its tag, and its payload when it is length-delimited (else empty).</summary>
    private static ulong ReadField(ReadOnlySpan<byte> source, scoped ref int position, out ReadOnlySpan<byte> payload)
    {
        payload = [];
        var tag = ReadVarint(source, ref position);
        switch (tag & 7)
        {
            case 0:
                ReadVarint(source, ref position);
                return tag;
            case 1:
                position += 8;
                return tag;
            case 5:
                position += 4;
                return tag;
            case 2:
            {
                var length = ReadVarint(source, ref position);
                if (length > (ulong)(source.Length - position)) throw new InvalidDataException("Truncated ONNX field.");
                payload = source.Slice(position, (int)length);
                position += (int)length;
                return tag;
            }
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {tag & 7} in an ONNX model.");
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> source, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= source.Length) throw new InvalidDataException("Truncated ONNX varint.");
            var b = source[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }

        throw new InvalidDataException("Malformed ONNX varint.");
    }

    private static int WriteVarint(ulong value, Span<byte> destination, int at, bool measure)
    {
        var count = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            if (!measure) destination[at + count] = b;
            count++;
        }
        while (value != 0);

        return count;
    }

    private static int Copy(ReadOnlySpan<byte> bytes, Span<byte> destination, int at, bool measure)
    {
        if (!measure) bytes.CopyTo(destination[at..]);
        return bytes.Length;
    }
}
