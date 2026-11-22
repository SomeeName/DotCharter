using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export;

public sealed class ExportCompanionMediaReviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("companion_media_review_").FullName;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Direct_sources_are_copied_and_export_edits_leave_sources_unchanged(bool audio)
    {
        var source = Path.Combine(_dir, audio ? "working.ogg" : "artwork.png");
        File.WriteAllText(source, "user source");
        var links = new List<string>();
        var copies = new List<string>();
        var media = Create(links, copies);
        var target = Path.Combine(_dir, "export", audio ? "song.ogg" : "cover.png");

        Ensure(media, source, audio);

        Assert.Equal("user source", File.ReadAllText(target));
        Assert.DoesNotContain(source, links);
        Assert.Contains(source, copies);
        File.WriteAllText(target, "edited in the other editor");
        Assert.Equal("user source", File.ReadAllText(source));
        Assert.False(CompanionOwnership.IsUnmodified(target));
        Ensure(media, source, audio);
        Assert.Equal("edited in the other editor", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Changing_a_direct_source_leaves_the_export_owned_and_allows_refresh(bool audio)
    {
        var source = Path.Combine(_dir, audio ? "working.ogg" : "artwork.png");
        File.WriteAllText(source, "original source");
        var media = Create([], []);
        var target = Path.Combine(_dir, "export", audio ? "song.ogg" : "cover.png");
        Ensure(media, source, audio);

        File.WriteAllText(source, "changed source");

        Assert.Equal("original source", File.ReadAllText(target));
        Assert.True(CompanionOwnership.IsUnmodified(target));
        Ensure(media, source, audio);
        Assert.Equal("changed source", File.ReadAllText(target));
        Assert.True(CompanionOwnership.IsUnmodified(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rendered_cache_media_uses_links_with_copy_fallback(bool failLink)
    {
        var source = Path.Combine(_dir, "working.wav");
        var cover = Path.Combine(_dir, "artwork.jpg");
        File.WriteAllText(source, "user audio");
        File.WriteAllText(cover, "user cover");
        var links = new List<string>();
        var copies = new List<string>();
        var media = Create(links, copies, failLink);

        media.Ensure(Path.Combine(_dir, "project.dcproj"), new ChartProject(), 60000, Config(), source, cover);

        var cache = Path.Combine(_dir, ".dotcharter", "cache");
        Assert.Equal(3, links.Count);
        Assert.All(links, path => Assert.Equal(cache, Path.GetDirectoryName(path)));
        Assert.Equal(failLink ? 3 : 0, copies.Count);
        Assert.Equal("rendered", File.ReadAllText(Path.Combine(_dir, "export", "song.ogg")));
        Assert.Equal("rendered", File.ReadAllText(Path.Combine(_dir, "export", "song_preview.ogg")));
        Assert.Equal("rendered", File.ReadAllText(Path.Combine(_dir, "export", "cover.png")));
    }

    [Fact]
    public void External_output_resolves_sources_and_keeps_render_cache_under_the_actual_project()
    {
        var projectRoot = Directory.CreateDirectory(Path.Combine(_dir, "project")).FullName;
        var output = Path.Combine(_dir, "external");
        File.WriteAllText(Path.Combine(projectRoot, "working.wav"), "user audio");
        File.WriteAllText(Path.Combine(projectRoot, "artwork.jpg"), "user cover");
        var project = new ChartProject { AudioFileName = "working.wav", Chart = { CoverArtPath = "artwork.jpg" } };
        var links = new List<string>();
        var media = Create(links, []);

        media.Ensure(Path.Combine(projectRoot, "project.dcproj"), project, 60000, Config(), outputDirectory: output);

        Assert.Equal("rendered", File.ReadAllText(Path.Combine(output, "song.ogg")));
        Assert.Equal("rendered", File.ReadAllText(Path.Combine(output, "cover.png")));
        Assert.Equal(3, links.Count);
        Assert.All(links, path => Assert.Equal(Path.Combine(projectRoot, ".dotcharter", "cache"), Path.GetDirectoryName(path)));
        Assert.False(Directory.Exists(Path.Combine(output, ".dotcharter", "cache")));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private void Ensure(ExportCompanionMedia media, string source, bool audio) =>
        media.Ensure(Path.Combine(_dir, "project.dcproj"), new ChartProject(), 60000, Config(),
            workingAudio: audio ? source : null, cover: audio ? null : source);

    private AppConfig Config()
    {
        var ffmpeg = Path.Combine(_dir, "ffmpeg.exe");
        File.WriteAllText(ffmpeg, "the runner is injected");
        return new AppConfig { FfmpegPath = ffmpeg };
    }

    private static ExportCompanionMedia Create(List<string> links, List<string> copies, bool failLink = false) =>
        new(new FakeAppLogger(), (_, args, _) =>
        {
            File.WriteAllText(args[^1], "rendered");
            return new MediaProcess.MediaProcessResult(0, "");
        }, (target, source) =>
        {
            links.Add(source);
            if (failLink) throw new IOException("unsupported volume");
            File.Copy(source, target);
        }, (source, target) =>
        {
            copies.Add(source);
            File.Copy(source, target);
        });
}
