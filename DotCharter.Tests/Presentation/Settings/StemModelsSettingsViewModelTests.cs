using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
/// Covers the row-level state machinery and the parent VM's single-active-row
/// exclusion. Does not exercise the actual download path (HTTP is out of
/// scope for unit tests).
/// </summary>
public sealed class StemModelsSettingsViewModelTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly FakeConfigurationService _cfg = new();

    public StemModelsSettingsViewModelTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "DotCharter_StemModelsVM_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[test cleanup] {ex.Message}"); }
    }

    private StemModelManager BuildManager()
        => new(_cfg, logger: null, modelsDirectoryOverride: _tmpDir);

    private static void FakeInstall(StemModelManager mgr, string id, int bytes = 32)
    {
        var d = StemModelCatalog.FindById(id) ?? throw new InvalidOperationException(id);
        var path = Path.Combine(mgr.ModelsDirectory, d.FileName);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void Catalog_rows_are_listed_with_correct_install_state()
    {
        var mgr = BuildManager();
        FakeInstall(mgr, "htdemucs-4s");

        using var vm = StemModelsRig.Create(mgr);

        Assert.Equal(mgr.Catalog.Count, vm.Models.Count);
        Assert.True(vm.Models.First(r => r.Descriptor.Id == "htdemucs-4s").IsInstalled);
        Assert.False(vm.Models.First(r => r.Descriptor.Id == "htdemucs-6s").IsInstalled);
    }

    [Fact]
    public void Active_row_reflects_stored_active_model_id()
    {
        var mgr = BuildManager();
        FakeInstall(mgr, "htdemucs-4s");
        mgr.ActiveModelId = "htdemucs-4s";

        using var vm = StemModelsRig.Create(mgr);

        Assert.True(vm.Models.First(r => r.Descriptor.Id == "htdemucs-4s").IsActive);
        Assert.False(vm.Models.First(r => r.Descriptor.Id == "htdemucs-6s").IsActive);
    }

    [Fact]
    public void Setting_a_row_active_clears_other_active_rows_and_updates_manager()
    {
        var mgr = BuildManager();
        FakeInstall(mgr, "htdemucs-4s");
        FakeInstall(mgr, "htdemucs-6s");
        mgr.ActiveModelId = "htdemucs-4s";

        using var vm = StemModelsRig.Create(mgr);
        var row6S = vm.Models.First(r => r.Descriptor.Id == "htdemucs-6s");

        row6S.IsActive = true;

        Assert.Equal("htdemucs-6s", mgr.ActiveModelId);
        Assert.True(row6S.IsActive);
        Assert.False(vm.Models.First(r => r.Descriptor.Id == "htdemucs-4s").IsActive);
    }

    [Fact]
    public void Row_can_command_states_track_install_state()
    {
        var mgr = BuildManager();
        using var vm = StemModelsRig.Create(mgr);
        var row = vm.Models.First();

        Assert.True(row.CanDownload);
        Assert.False(row.CanDelete);
        Assert.False(row.CanCancel);
        Assert.False(row.CanSetActive);

        FakeInstall(mgr, row.Descriptor.Id);
        row.RefreshState();

        Assert.False(row.CanDownload);
        Assert.True(row.CanDelete);
        Assert.True(row.CanSetActive);
    }

    [Fact]
    public void Size_label_renders_megabytes()
    {
        var mgr = BuildManager();
        using var vm = StemModelsRig.Create(mgr);
        var row = vm.Models.First();
        Assert.Contains("MB", row.SizeLabel);
    }
}
