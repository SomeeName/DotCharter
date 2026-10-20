using static DotCharter.Tests.Presentation.Settings.SplitTabStubs;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Files &amp; backups on the Presentation layer: the save
///     companions and timed backup (load, count, save, revert, reset, search),
///     the decoded-audio cache readout, and backup export / import — including
///     that a FAILED import still drops the caches it may have invalidated.
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class FilesSettingsViewModelTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "DotCharterTests", "files-tab-" + Guid.NewGuid().ToString("N"));

    private sealed record Rig(
        FilesSettingsViewModel Vm, BackupMigrateViewModel Backup, FakeConfigurationService Config,
        RecordingLocalization Loc, FakeFiles Files, FakeBackup Archive, SettingsSession Session,
        IPlayerProfileService Players, IProjectSessionService ProjectSession, FakeAppLogger Logger);

    private Rig NewRig(Action<AppConfig>? seed = null, bool withCache = true,
        DotCharter.Services.Plugins.IPackageFeatures? features = null)
    {
        var config = new FakeConfigurationService();
        seed?.Invoke(config.AppConfig);
        var loc = new RecordingLocalization();
        var files = new FakeFiles();
        var archive = new FakeBackup();
        var session = new SettingsSession();
        var players = Recorder<IPlayerProfileService>();
        var projectSession = Recorder<IProjectSessionService>();
        var logger = new FakeAppLogger();
        var stores = new ImportedConfigStores(config, players, Recorder<IPracticeProfileService>(),
            Recorder<IPhraseCatalogService>(), projectSession);
        var backup = new BackupMigrateViewModel(config, loc, files, archive, stores, session, logger);
        var cache = withCache ? new LibraryAudioCacheStore(rootOverride: _cacheRoot) : null;
        var vm = new FilesSettingsViewModel(config, loc, new TestUiDispatcher(), backup,
            features ?? DotCharter.Services.Plugins.PackageFeatures.All, cache, logger);
        vm.Load();
        return new Rig(vm, backup, config, loc, files, archive, session, players, projectSession, logger);
    }

    /// <summary>The measure runs on a worker; a 60 s cap is a deadlock detector, never a budget.</summary>
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"{what} never happened");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true);
    }

    /// <summary>
    ///     Without the package reader (#887) the decoded-audio cache and the open options
    ///     are ABSENT — hidden before any search and never found by one — while the companion
    ///     <c>.psarc</c> rows (the writer) stay. The fake's off variant hides the same keys.
    /// </summary>
    [Fact]
    public void WithoutThePackageReader_ThePackageGroupsAreAbsent_AndTheWriterRowsStay()
    {
        var off = NewRig(withCache: false, features: DotCharter.Services.Plugins.PackageFeatures.None).Vm;
        var on = NewRig(withCache: false, features: DotCharter.Services.Plugins.PackageFeatures.All).Vm;
        string[] reader = ["files.group.2", "files.psarcCache", "files.group.3", "files.psarcHardestOnly"];

        foreach (var key in reader)
        {
            Assert.Contains(key, off.HiddenKeys);
            Assert.DoesNotContain(key, on.HiddenKeys);
        }

        Assert.DoesNotContain("files.psarc", off.HiddenKeys);
        Assert.DoesNotContain("files.psarcCodec", off.HiddenKeys);
        Assert.DoesNotContain("files.rsXmlSixString", off.HiddenKeys);
        Assert.Equal(DotCharter.Presentation.Fakes.Settings.FakeFilesSettingsScreen.PackageReaderKeys.Order(), reader.Order());

        // A search keeps them hidden, and the cross-tab search finds nothing in them.
        var hardest = new RecordingLocalization()["Settings_PsarcOpenHardestOnly"];
        off.ApplySearch(hardest);
        Assert.Contains("files.psarcHardestOnly", off.HiddenKeys);
        Assert.Empty(SettingsSearch.Hits([new SettingsSearchSource("files", "Files", off.SearchGroups)], hardest));
        Assert.NotEmpty(SettingsSearch.Hits([new SettingsSearchSource("files", "Files", on.SearchGroups)], hardest));
    }

    [Fact]
    public void Load_ReadsThePersistedValues()
    {
        var rig = NewRig(c =>
        {
            c.SaveCompanionPsarcCodec = "pcm";
            c.SaveCompanionEof = false;
            c.AutoBackupIntervalMinutes = 0;
        });

        Assert.Equal(1, rig.Vm.SaveCompanionPsarcCodecIndex);   // 0 = Vorbis, 1 = PCM
        Assert.False(rig.Vm.SaveCompanionEof);
        Assert.Equal(5, rig.Vm.AutoBackupIntervalMinutes);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void EverySettableMember_CountsOnce_AndPuttingItBackCountsNothing() =>
        SplitTabAssert.EverySettableMember_CountsOnce<IFilesSettingsScreen>(() => NewRig(withCache: false).Vm, atLeast: 9);

    [Fact]
    public void Save_PersistsEveryRow_AndClearsTheCount()
    {
        var rig = NewRig();
        rig.Vm.SaveCompanionPsarcCodecIndex = 1;
        rig.Vm.SaveCompanionRs2Dlc = false;
        rig.Vm.RsXmlSixStringProjection = true;
        rig.Vm.ExportGenerateDynamicDifficulty = false;
        rig.Vm.ExportDdUseMlModel = true;
        rig.Vm.ExportApplyDlcBuilderFixes = false;
        rig.Vm.AutoBackupEnabled = false;
        rig.Vm.AutoBackupIntervalMinutes = 0;

        rig.Vm.Save();

        var c = rig.Config.AppConfig;
        Assert.Equal("Pcm", c.SaveCompanionPsarcCodec);
        Assert.False(c.SaveCompanionRs2Dlc);
        Assert.True(c.RsXmlSixStringProjection);
        Assert.False(c.ExportGenerateDynamicDifficulty);
        Assert.Equal("MLModel", c.ExportDdLevelCountMode);
        Assert.False(c.ExportApplyDlcBuilderFixes);
        Assert.False(c.AutoBackupEnabled);
        Assert.Equal(1, c.AutoBackupIntervalMinutes);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void Revert_AndReset_PutThePersistedValuesBack()
    {
        foreach (var verb in new Action<FilesSettingsViewModel>[] { v => v.Revert(), v => v.Reset() })
        {
            var rig = NewRig();
            rig.Vm.SaveCompanionRs2Dlc = !rig.Vm.SaveCompanionRs2Dlc;

            verb(rig.Vm);

            Assert.Equal(rig.Config.AppConfig.SaveCompanionRs2Dlc, rig.Vm.SaveCompanionRs2Dlc);
            Assert.Empty(rig.Vm.UnsavedSettings);
        }
    }

    /// <summary>Loading measures the cache, and the readout is STATUS: measuring must not read as an unsaved edit.</summary>
    [Fact]
    public async Task Loading_MeasuresTheDecodedAudioCache_WithoutCountingAChange()
    {
        Directory.CreateDirectory(Path.Combine(_cacheRoot, "song_a_1"));
        File.WriteAllBytes(Path.Combine(_cacheRoot, "song_a_1", "audio.wav"), new byte[2048]);

        var rig = NewRig();

        await WaitFor(() => rig.Vm.LibraryAudioCacheHasEntries, "the load-time cache measure");
        Assert.Contains("1", rig.Vm.LibraryAudioCacheUsage, StringComparison.Ordinal);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public async Task Clearing_TheCache_SaysWhatWentAndReMeasures()
    {
        Directory.CreateDirectory(Path.Combine(_cacheRoot, "song_b_1"));
        var rig = NewRig();
        await WaitFor(() => rig.Vm.LibraryAudioCacheHasEntries, "the load-time cache measure");

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)rig.Vm.ClearLibraryAudioCacheCommand).ExecuteAsync(null);

        Assert.True(rig.Vm.HasLibraryAudioCacheDetail);
        Assert.False(rig.Vm.LibraryAudioCacheHasEntries);
        Assert.Equal(rig.Loc["Settings_PsarcAudioCache_Empty"], rig.Vm.LibraryAudioCacheUsage);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public async Task Export_FlushesTheConfig_AndWritesTheArchive()
    {
        var rig = NewRig();
        rig.Files.Answer = @"D:\backup.zip";

        await rig.Backup.ExportAsync();

        Assert.Equal([@"export:D:\backup.zip"], rig.Archive.Calls);
        Assert.True(rig.Backup.HasStatus);
        Assert.False(rig.Backup.IsBusy);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public async Task ACancelledPicker_DoesNothing()
    {
        var rig = NewRig();

        await rig.Backup.ExportAsync();
        await rig.Backup.ImportAsync();

        Assert.Empty(rig.Archive.Calls);
        Assert.False(rig.Backup.HasStatus);
    }

    /// <summary>
    ///     An import replaced files behind every caching service: the caches go,
    ///     the session re-reads the config, the imported language and log filter
    ///     apply, and the page is asked to reload every tab.
    /// </summary>
    [Fact]
    public async Task Import_DropsTheCaches_ReappliesTheLanguage_AndReloadsThePage()
    {
        var rig = NewRig();
        rig.Files.Answer = @"D:\backup.zip";
        rig.Config.AppConfig.LanguageCulture = "de-DE";
        var reloads = rig.Session.ReloadRequests;

        await rig.Backup.ImportAsync();

        Assert.Equal([@"import:D:\backup.zip"], rig.Archive.Calls);
        Assert.Contains("InvalidateCache", CallsOf(rig.Players));
        Assert.Contains("LoadAppConfig", CallsOf(rig.ProjectSession));
        Assert.Equal(["de-DE"], rig.Loc.CulturesSet);
        Assert.True(rig.Logger.ReloadSettingsCalled);
        Assert.Equal(reloads + 1, rig.Session.ReloadRequests);
        Assert.True(rig.Backup.HasStatus);
    }

    /// <summary>A part-way import may still have replaced files, so the caches are dropped even when it throws.</summary>
    [Fact]
    public async Task AFailedImport_StillDropsTheCaches_AndReportsTheFailure()
    {
        var rig = NewRig();
        rig.Files.Answer = @"D:\broken.zip";
        rig.Archive.Throw = new InvalidDataException("not a DotCharter backup");
        var reloads = rig.Session.ReloadRequests;

        await rig.Backup.ImportAsync();

        Assert.Contains("InvalidateCache", CallsOf(rig.Players));
        Assert.Contains("not a DotCharter backup", rig.Backup.StatusText, StringComparison.Ordinal);
        Assert.Equal(reloads, rig.Session.ReloadRequests);
        Assert.Empty(rig.Loc.CulturesSet);
        Assert.False(rig.Backup.IsBusy);
    }

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys() => SplitTabAssert.TheSearchCatalogue_HasNoRawResourceKeys(NewRig().Vm);

    [Fact]
    public void Search_HidesWhatDoesNotMatch()
    {
        var rig = NewRig();
        var migrate = rig.Vm.SearchGroups[1].Rows.Single(r => r.Key == "files.backupMigrate");

        rig.Vm.ApplySearch(migrate.Label);

        Assert.DoesNotContain("files.backupMigrate", rig.Vm.HiddenKeys);
        Assert.Contains(FilesSettingsViewModel.GroupKey(0), rig.Vm.HiddenKeys);

        rig.Vm.ApplySearch("");
        Assert.Empty(rig.Vm.HiddenKeys);
    }

    [Fact]
    public void TheScreen_ConformsToItsContract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IFilesSettingsScreen), NewRig(withCache: false).Vm));
}
