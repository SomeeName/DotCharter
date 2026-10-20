using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Components;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// Pure tests for the component-manager inventory: model + tool resolution,
/// installed/missing/active status, PATH probing, size/hash verification and
/// the catalog builder — all driven by an in-memory <see cref="FakeComponentFileSystem"/>.
/// </summary>
public sealed class ComponentInventoryTests
{
    // ---- fake filesystem seam --------------------------------------------

    private sealed class FakeComponentFileSystem : IComponentFileSystem
    {
        private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _path = [];

        public FakeComponentFileSystem AddFile(string path, long size, string? sha256 = null)
        {
            _files[path] = size;
            if (sha256 != null) _hashes[path] = sha256;
            return this;
        }

        public FakeComponentFileSystem AddPathDir(string dir) { _path.Add(dir); return this; }

        public bool FileExists(string path) => _files.ContainsKey(path);
        public long GetFileSize(string path) => _files.TryGetValue(path, out var s) ? s : -1;
        public string? ComputeSha256(string path) => _hashes.TryGetValue(path, out var h) ? h : null;
        public IReadOnlyList<string> PathDirectories => _path;
    }

    private const string ModelsDir = @"C:\models";
    private const string AppDir = @"C:\app";

    private static StemModelDescriptor Model(string id, string file, long bytes) => new()
    {
        Id = id,
        DisplayName = id,
        Description = "test",
        DownloadUrl = "https://example/" + file,
        FileName = file,
        ApproxBytes = bytes,
        SourceCount = 4,
        Kind = StemModelKind.DemucsGgml,
    };

    // ---- model scanning ---------------------------------------------------

    [Fact]
    public void Model_present_in_models_dir_reports_installed()
    {
        var fs = new FakeComponentFileSystem()
            .AddFile(Path.Combine(ModelsDir, "a.bin"), 100);
        var defs = ComponentCatalog.Build(ModelsDir, activeModelId: null,
            new[] { Model("a", "a.bin", 100) }, AppDir);
        var inv = new ComponentInventory(fs);

        var status = inv.Scan(defs).First(s => s.Definition.Id == "a");

        Assert.Equal(ComponentState.Installed, status.State);
        Assert.True(status.IsInstalled);
        Assert.False(status.IsActive);
        Assert.Equal(Path.Combine(ModelsDir, "a.bin"), status.ResolvedPath);
        Assert.Equal(100, status.SizeBytes);
    }

    [Fact]
    public void Model_absent_reports_missing()
    {
        var fs = new FakeComponentFileSystem();
        var defs = ComponentCatalog.Build(ModelsDir, null, new[] { Model("a", "a.bin", 100) }, AppDir);
        var status = new ComponentInventory(fs).Scan(defs).First(s => s.Definition.Id == "a");

        Assert.Equal(ComponentState.Missing, status.State);
        Assert.False(status.IsInstalled);
        Assert.Null(status.ResolvedPath);
        Assert.Equal(0, status.SizeBytes);
    }

    [Fact]
    public void Active_model_that_is_installed_reports_active()
    {
        var fs = new FakeComponentFileSystem().AddFile(Path.Combine(ModelsDir, "a.bin"), 100);
        var defs = ComponentCatalog.Build(ModelsDir, activeModelId: "a",
            new[] { Model("a", "a.bin", 100) }, AppDir);
        var status = new ComponentInventory(fs).Scan(defs).First(s => s.Definition.Id == "a");

        Assert.Equal(ComponentState.Active, status.State);
        Assert.True(status.IsActive);
    }

    [Fact]
    public void Active_selection_but_file_missing_reports_missing_not_active()
    {
        var fs = new FakeComponentFileSystem();
        var defs = ComponentCatalog.Build(ModelsDir, activeModelId: "a",
            new[] { Model("a", "a.bin", 100) }, AppDir);
        var status = new ComponentInventory(fs).Scan(defs).First(s => s.Definition.Id == "a");

        Assert.Equal(ComponentState.Missing, status.State);
        Assert.False(status.IsActive);
    }

    // ---- tool scanning ----------------------------------------------------

    [Fact]
    public void Ffmpeg_found_in_app_tools_folder()
    {
        var toolsPath = Path.Combine(Path.Combine(AppDir, "tools"), "ffmpeg.exe");
        var fs = new FakeComponentFileSystem().AddFile(toolsPath, 5000);
        var defs = ComponentCatalog.Build(ModelsDir, null, Array.Empty<StemModelDescriptor>(), AppDir);

        var status = new ComponentInventory(fs).Scan(defs).First(s => s.Definition.Id == "tool-ffmpeg");

        Assert.True(status.IsInstalled);
        Assert.Equal(toolsPath, status.ResolvedPath);
    }

    [Fact]
    public void Ffmpeg_found_on_path_when_not_bundled()
    {
        var pathHit = Path.Combine(@"C:\bin", "ffmpeg.exe");
        var fs = new FakeComponentFileSystem().AddPathDir(@"C:\bin").AddFile(pathHit, 42);
        var defs = ComponentCatalog.Build(ModelsDir, null, Array.Empty<StemModelDescriptor>(), AppDir);

        var status = new ComponentInventory(fs).Scan(defs).First(s => s.Definition.Id == "tool-ffmpeg");

        Assert.True(status.IsInstalled);
        Assert.Equal(pathHit, status.ResolvedPath);
    }

    [Fact]
    public void Tool_never_reports_active_even_when_present()
    {
        var pathHit = Path.Combine(@"C:\bin", "ffmpeg.exe");
        var fs = new FakeComponentFileSystem().AddPathDir(@"C:\bin").AddFile(pathHit, 42);
        var defs = ComponentCatalog.Build(ModelsDir, null, Array.Empty<StemModelDescriptor>(), AppDir);

        var status = new ComponentInventory(fs).Scan(defs).First(s => s.Definition.Id == "tool-ffmpeg");

        Assert.Equal(ComponentState.Installed, status.State);
        Assert.False(status.Definition.Downloadable); // tools are list-only
    }

    [Fact]
    public void Missing_tool_is_missing_when_path_empty()
    {
        var fs = new FakeComponentFileSystem();
        var defs = ComponentCatalog.Build(ModelsDir, null, Array.Empty<StemModelDescriptor>(), AppDir);
        var status = new ComponentInventory(fs).Scan(defs).First(s => s.Definition.Id == "tool-yt-dlp");
        Assert.Equal(ComponentState.Missing, status.State);
    }

    // ---- verification -----------------------------------------------------

    [Fact]
    public void Verify_missing_file_is_not_ok()
    {
        var fs = new FakeComponentFileSystem();
        var def = ComponentCatalog.FromModel(Model("a", "a.bin", 100), ModelsDir, null);
        var v = new ComponentInventory(fs).Verify(def);

        Assert.False(v.Exists);
        Assert.False(v.Ok);
        Assert.Equal("a", v.ComponentId);
    }

    [Fact]
    public void Verify_present_matching_size_is_ok()
    {
        var path = Path.Combine(ModelsDir, "a.bin");
        var fs = new FakeComponentFileSystem().AddFile(path, 100);
        var def = ComponentCatalog.FromModel(Model("a", "a.bin", 100), ModelsDir, null);
        var v = new ComponentInventory(fs).Verify(def);

        Assert.True(v.Exists);
        Assert.True(v.SizeLooksRight);
        Assert.True(v.Ok);
        Assert.Equal(100, v.SizeBytes);
        Assert.Equal(path, v.Path);
    }

    [Fact]
    public void Verify_size_far_from_expected_flags_mismatch()
    {
        var fs = new FakeComponentFileSystem().AddFile(Path.Combine(ModelsDir, "a.bin"), 10);
        var def = ComponentCatalog.FromModel(Model("a", "a.bin", 1000), ModelsDir, null);
        var v = new ComponentInventory(fs).Verify(def);

        Assert.True(v.Exists);
        Assert.False(v.SizeLooksRight);
        Assert.False(v.Ok);
    }

    [Fact]
    public void Verify_unknown_expected_size_only_requires_nonempty()
    {
        var fs = new FakeComponentFileSystem().AddFile(Path.Combine(ModelsDir, "a.bin"), 7);
        var def = ComponentCatalog.FromModel(Model("a", "a.bin", 0), ModelsDir, null);
        var v = new ComponentInventory(fs).Verify(def);

        Assert.True(v.Ok);
        Assert.True(v.SizeLooksRight);
    }

    [Fact]
    public void Verify_computes_hash_only_when_requested()
    {
        var path = Path.Combine(ModelsDir, "a.bin");
        var fs = new FakeComponentFileSystem().AddFile(path, 100, sha256: "deadbeef");
        var def = ComponentCatalog.FromModel(Model("a", "a.bin", 100), ModelsDir, null);
        var inv = new ComponentInventory(fs);

        Assert.Null(inv.Verify(def, computeHash: false).Sha256);
        Assert.Equal("deadbeef", inv.Verify(def, computeHash: true).Sha256);
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(80, 100, true)]    // within -25%
    [InlineData(125, 100, true)]   // within +25%
    [InlineData(50, 100, false)]   // too small
    [InlineData(200, 100, false)]  // too big
    [InlineData(5, 0, true)]       // no expectation, non-empty
    [InlineData(0, 0, false)]      // empty, no expectation
    public void SizeLooksRight_tolerance(long actual, long expected, bool ok) => Assert.Equal(ok, ComponentInventory.SizeLooksRight(actual, expected));

    // ---- catalog builder --------------------------------------------------

    [Fact]
    public void Build_emits_a_definition_per_model_plus_external_tools()
    {
        var defs = ComponentCatalog.Build(ModelsDir, null,
            new[] { Model("a", "a.bin", 1), Model("b", "b.onnx", 2) }, AppDir);

        Assert.Equal(2, defs.Count(d => d.Category == ComponentCategory.StemModel));
        Assert.Contains(defs, d => d.Id == "tool-ffmpeg");
        Assert.Contains(defs, d => d.Id == "tool-yt-dlp");
        Assert.All(defs.Where(d => d.Category == ComponentCategory.MediaTool),
            d => Assert.True(d.SearchPath));
    }

    [Fact]
    public async Task Disabled_downloader_never_permits_download()
    {
        var def = ComponentCatalog.FromModel(Model("a", "a.bin", 1), ModelsDir, null);
        var dl = DisabledComponentDownloader.Instance;
        Assert.False(dl.CanDownload(def));
        Assert.False(string.IsNullOrWhiteSpace(dl.UnavailableReason));
        await Assert.ThrowsAsync<NotSupportedException>(() => dl.DownloadAsync(def, ct: TestContext.Current.CancellationToken));
    }
}
