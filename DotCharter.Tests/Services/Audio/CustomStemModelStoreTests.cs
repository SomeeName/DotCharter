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

    private static StemModelDescriptor Make(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Description = "",
        DownloadUrl = "https://example.com/" + id,
        FileName = id + ".bin",
    };
}
