using DotCharter.UI.Controls.Hero;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The Home hero decodes a cover into a raw RGBA buffer the size of the
///     codec's DECLARED dimensions. The oversized fixture is a real PNG one pixel
///     past the shared ceiling (a few dozen bytes), so only the header probe can
///     be what turns it away - the decoder itself would take it.
/// </summary>
public sealed class HeroStageDecodeCapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotcharter-herocap-{Guid.NewGuid():N}");

    public HeroStageDecodeCapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string WritePng(int width, int height, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TinyPng.Encode(width, height));
        return path;
    }

    [Fact]
    public void AnOversizedCover_IsNotDecoded()
    {
        var (rgba, w, h) = HeroStage.DecodeRgba(WritePng(8193, 1, "big.png"));
        Assert.Null(rgba);
        Assert.Equal((0, 0), (w, h));
    }

    [Fact]
    public void AnOrdinaryCover_StillDecodes()
    {
        var (rgba, w, h) = HeroStage.DecodeRgba(WritePng(64, 64, "ok.png"));
        Assert.NotNull(rgba);
        Assert.Equal((64, 64), (w, h));
    }
}
