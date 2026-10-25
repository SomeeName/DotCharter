using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     The lyric models must run on DirectML. torch.export writes
///     <c>allowzero=1</c> on every <c>Reshape</c>, DirectML rejects it, and the
///     run silently falls back to the CPU at roughly twenty times the cost.
///     <para>
///         The archives are not in the repository, so the catalogue's names and
///         hashes are pinned here (changing them is a deliberate act), and the
///         graph itself is scanned when a model folder is supplied.
///     </para>
/// </summary>
public sealed class LyricModelGraphTests
{
    private const string ModelDirsVariable = "DOTCHARTER_LYRIC_MODEL_DIRS";

    [Fact]
    public void Catalogue_pins_the_downloaded_archives()
    {
        Assert.Equal("lyric-align-v1.zip", LyricModelCatalog.MultilingualDownload.AssetName);
        Assert.Equal("8c0ededd24910ca79358b7f0a6126c4e7dca103237c02b5a42f5765b27020b07",
            LyricModelCatalog.MultilingualDownload.Sha256);
        Assert.Equal("lyric-align-en-v1.zip", LyricModelCatalog.EnglishDownload.AssetName);
        Assert.Equal("d6029fa8a08b4fcfb5631454e8b54d56b04af2361d449eec4e363f774b475438",
            LyricModelCatalog.EnglishDownload.Sha256);
    }

    [Fact]
    public void Installed_models_carry_no_Reshape_allowzero()
    {
        var dirs = Environment.GetEnvironmentVariable(ModelDirsVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dirs),
            $"{ModelDirsVariable} is not set (path-separated lyric model folders).");

        foreach (var dir in dirs!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            using var stream = File.OpenRead(Path.Combine(dir, "model.onnx"));
            Assert.False(OnnxReshapeAllowZero.Contains(stream), $"{dir} still has Reshape allowzero.");
        }
    }
}
