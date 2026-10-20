using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     #313: the splice writes NEW padded files beside the originals, which it never touches (the undo
///     step points back at them), and it is all or nothing.
/// </summary>
public sealed class AudioSilenceSplicerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "splice-" + Guid.NewGuid().ToString("N"));

    public AudioSilenceSplicerTests() => Directory.CreateDirectory(Path.Combine(_dir, "stems"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(_dir, relative);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Wav(int frames) =>
        WavSilencePadderTests.Wav(100, 1, 16, [.. Enumerable.Range(0, frames * 2).Select(i => (byte)(i + 1))]);

    private static long DataLength(string path)
    {
        using var stream = File.OpenRead(path);
        return WavSilencePadder.ReadLayout(stream)!.DataLength;
    }

    /// <summary>#461: a padded stem takes the audio stream only, as the song encode does.</summary>
    [Fact]
    public void The_stem_encode_maps_the_audio_stream_only()
        => ImportAudioPreparerTests.AssertAudioOnly(
            AudioSilenceSplicer.BuildStemEncodeArgs("in.flac", "out.wav", "adelay=1000:all=1"), "out.wav");

    [Fact]
    public void The_next_slot_is_the_first_free_one_and_never_stacks_suffixes()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_dir, "song-silence1.ogg"),
            Path.Combine(_dir, "stems", "silence-2")
        };

        var slot = AudioSilenceSplicer.NextSlot(_dir, Path.Combine(_dir, "song-silence1.ogg"), hasStems: true,
            taken.Contains, taken.Contains);

        Assert.Equal(Path.Combine(_dir, "song-silence3"), slot.AudioStem);
        Assert.Equal(Path.Combine(_dir, "stems", "silence-3"), slot.StemDirectory);
        Assert.Equal(Path.Combine(_dir, "song-silence2"),
            AudioSilenceSplicer.NextSlot(_dir, Path.Combine(_dir, "song.wav"), hasStems: false, taken.Contains, taken.Contains).AudioStem);
    }

    [Fact]
    public async Task A_wave_song_and_its_stems_are_padded_without_ffmpeg_and_the_originals_are_kept()
    {
        var song = Write("song.wav", Wav(50));
        var drums = Write(Path.Combine("stems", "drums.wav"), Wav(50));
        var songBytes = File.ReadAllBytes(song);
        var splicer = new AudioSilenceSplicer(null, null, null,
            resolveFfmpeg: () => throw new InvalidOperationException("a WAVE needs no ffmpeg"));

        var result = await splicer.SpliceAsync(
            new SilenceSpliceRequest(song, [new SilenceSpliceFile("drums", drums)], _dir, 1.0, 0),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(_dir, "song-silence1.wav"), result.AudioPath);
        Assert.Equal(Path.Combine(_dir, "stems", "silence-1", "drums.wav"), Assert.Single(result.Stems).Path);
        Assert.Equal("drums", result.Stems[0].Name);
        Assert.Equal((100 + 50) * 2, DataLength(result.AudioPath!));
        Assert.Equal((100 + 50) * 2, DataLength(result.Stems[0].Path));
        Assert.Equal(songBytes, File.ReadAllBytes(song));
        Assert.True(File.Exists(drums));
    }

    [Fact]
    public async Task A_compressed_song_is_re_encoded_through_ffmpeg_with_the_bake_filter()
    {
        var song = Write("song.mp3", [1, 2, 3]);
        var calls = new List<IReadOnlyList<string>>();
        var splicer = new AudioSilenceSplicer(null, null, null, () => "ffmpeg.exe", (_, args) =>
        {
            calls.Add(args);
            File.WriteAllBytes(args[^1], [9]);
            return true;
        });

        var result = await splicer.SpliceAsync(new SilenceSpliceRequest(song, [], _dir, 0, 2.5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(_dir, "song-silence1.ogg"), result.AudioPath);
        var args = Assert.Single(calls);
        Assert.Contains("apad=pad_dur=2.5", args);
        Assert.Contains("libvorbis", args);
        Assert.Equal(song, args[args.ToList().IndexOf("-i") + 1]);
    }

    [Fact]
    public async Task Without_ffmpeg_a_compressed_song_reports_it_and_writes_nothing()
    {
        var song = Write("song.ogg", [1, 2, 3]);
        var splicer = new AudioSilenceSplicer(null, null, null, () => null, (_, _) => true);

        var result = await splicer.SpliceAsync(new SilenceSpliceRequest(song, [], _dir, 1, 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(SilenceSpliceFailure.NoFfmpeg, result.Failure);
        Assert.Equal(["song.ogg"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_stem_that_fails_takes_every_file_of_the_run_with_it()
    {
        var song = Write("song.wav", Wav(10));
        var bass = Write(Path.Combine("stems", "bass.wav"), Wav(10));
        var broken = Write(Path.Combine("stems", "vocals.flac"), [1]);
        var splicer = new AudioSilenceSplicer(null, null, null, () => "ffmpeg.exe", (_, _) => false);

        var result = await splicer.SpliceAsync(new SilenceSpliceRequest(song,
            [new SilenceSpliceFile("bass", bass), new SilenceSpliceFile("vocals", broken)], _dir, 1, 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(SilenceSpliceFailure.StemFailed, result.Failure);
        Assert.False(File.Exists(Path.Combine(_dir, "song-silence1.wav")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "stems", "silence-1")));
        Assert.True(File.Exists(song) && File.Exists(bass) && File.Exists(broken));
    }
}
