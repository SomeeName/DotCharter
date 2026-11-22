using System.Collections.Concurrent;
using System.Reflection;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Session;
using DotCharter.Services.Library.Cache;
using DotCharter.Tests.Services.Library;
using DotCharter.Tests.Services.Package.DcSong;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Panels;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Session;

public sealed partial class ProjectSessionAttachAudioTests
{
    [Theory]
    [InlineData("library", "save-as")]
    [InlineData("library", "save")]
    [InlineData("new", "save")]
    [InlineData("new", "save-as")]
    [InlineData("new", "autosave")]
    public async Task Saving_through_production_flows_preserves_section_results(string source, string operation)
    {
        var registrations = new ServiceCollection();
        registrations.AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp");
        using var services = registrations.BuildServiceProvider();
        var documents = new ProjectDocumentService(services, new FakeAppLogger());
        var h = new Harness(new ChartProject(), "", audio: new SectionAudioPlaybackService(), documents: documents);
        using var session = h.Session;
        if (source == "library") await OpenSectionLibrarySong(h);
        else await CreateSectionProject(h, documents);
        var applied = 0;
        var saved = 0;
        using var panel = SectionPanel(h, () => applied++, () => saved++);
        var row = AddChosenSection(panel);
        var result = panel.Result;
        var revision = h.State.SongAudioRevision;

        if (operation == "autosave")
        {
            RunSectionBackupTick(h, documents);
        }
        else
        {
            h.Storage.NextSaveFile = Path.Combine(_tempDir, "saved", "variant.dcproj");
            Directory.CreateDirectory(Path.GetDirectoryName(h.Storage.NextSaveFile)!);
            Assert.True(operation == "save-as"
                ? await session.SaveProjectAsAsync()
                : await session.SaveProjectAsync(SaveInitiator.User));
            await session.Saver.FlushCompanionsAsync();
        }

        // CanExecute refreshes ownership, as do both result-consuming commands.
        Assert.True(panel.HearItCommand.CanExecute(null));
        panel.HearItCommand.Execute(null);
        Assert.True(panel.SaveToneCommand.CanExecute(null));
        panel.SaveToneCommand.Execute(null);
        Assert.Equal(1, applied);
        Assert.Equal(1, saved);
        Assert.Same(row, Assert.Single(panel.Sections.Sections));
        Assert.Single(row.Candidates);
        Assert.Same(result, panel.Result);
        Assert.True(panel.HasResult);
        Assert.Equal(revision, h.State.SongAudioRevision);
    }

    private async Task OpenSectionLibrarySong(Harness h)
    {
        var library = Directory.CreateDirectory(Path.Combine(_tempDir, "library")).FullName;
        DcSongLibraryKit.Write(Path.Combine(library, "song.dcsong"),
            DcSongLibraryKit.Song("Song", "Band"), "song", lossless: true);
        var cache = Path.Combine(_tempDir, "cache");
        var packages = DcSongLibraryKit.Source(cache, new DcSongTestKit.FakeDecoder(available: true), h.Logger);
        var service = TestLibraries.Create(h.Logger, null, new LibraryAudioCacheStore(h.Logger, cache), packages);
        var entry = Assert.Single(await service.ScanAsync(library));
        var browser = new SongBrowserViewModel(h.Storage, h.Config, service, h.Logger, h.Session,
            h.State, h.Editor, h.Transport, new DotCharter.Presentation.Dialogs.DialogsSlot(),
            FakeLocalization.English(), playability: service);
        Assert.True(await browser.LoadSongItem(new PsarcSongListItem(entry, FakeLocalization.English(), packages.BlockFor(entry))));
        Assert.Equal("song.flac", h.State.ActiveProject!.AudioFileName);
        Assert.Empty(h.State.CurrentProjectPath);
    }

    private async Task CreateSectionProject(Harness h, IProjectDocumentService documents)
    {
        h.Storage.NextFolder = _tempDir;
        h.Config.AppConfig.PadSilenceOnNewProject = false;
        var creator = new NewProjectCreator(h.Editor, h.Transport, h.State, h.Session, h.Storage,
            h.Config, documents, new TempoService(h.Logger), new ExportTargetWriter(), h.Logger,
            static (src, dir, _, _) =>
            {
                var target = Path.Combine(dir, "song.wav");
                File.Copy(src, target);
                return new ImportAudioResult("song.wav", null, false);
            }, FakeLocalization.English());
        await creator.CreateAsync(new NewProjectDetails("Band", "Song", "", "", "", "", "", "", null,
            CreateSourceAudio("source.wav")));
        Assert.Equal("song.wav", h.State.ActiveProject!.AudioFileName);
        Assert.False(File.Exists(h.State.CurrentProjectPath));
    }

    private static void RunSectionBackupTick(Harness h, IProjectDocumentService documents)
    {
        using var dirty = new ProjectDirtyTracker(h.History);
        using var backup = new ProjectAutoBackup(h.Config, dirty, documents, h.State, h.Transport,
            h.Editor, new TestUiDispatcher(), h.Logger, FakeLocalization.English());
        dirty.MarkDirty();
        using var context = new SectionBackupContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            typeof(ProjectAutoBackup).GetField("_lastBackupUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(backup, DateTimeOffset.MinValue);
            backup.Tick();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        context.CompleteWrite();
        Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(h.State.CurrentProjectPath)!, "backups"), "*.zip"));
    }

    private sealed class SectionBackupContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = [];

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public void CompleteWrite()
        {
            Assert.True(_posted.TryTake(out var work, TimeSpan.FromSeconds(30)), "Backup continuation was never posted.");
            work.Callback(work.State);
        }

        public void Dispose() => _posted.Dispose();
    }
}
