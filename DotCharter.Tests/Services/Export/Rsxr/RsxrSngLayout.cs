namespace DotCharter.Tests.Services.Export.Rsxr;

/// <summary>Walks fixed record widths in synthetic chart bodies for structural assertions.</summary>
internal static class RsxrSngLayout
{
    internal readonly record struct Region(int Start, int Length);

    internal sealed class Layout
    {
        public List<Region> Templates { get; set; } = [];
        public List<Region> Techniques { get; set; } = [];
        public List<Region> Sections { get; set; } = [];
        public List<Region> NoteStrings { get; } = [];
        public Region Tuning { get; set; }
        public int End { get; set; }
    }

    public static Layout Walk(byte[] bytes, int width, int start = 0)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        stream.Position = start;
        var layout = new Layout();
        Array(reader, 16);
        Array(reader, 44);
        layout.Templates = Array(reader, 36 + 6 * width);
        layout.Techniques = Array(reader, 396 * width);
        if (Array(reader, 60).Count > 0)
        {
            Array(reader, 32);
            Array(reader, 144);
            Array(reader, 44);
        }

        Array(reader, 24);
        Array(reader, 16);
        var linked = reader.ReadInt32();
        for (var i = 0; i < linked; i++)
        {
            stream.Position += 4;
            Array(reader, 4);
        }

        foreach (var size in new[] { 260, 260, 8, 8 }) Array(reader, size);
        layout.Sections = Array(reader, 88);
        var levels = reader.ReadInt32();
        for (var i = 0; i < levels; i++) WalkLevel(reader, layout);
        stream.Position += 79;
        var tuningStart = (int)stream.Position;
        Array(reader, 2);
        layout.Tuning = new Region(tuningStart, (int)stream.Position - tuningStart);
        stream.Position += 12;
        layout.End = (int)stream.Position;
        Assert.InRange(layout.End, 0, bytes.Length);
        return layout;
    }

    private static void WalkLevel(BinaryReader reader, Layout layout)
    {
        reader.BaseStream.Position += 4;
        foreach (var size in new[] { 28, 12, 20, 20 }) Array(reader, size);
        var notes = reader.ReadInt32();
        for (var i = 0; i < notes; i++)
        {
            layout.NoteStrings.Add(new Region((int)reader.BaseStream.Position + 16, 1));
            reader.BaseStream.Position += 63;
            Array(reader, 12);
        }

        for (var i = 0; i < 3; i++) Array(reader, 4);
    }

    private static List<Region> Array(BinaryReader reader, int size)
    {
        var count = reader.ReadInt32();
        Assert.InRange(count, 0, (reader.BaseStream.Length - reader.BaseStream.Position) / size);
        var result = Enumerable.Range(0, count).Select(i => new Region((int)reader.BaseStream.Position + i * size, size)).ToList();
        reader.BaseStream.Position += count * size;
        return result;
    }

}
