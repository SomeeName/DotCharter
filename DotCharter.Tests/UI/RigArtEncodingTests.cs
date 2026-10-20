using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig's WebP art ships LOSSY and at full size (#407). A lossless face is
///     ~1.5 MB against ~0.3 MB, and 100 of them were 146 of the 160 MB of
///     <c>DotCharter.Tone.UI.dll</c>: <c>tools/rig-art/import-faceplates.py</c> writes
///     a lossless master and <c>tools/rig-art/compress-faces.py</c> must run after it.
///     A face is also never stored SMALLER than the catalogue says, because the
///     module editor draws it bigger than its stored size on a 4K panel.
/// </summary>
public sealed class RigArtEncodingTests
{
    /// <summary>The largest face at q90 + sharp YUV is 545 KB (whammy treadle and the
    /// wide heads); a lossless one is 0.9-2.1 MB.</summary>
    private const long MaxFaceBytes = 768 * 1024;

    /// <summary>All 100 faces are 29.1 MB at q90; lossless they were 146 MB.</summary>
    private const long MaxFacesTotalBytes = 36L * 1024 * 1024;

    private static string RigAssets() => Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Assets", "Rig");

    [Fact]
    public void EveryRigWebp_IsLossy()
    {
        var lossless = new List<string>();
        foreach (var folder in new[] { "Faces", "Art", "Caps" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RigAssets(), folder), "*.webp"))
                if (ImageChunk(file).Tag == "VP8L") lossless.Add($"{folder}/{Path.GetFileName(file)}");
        }

        Assert.True(lossless.Count == 0,
            "lossless WebP (run tools/rig-art/compress-faces.py): " + string.Join(", ", lossless));
    }

    [Fact]
    public void EveryFace_FitsTheSizeBudget()
    {
        var faces = Directory.GetFiles(Path.Combine(RigAssets(), "Faces"), "*.webp");
        Assert.True(faces.Length >= 100, $"only {faces.Length} faces");
        var oversized = faces.Select(f => new FileInfo(f)).Where(f => f.Length > MaxFaceBytes)
            .Select(f => $"{f.Name} ({f.Length / 1024} KB)").ToList();
        Assert.True(oversized.Count == 0, $"faces over {MaxFaceBytes / 1024} KB: " + string.Join(", ", oversized));
        var total = faces.Sum(f => new FileInfo(f).Length);
        Assert.True(total <= MaxFacesTotalBytes, $"faces total {total / 1024 / 1024} MB, budget {MaxFacesTotalBytes / 1024 / 1024} MB");
    }

    [Fact]
    public void EveryFace_IsStoredAtTheSizeTheCatalogueRecords()
    {
        var root = Path.Combine(RigAssets(), "Faces");
        var faces = RigFaceplates.Parse(File.ReadAllText(Path.Combine(RigAssets(), "faceplates.json")));
        var wrong = new List<string>();
        foreach (var face in faces.Values)
        {
            var (width, height) = CanvasSize(Path.Combine(root, face.File));
            if (width != face.PixelWidth || height != face.PixelHeight)
                wrong.Add($"{face.File} is {width}x{height}, catalogue {face.PixelWidth}x{face.PixelHeight}");
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    /// <summary>The first image chunk (VP8 = lossy, VP8L = lossless) of a RIFF WebP.</summary>
    private static (string Tag, byte[] Payload) ImageChunk(string path)
    {
        var data = File.ReadAllBytes(path);
        Assert.True(data.Length > 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            $"{path} is not a WebP");
        for (var i = 12; i + 8 <= data.Length;)
        {
            var tag = System.Text.Encoding.ASCII.GetString(data, i, 4);
            var size = BitConverter.ToInt32(data, i + 4);
            if (tag is "VP8 " or "VP8L") return (tag.TrimEnd(), data.AsSpan(i + 8, size).ToArray());
            i += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path}: no image chunk");
    }

    /// <summary>Width and height from the VP8 / VP8L bitstream header.</summary>
    private static (int Width, int Height) CanvasSize(string path)
    {
        var (tag, p) = ImageChunk(path);
        if (tag == "VP8")
            return (BitConverter.ToUInt16(p, 6) & 0x3FFF, BitConverter.ToUInt16(p, 8) & 0x3FFF);
        var bits = BitConverter.ToUInt32(p, 1); // after the 0x2F signature: 14-bit width-1, 14-bit height-1
        return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
    }
}
