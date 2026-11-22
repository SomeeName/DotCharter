using System.Reflection;
using System.Text.Json;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export;

public sealed partial class SaveCompanionServiceTests
{
    [Theory]
    [InlineData(".wem")]
    [InlineData(".wav")]
    [InlineData(".ogg")]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    public void Missing_working_audio_never_uses_a_named_companion_sibling(string extension)
    {
        File.WriteAllText(Path.Combine(_dir, "song" + extension), "foreign audio");
        Assert.Null(SaveCompanionService.ResolveAudio(_dir, "song.aac"));
    }

    [Fact]
    public void Rule1_companions_use_export_and_the_project_file_name()
    {
        var path = Path.Combine(_dir, "project.dcproj");
        var project = CreateProject();
        var service = Create(new AppConfig { SaveCompanionEofAudio = false });
        var export = Directory.CreateDirectory(Path.Combine(_dir, "export")).FullName;
        Directory.CreateDirectory(DlcBuilderBundle.DirectoryFor(path));
        Assert.Equal(Path.Combine(export, "project.rscp"), service.WriteCharterProject(path, project));
        Assert.Equal(Path.Combine(export, "project.eof"), service.WriteEof(path, project));
        Assert.Equal(Path.Combine(export, "project.rs2dlc"), service.WriteDlcBuilderProject(path, project, null));
        Assert.Equal(Path.Combine(export, "DTRS XML"), DlcBuilderBundle.DirectoryFor(path));
        Assert.Equal(export, ExportPathDefaults.ExportDirectory(new AppConfig { LastExportDir = _dir }, path,
            new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter()));
    }

    [Fact]
    public void Rule2_companion_media_and_preview_resolve_inside_export()
    {
        var path = Path.Combine(_dir, "project.dcproj");
        var project = CreateProject();
        File.WriteAllText(Path.Combine(_dir, "song.wav"), "working audio");
        File.WriteAllText(Path.Combine(_dir, "cover.png"), "cover");
        project.Chart.CoverArtPath = "cover.png";
        project.Chart.PreviewStartMs = 1250;
        var ffmpeg = Path.Combine(_dir, "ffmpeg.exe");
        File.WriteAllText(ffmpeg, "injected runner");
        var arguments = new List<IReadOnlyList<string>>();
        var service = new SaveCompanionService(new FakeConfigurationService
        {
            AppConfig = new AppConfig { FfmpegPath = ffmpeg }
        }, new FakeAppLogger(), (_, args, _) =>
        {
            arguments.Add(args);
            File.WriteAllText(args[^1], "encoded audio");
            return new MediaProcess.MediaProcessResult(0, "");
        });
        Directory.CreateDirectory(DlcBuilderBundle.DirectoryFor(path));
        var rs2dlc = service.WriteDlcBuilderProject(path, project, 30000)!;
        using var json = JsonDocument.Parse(File.ReadAllText(rs2dlc));
        var export = Path.Combine(_dir, "export");
        foreach (var reference in new[] { "song.ogg", "song_preview.ogg", "cover.png" })
            Assert.True(File.Exists(Path.Combine(export, reference)), reference);
        Assert.Equal("song.ogg", json.RootElement.GetProperty("AudioFile").GetProperty("Path").GetString());
        Assert.Equal("song_preview.ogg", json.RootElement.GetProperty("AudioPreviewFile").GetProperty("Path").GetString());
        Assert.Equal("cover.png", json.RootElement.GetProperty("AlbumArtFile").GetString());
        Assert.Contains(arguments, args => args.Contains("-ss") && args.Contains("1.25"));
        var charter = service.WriteCharterProject(path, project)!;
        Assert.Equal("song.ogg", new RscpChartSerializer().Load(charter).AudioFileName);
        var eof = service.WriteEof(path, project)!;
        Assert.Equal("song.ogg", new DotCharter.Services.Parsing.Eof.EofImporter().ImportTracksWithMetadata(eof, []).Metadata.AudioFileName);
        Assert.Equal("song.wav", project.AudioFileName);
    }

    [Fact]
    public void Rule3_ownership_and_chat_live_in_hidden_metadata()
    {
        var service = Create(new AppConfig());
        service.WriteCharterProject(Path.Combine(_dir, "project.dcproj"), CreateProject());
        var metadata = Path.Combine(_dir, ".dotcharter");
        Assert.True(File.Exists(Path.Combine(metadata, "companions.json")));
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(metadata).HasFlag(FileAttributes.Hidden));
        var chart = new AiChatChart(Guid.NewGuid().ToString("N"), _dir);
        Assert.Equal(AiChatStatus.Ok, AiChatStore.TryOpen(chart, out var store));
        Assert.Equal(Path.Combine(metadata, "ai-chats", chart.ChartId), store!.FolderPath);
    }

    [Fact]
    public void Rule3_codec_changes_use_one_cache_entry_for_each_source()
    {
        var method = typeof(RocksmithPackageBuilder).GetMethod("ManagedWemCachePath", BindingFlags.Static | BindingFlags.NonPublic)!;
        var source = Path.Combine(_dir, "song.wav");
        var vorbis = (string)method.Invoke(null, [source, WemCodec.Vorbis, null])!;
        var pcm = (string)method.Invoke(null, [source, WemCodec.Pcm, null])!;
        Assert.Equal(vorbis, pcm);
        Assert.Equal(Path.Combine(_dir, ".dotcharter", "cache"), Path.GetDirectoryName(pcm));
    }

    [Fact]
    public async Task Rule3_codec_and_source_changes_replace_the_managed_audio_cache()
    {
        var source = Path.Combine(_dir, "song.wav");
        WriteWav(source);
        var project = CreateProject();
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());
        foreach (var codec in new[] { WemCodec.Vorbis, WemCodec.Pcm })
        {
            await builder.BuildPsarcAsync(project, new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(_dir, "export", "song_p.psarc"),
                AudioPath = source,
                AudioCodec = codec
            }, cancellationToken: CancellationToken.None);
        }
        var cache = Path.Combine(_dir, ".dotcharter", "cache");
        var wems = Directory.GetFiles(cache, "*.wem");
        Assert.Equal(2, wems.Length);
        foreach (var wem in wems)
        {
            using var stream = File.OpenRead(wem);
            Assert.Equal(WemCodec.Pcm, WemCodecInfo.Detect(stream));
        }
        var mainCache = ManagedAudioCache.WemPath(source);
        var before = File.ReadAllBytes(mainCache);
        var timestamp = File.GetLastWriteTimeUtc(source);
        PcmAudioLoader.WriteWav(source, new PcmAudio(Enumerable.Repeat((short)1000, 48000).ToArray(), 1, 48000));
        File.SetLastWriteTimeUtc(source, timestamp);
        await builder.BuildPsarcAsync(project, new RocksmithBuildOptions
        {
            OutputPath = Path.Combine(_dir, "export", "song_p.psarc"), AudioPath = source, AudioCodec = WemCodec.Pcm
        }, cancellationToken: CancellationToken.None);
        Assert.NotEqual(before, File.ReadAllBytes(mainCache));
        Assert.Equal(2, Directory.GetFiles(cache, "*.wem").Length);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task External_working_audio_gets_no_generated_siblings()
    {
        var external = Directory.CreateDirectory(Path.Combine(_dir, "shared")).FullName;
        var source = Path.Combine(external, "song.wav");
        WriteWav(source);
        var chart = Directory.CreateDirectory(Path.Combine(_dir, "chart")).FullName;
        await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(CreateProject(), new RocksmithBuildOptions
        {
            OutputPath = Path.Combine(chart, "export", "song_p.psarc"), AudioPath = source, ProjectDirectory = chart
        }, cancellationToken: CancellationToken.None);
        Assert.Equal(new[] { "song.wav" }, Directory.GetFileSystemEntries(external).Select(Path.GetFileName).ToArray());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(chart, ".dotcharter", "cache"), "*.wem").Length);
    }

    [Fact]
    public void Rule4_backups_and_stems_remain_visible_and_untouched()
    {
        foreach (var folder in new[] { "backups", "stems" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(_dir, folder)).FullName;
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
        }
        Create(new AppConfig()).WriteCharterProject(Path.Combine(_dir, "project.dcproj"), CreateProject());
        foreach (var folder in new[] { "backups", "stems" })
        {
            Assert.Equal("keep", File.ReadAllText(Path.Combine(_dir, folder, "keep.txt")));
            Assert.False(File.GetAttributes(Path.Combine(_dir, folder)).HasFlag(FileAttributes.Hidden));
        }
    }

    [Fact]
    public async Task Rule6_save_and_preview_build_leave_only_inputs_at_top_level()
    {
        var path = Path.Combine(_dir, "project.dcproj");
        WriteWav(Path.Combine(_dir, "song.wav"));
        var project = CreateProject();
        using var provider = new ServiceCollection().AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp").BuildServiceProvider();
        var documents = new ProjectDocumentService(provider, new FakeAppLogger());
        documents.SaveChart(path, project);
        var ffmpeg = Path.Combine(_dir, ".dotcharter", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ffmpeg)!);
        File.WriteAllText(ffmpeg, "injected runner");
        var service = new SaveCompanionService(new FakeConfigurationService
        {
            AppConfig = new AppConfig { FfmpegPath = ffmpeg }
        }, new FakeAppLogger(), (_, args, _) =>
        {
            File.WriteAllText(args[^1], "encoded audio");
            return new MediaProcess.MediaProcessResult(0, "");
        });
        service.WriteCharterProject(path, project);
        service.WriteEof(path, project);
        documents.SaveDlcBuilderXmlBundle(path, project, 1000);
        service.WriteDlcBuilderProject(path, project, 1000);
        Assert.NotNull(await service.BuildPsarcAsync(path, project, 1000, CancellationToken.None));
        Assert.Equal(AiChatStatus.Ok, AiChatStore.TryOpen(new AiChatChart(project.ProjectId, _dir), out var store));
        Assert.Equal(AiChatStatus.Ok, store!.Write(AiChatConversation.Start(project.ProjectId, "model", DateTimeOffset.UnixEpoch)).Status);
        Assert.Equal(new[] { "project.dcproj", "song.wav" }, Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(new[] { ".dotcharter", "export" }, Directory.GetDirectories(_dir).Select(Path.GetFileName).Order().ToArray());
    }
}
