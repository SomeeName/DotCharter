using DotCharter.Services.Package.DcSong;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     The export dialog's door (#707): the probe says whether a package can be written at all,
///     how big the audio is and which separated stems exist, under the names a package may hold;
///     the size estimate and the stem names are pure.
/// </summary>
public sealed class DcSongExporterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-dcsong-exporter-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public async Task The_probe_offers_only_stems_on_disk_once_each_under_package_names()
    {
        var project = Directory.CreateDirectory(Path.Combine(_dir, "project")).FullName;
        var audioDir = Directory.CreateDirectory(Path.Combine(_dir, "audio")).FullName;
        var audio = Write(Path.Combine(audioDir, "song.wav"), 1000);
        Directory.CreateDirectory(Path.Combine(project, "stems"));
        Write(Path.Combine(project, "stems", "drums.wav"), 300);
        Write(Path.Combine(audioDir, "Vocals (lead).wav"), 200);
        var stems = new List<Stem>
        {
            new() { Name = "drums", Path = Path.Combine("stems", "drums.wav") },
            // An older build wrote the path against the AUDIO's folder; still found.
            new() { Name = "Vocals (lead)", Path = "Vocals (lead).wav" },
            new() { Name = "bass", Path = Path.Combine("stems", "bass.wav") },
            new() { Name = "drums-again", Path = Path.Combine("stems", "drums.wav") }
        };

        var probe = await Exporter(encoder: true).ProbeAsync(new DcSongProbeRequest(audio, project, stems),
            TestContext.Current.CancellationToken);

        Assert.True(probe.EncoderAvailable);
        Assert.True(probe.AudioExists);
        Assert.Equal(1000, probe.AudioBytes);
        Assert.Equal(["drums", "vocals--lead"], probe.Stems.Select(s => s.Name));
        Assert.Equal([300L, 200L], probe.Stems.Select(s => s.Bytes));
        Assert.All(probe.Stems, s => Assert.True(DcSongFormat.IsValidStemName(s.Name)));
    }

    [Fact]
    public async Task No_ffmpeg_and_no_audio_are_reported_not_thrown()
    {
        var probe = await Exporter(encoder: false).ProbeAsync(
            new DcSongProbeRequest(Path.Combine(_dir, "missing.wav"), "", []), TestContext.Current.CancellationToken);

        Assert.False(probe.EncoderAvailable);
        Assert.False(probe.AudioExists);
        Assert.Equal(0, probe.AudioBytes);
        Assert.Empty(probe.Stems);
    }

    [Fact]
    public async Task The_probe_reads_the_stem_list_it_was_given_not_a_list_edited_later()
    {
        var audio = Write(Path.Combine(_dir, "song.wav"), 10);
        Write(Path.Combine(_dir, "bass.wav"), 10);
        var stems = new List<Stem> { new() { Name = "bass", Path = "bass.wav" } };

        var task = Exporter(encoder: true).ProbeAsync(new DcSongProbeRequest(audio, _dir, stems), TestContext.Current.CancellationToken);
        stems.Clear();

        Assert.Equal(["bass"], (await task).Stems.Select(s => s.Name));
    }

    [Theory]
    [InlineData(new[] { "vocals", "drums" }, new[] { "vocals", "drums" })]
    [InlineData(new[] { "Vocals", "vocals", "VOCALS" }, new[] { "vocals", "vocals-2", "vocals-3" })]
    [InlineData(new[] { "Guitar (Lead)", "", "---" }, new[] { "guitar--lead", "stem", "stem-2" })]
    [InlineData(new[] { "Ärger über Gitarre" }, new[] { "rger--ber-gitarre" })]
    public void Stem_names_become_valid_unique_package_names(string[] names, string[] expected)
    {
        var result = DcSongStemNames.ForPackage(names);

        Assert.Equal(expected, result);
        Assert.All(result, n => Assert.True(DcSongFormat.IsValidStemName(n), n));
    }

    [Fact]
    public void A_long_stem_name_is_cut_to_the_format_limit_and_still_unique()
    {
        var name = new string('x', 40);

        var result = DcSongStemNames.ForPackage([name, name]);

        Assert.Equal(new string('x', 32), result[0]);
        Assert.Equal(new string('x', 30) + "-2", result[1]);
        Assert.All(result, n => Assert.True(DcSongFormat.IsValidStemName(n), n));
    }

    [Fact]
    public void The_size_estimate_counts_Opus_at_its_bit_rate_plus_the_preview()
    {
        // 180 s at 192 kb/s is 4,320,000 bytes; the 28 s preview 672,000.
        Assert.Equal(4_320_000 + 672_000 + DcSongSizeEstimate.OverheadBytes,
            DcSongSizeEstimate.Bytes(180_000, false, "song.ogg", 5_000_000, []));
        // A song shorter than a preview: the preview is the whole song.
        Assert.Equal(240_000 + 240_000 + DcSongSizeEstimate.OverheadBytes,
            DcSongSizeEstimate.Bytes(10_000, false, "song.ogg", 0, []));
    }

    [Theory]
    [InlineData("song.flac", 30_000_000, 30_000_000)]
    [InlineData("song.wav", 30_000_000, 19_500_000)]
    [InlineData("song.mp3", 6_000_000, 22_464_000)]
    public void A_lossless_estimate_follows_the_source(string audio, long sourceBytes, long expectedSongBytes) =>
        Assert.Equal(expectedSongBytes + 672_000 + DcSongSizeEstimate.OverheadBytes,
            DcSongSizeEstimate.Bytes(180_000, true, audio, sourceBytes, []));

    [Fact]
    public void Stems_count_at_the_Opus_rate_unless_they_are_Opus_already()
    {
        DcSongStemCandidate[] stems = [new("drums", "drums.wav", 31_000_000), new("vocals", "vocals.opus", 2_500_000)];

        Assert.Equal(4_320_000 + 2_500_000, DcSongSizeEstimate.StemBytes(180_000, stems));
        Assert.Equal(DcSongSizeEstimate.Bytes(180_000, false, "song.ogg", 0, []) + 6_820_000,
            DcSongSizeEstimate.Bytes(180_000, false, "song.ogg", 0, stems));
    }

    private DcSongExporter Exporter(bool encoder) => new(new FakeAppLogger(), () => null, () => encoder);

    private static string Write(string path, int bytes)
    {
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
