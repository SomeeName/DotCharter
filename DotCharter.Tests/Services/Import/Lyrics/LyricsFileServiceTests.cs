using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Import.Lyrics;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.Lyrics;

public sealed class LyricsFileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-lyricsfile-" + Guid.NewGuid().ToString("N"));

    public LyricsFileServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static List<BeatMarker> Grid()
        => Enumerable.Range(0, 40).Select(i => new BeatMarker
        {
            Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0
        }).ToList();

    private static LyricsFileService Service(IExportTargetWriter? files = null)
        => new(files ?? new ExportTargetWriter(), new LrcLyricsParser());

    [Fact]
    public async Task ReadLrc_parses_a_file_on_disk_into_phrases()
    {
        var path = Path.Combine(_dir, "a.lrc");
        await File.WriteAllTextAsync(path, "[00:01.00]hello world\n[00:03.00]second line\n", TestContext.Current.CancellationToken);

        var phrases = await Service().ReadLrcAsync(path, Grid());

        Assert.Equal(2, phrases.Count);
        Assert.Contains(phrases[0].Lyrics, l => l.Text.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadUltraStar_returns_one_track_per_voice_and_the_relative_flag()
    {
        var path = Path.Combine(_dir, "duet.txt");
        await File.WriteAllTextAsync(path,
            "#BPM:120\n#GAP:0\nP1\n: 0 4 0 Hel\n- 8\nP2\n: 2 4 5 Hi\nE\n", TestContext.Current.CancellationToken);

        var imported = await Service().ReadUltraStarAsync(path, Grid());

        Assert.Equal(2, imported.Voices.Count);
        Assert.False(imported.IsRelative);
    }

    [Fact]
    public async Task A_file_over_the_cap_is_refused_before_it_is_read()
    {
        var files = new OversizedFileWriter(ImportSizeLimits.LyricsTextFileBytes + 1);

        await Assert.ThrowsAsync<ImportFileTooLargeException>(() => Service(files).ReadTextAsync("big.lrc"));

        Assert.Equal(0, files.Reads);
    }

    [Fact]
    public async Task WriteAsync_replaces_an_existing_file_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "out.lrc");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);

        await Service().WriteAsync(path, "new content");

        Assert.Equal("new content", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData(null, "lyrics")]
    [InlineData("   ", "lyrics")]
    [InlineData("AC/DC: Live", "AC_DC_ Live")]
    [InlineData("  Song  ", "Song")]
    public void SuggestFileName_replaces_invalid_characters_and_falls_back(string? name, string expected)
        => Assert.Equal(expected, Service().SuggestFileName(name));

    [Fact]
    public void BuildUltraStar_keeps_only_the_audio_file_name()
    {
        var track = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Text = "hi", Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(1)
        });
        track.Phrases.Add(phrase);

        var text = Service().BuildUltraStar(track, Grid(), 120, "T", "A", @"C:\music\song.ogg");

        Assert.Contains("song.ogg", text, StringComparison.Ordinal);
        Assert.DoesNotContain("music", text, StringComparison.Ordinal);
    }
}
