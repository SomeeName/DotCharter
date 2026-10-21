using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

public class CustomStemModelStoreTests : IDisposable
{
    private readonly string _dir;

    public CustomStemModelStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dotcharter-test-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void Load_WhenFileMissing_ReturnsEmpty()
    {
        var store = new CustomStemModelStore(_dir);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Save_ThenLoad_Roundtrip()
    {
        var store = new CustomStemModelStore(_dir);
        var models = new[]
        {
            new StemModelDescriptor
            {
                Id = "test-htdemucs",
                DisplayName = "Test HTDemucs",
                Description = "Custom model for testing",
                DownloadUrl = "https://example.com/model.bin",
                FileName = "model.bin",
                ApproxBytes = 12345,
                SourceCount = 6,
                Kind = StemModelKind.HtdemucsOnnx,
            },
        };

        store.Save(models);

        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Equal("test-htdemucs", loaded[0].Id);
        Assert.Equal("Test HTDemucs", loaded[0].DisplayName);
        Assert.Equal("Custom model for testing", loaded[0].Description);
        Assert.Equal("https://example.com/model.bin", loaded[0].DownloadUrl);
        Assert.Equal("model.bin", loaded[0].FileName);
        Assert.Equal(12345, loaded[0].ApproxBytes);
        Assert.Equal(6, loaded[0].SourceCount);
        Assert.Equal(StemModelKind.HtdemucsOnnx, loaded[0].Kind);
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "nested", "deeper");
        var store = new CustomStemModelStore(nested);

        store.Save(Array.Empty<StemModelDescriptor>());

        Assert.True(File.Exists(Path.Combine(nested, "custom_models.json")));
    }

    [Fact]
    public void Load_WithCorruptJson_ReturnsEmpty_AndDoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_dir, "custom_models.json"), "{not valid json");
        var store = new CustomStemModelStore(_dir);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_WithUnknownKind_DefaultsToMdxNetOnnx()
    {
        File.WriteAllText(Path.Combine(_dir, "custom_models.json"),
            "[{\"id\":\"x\",\"displayName\":\"X\",\"description\":\"\",\"downloadUrl\":\"\",\"fileName\":\"x\",\"approxBytes\":0,\"sourceCount\":4,\"kind\":\"NotARealKind\"}]");
        var store = new CustomStemModelStore(_dir);
        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Equal(StemModelKind.MdxNetOnnx, loaded[0].Kind);
    }

    [Fact]
    public void Save_OverwritesPreviousContent()
    {
        var store = new CustomStemModelStore(_dir);
        store.Save(new[] { Make("a"), Make("b") });
        store.Save(new[] { Make("c") });

        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Equal("c", loaded[0].Id);
    }

    /// <summary>
    ///     <c>custom_models.json</c> is a file the user may have received from someone
    ///     else, and a model's file name is joined onto the models folder to decide
    ///     where a download is written and what gets loaded or deleted. A name that is
    ///     not a bare file name (a rooted path, a "..", a separator, a drive/stream
    ///     colon, an empty or illegal name) is dropped on load, never joined.
    /// </summary>
    [Theory]
    [InlineData("..\\..\\evil.dll")]
    [InlineData("../evil.dll")]
    [InlineData("sub/model.onnx")]
    [InlineData("sub\\model.onnx")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("\\\\server\\share\\evil.dll")]
    [InlineData("/etc/evil")]
    [InlineData("model.onnx:stream")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("model\u0000.onnx")]
    public void Load_DropsAModelWhoseFileNameIsNotABareFileName(string fileName)
    {
        var entries = new[] { ("good", "good.onnx"), ("bad", fileName), ("good2", "good2.onnx") }
            .Select(e => new Dictionary<string, object>
            {
                ["id"] = e.Item1,
                ["displayName"] = e.Item1,
                ["description"] = "",
                ["downloadUrl"] = "",
                ["fileName"] = e.Item2,
                ["approxBytes"] = 0,
                ["sourceCount"] = 4,
                ["kind"] = "MdxNetOnnx"
            });
        File.WriteAllText(Path.Combine(_dir, "custom_models.json"),
            System.Text.Json.JsonSerializer.Serialize(entries));

        var loaded = new CustomStemModelStore(_dir).Load();

        Assert.Equal(new[] { "good", "good2" }, loaded.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void GetLocalPath_NeverLeavesTheModelsFolder()
    {
        var modelsDir = Path.Combine(_dir, "models");
        var manager = new StemModelManager(modelsDirectoryOverride: modelsDir);
        var escape = new StemModelDescriptor
        {
            Id = "x", DisplayName = "x", Description = "", DownloadUrl = "",
            FileName = Path.Combine("..", "..", "evil.dll"),
        };

        var path = Path.GetFullPath(manager.GetLocalPath(escape));

        Assert.Equal(Path.GetFullPath(modelsDir), Path.GetDirectoryName(path));
        Assert.Equal(Path.Combine(Path.GetFullPath(modelsDir), "model.bin"),
            Path.GetFullPath(manager.GetLocalPath(Make("model"))));
    }

    private static StemModelDescriptor Make(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Description = "",
        DownloadUrl = "https://example.com/" + id,
        FileName = id + ".bin",
    };
}
