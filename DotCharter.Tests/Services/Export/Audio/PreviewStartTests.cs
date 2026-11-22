using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Export.Rs7;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>
///     #456: the per-project preview start (m:ss.fff, blank = automatic): its text form, the save and DLC Builder
///     round trips, the build options, the clip's first frame, and the clip's fades and length against DLC Builder's
///     <c>Preview.fs</c> + <c>AudioFader.fs</c>.
/// </summary>
public sealed class PreviewStartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-previewstart-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(90_000, "1:30.000")]
    [InlineData(0, "0:00.000")]
    [InlineData(61_005, "1:01.005")]
    [InlineData(3_723_450, "62:03.450")]
    public void Start_formats_as_m_ss_fff_and_reads_back(double ms, string text)
    {
        Assert.Equal(text, PreviewStartTime.Format(ms));
        Assert.True(PreviewStartTime.TryParse(text, out var parsed));
        Assert.Equal(ms, parsed);
    }

    [Theory]
    [InlineData("1:30", 90_000)]
    [InlineData(" 1:30.5 ", 90_500)]
    [InlineData("90.25", 90_250)]
    [InlineData("0:07.123", 7_123)]
    public void Looser_spellings_are_read(string text, double ms)
    {
        Assert.True(PreviewStartTime.TryParse(text, out var parsed));
        Assert.Equal(ms, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_automatic_and_valid(string text)
    {
        Assert.True(PreviewStartTime.TryParse(text, out var parsed));
        Assert.Null(parsed);
        Assert.Equal("", PreviewStartTime.Format(null));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:2:3")]
    [InlineData("1:75")]
    [InlineData("-5")]
    [InlineData("1:-5")]
    [InlineData(":30")]
    [InlineData("1:")]
    public void Garbage_is_rejected(string text) => Assert.False(PreviewStartTime.TryParse(text, out _));

    [Fact]
    public void The_start_is_moved_back_so_a_whole_clip_fits()
    {
        Assert.Equal(32_000, PreviewClip.ClampStartMs(50_000, 60_000));
        Assert.Equal(0, PreviewClip.ClampStartMs(-5, 60_000));
        Assert.Equal(0, PreviewClip.ClampStartMs(1_000, 20_000));
        Assert.Equal(10_000, PreviewClip.ClampStartMs(10_000, 60_000));
    }

    [Fact]
    public void The_start_survives_a_save_and_load()
    {
        var project = Rs7PackageExportTests.CreateProject(4);
        project.Chart.PreviewStartMs = 91_500.5;
        var path = Path.Combine(_dir, "p.rscp");

        new RscpChartSerializer().Save(path, project);
        var loaded = new RscpChartSerializer().Load(path);

        Assert.Equal(91_500.5, loaded.Chart.PreviewStartMs);
        Assert.Contains("<dcPreviewStartMs>91500.5</dcPreviewStartMs>", File.ReadAllText(path));
    }

    [Fact]
    public void An_automatic_start_writes_nothing()
    {
        var project = Rs7PackageExportTests.CreateProject(4);
        var path = Path.Combine(_dir, "auto.rscp");

        new RscpChartSerializer().Save(path, project);

        Assert.DoesNotContain("dcPreviewStartMs", File.ReadAllText(path));
        Assert.Null(new RscpChartSerializer().Load(path).Chart.PreviewStartMs);
    }

    [Fact]
    public void A_DLC_Builder_start_in_seconds_is_the_projects_ms_and_back()
    {
        Assert.Equal(90_000, PreviewStartTime.FromRs2DlcSeconds(90));
        Assert.Equal(1_234, PreviewStartTime.FromRs2DlcSeconds(1.234));
        Assert.Null(PreviewStartTime.FromRs2DlcSeconds(null));
        Assert.Null(PreviewStartTime.FromRs2DlcSeconds(-1));
        Assert.Equal(90.5, PreviewStartTime.ToRs2DlcSeconds(90_500));
        Assert.Null(PreviewStartTime.ToRs2DlcSeconds(null));
    }

    [Fact]
    public void The_rs2dlc_carries_the_start_as_AudioPreviewStartTime()
    {
        var project = Rs7PackageExportTests.CreateProject(4);
        var options = new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("song.wav", "cover.png", 32_000, "Tester");

        Assert.Null(Rs2DlcProjectBuilder.Build(project, options).AudioPreviewStartTime);
        project.Chart.PreviewStartMs = 90_500;
        var dto = Rs2DlcProjectBuilder.Build(project, options);

        Assert.Equal(90.5, dto.AudioPreviewStartTime);
        Assert.Contains("\"AudioPreviewStartTime\": 90.5", Rs2DlcWriter.Serialize(dto));
        Assert.Equal(90_500, PreviewStartTime.FromRs2DlcSeconds(dto.AudioPreviewStartTime));
    }

    /// <summary>A ramp: frame f holds f / 200, so any sample says which source frame it came from.</summary>
    private static PcmAudio Ramp(int seconds, int sampleRate = 48_000)
    {
        var frames = seconds * sampleRate;
        var samples = new short[frames * 2];
        for (var f = 0; f < frames; f++) samples[f * 2] = samples[(f * 2) + 1] = (short)Math.Min(30_000, (f / 200) + 1);
        return new PcmAudio(samples, 2, sampleRate);
    }

    [Fact]
    public void The_clip_starts_at_the_chosen_frame_and_is_28_s_long()
    {
        var source = Ramp(120);
        var startFrame = 90 * 48_000;

        var clip = PreviewClip.Create(source, 90_000);

        Assert.Equal(28 * 48_000, clip.FrameCount);
        // Past the 2.5 s fade-in the clip is the source, frame for frame, from the chosen start.
        var probe = 5 * 48_000;
        Assert.Equal(source.Samples[(startFrame + probe) * 2], clip.Samples[probe * 2]);
        Assert.Equal(source.Samples[(startFrame + (48_000 * 10)) * 2], clip.Samples[48_000 * 10 * 2]);
    }

    [Fact]
    public void A_start_too_late_for_a_whole_clip_is_pulled_back()
    {
        var source = Ramp(60);

        var clip = PreviewClip.Create(source, 59_000);

        Assert.Equal(28 * 48_000, clip.FrameCount);
        Assert.Equal(source.Samples[((32 * 48_000) + (5 * 48_000)) * 2], clip.Samples[5 * 48_000 * 2]);
    }

    /// <summary>
    ///     DLC Builder's chain, ported statement for statement: <c>Skip(offset).Take(28 s)</c> through
    ///     <c>AudioFader</c> (fade-in multiplier pos/count, fade-out multiplier 1 - pos/count from
    ///     (length - fade-out)), where the fades halve for a song shorter than 5.5 s.
    /// </summary>
    private static short[] DlcBuilderChannel0(PcmAudio source, double startMs)
    {
        var sr = source.SampleRate;
        var ch = source.Channels;
        var audioMs = source.FrameCount * 1000.0 / sr;
        long fadeIn = 2500, fadeOut = 3000;
        if (audioMs < fadeIn + fadeOut) fadeIn = fadeOut = (long)(audioMs / 2);
        var previewMs = Math.Min((long)audioMs, 28_000);

        var skipFrames = (int)(startMs / 1000.0 * sr);
        var takeFrames = Math.Min((int)(28.0 * sr), source.FrameCount - skipFrames);
        var fadeInCount = (int)(fadeIn * sr / 1000);
        var fadeOutCount = (int)(fadeOut * sr / 1000);
        var fadeOutStartFrame = (previewMs - fadeOut) * sr / 1000;
        var result = new short[takeFrames];
        for (var f = 0; f < takeFrames; f++)
        {
            var v = source.Samples[(skipFrames + f) * ch] / 32768f;
            if (f <= fadeInCount) v *= f / (float)fadeInCount;
            if (f >= fadeOutStartFrame) v *= 1f - ((f - fadeOutStartFrame) / (float)fadeOutCount);
            result[f] = (short)(v * 32768f);
        }

        return result;
    }

    // 1 kHz keeps one frame per ms, so a fade step is large enough to tell apart in 16-bit.
    [Theory]
    [InlineData(100, 30_000)]
    [InlineData(100, 0)]
    [InlineData(20, 0)]
    [InlineData(4, 0)]
    [InlineData(100, 99_000)]
    public void The_fades_and_length_match_DLC_Builders_preview(int songSeconds, double startMs)
    {
        var samples = new short[songSeconds * 1_000 * 2];
        Array.Fill(samples, (short)30_000);
        var source = new PcmAudio(samples, 2, 1_000);
        var effectiveStart = PreviewClip.ClampStartMs(startMs, source.Duration.TotalMilliseconds);

        var clip = PreviewClip.Create(source, startMs);
        var expected = DlcBuilderChannel0(source, effectiveStart);

        Assert.Equal(expected.Length, clip.FrameCount);
        for (var f = 0; f < expected.Length; f++)
            Assert.True(Math.Abs(expected[f] - clip.Samples[f * 2]) <= 1, $"frame {f}: DLC Builder {expected[f]}, ours {clip.Samples[f * 2]}");
    }

    [Fact]
    public void The_fade_out_starts_at_full_level_and_ends_one_step_above_silence()
    {
        var samples = new short[40 * 1_000 * 2];
        Array.Fill(samples, (short)30_000);

        var clip = PreviewClip.Create(new PcmAudio(samples, 2, 1_000), 0);

        // 28 s clip, 3 s fade-out: it begins at 25 s on the first frame still at gain 1, as AudioFader does.
        Assert.Equal(30_000, clip.Samples[25_000 * 2]);
        Assert.InRange(clip.Samples[((28 * 1_000) - 1) * 2], 9, 10);
    }

    private RocksmithBuildOptions BuildOptions(string audio, string name, double? previewStartMs) => new()
    {
        OutputPath = Path.Combine(_dir, name + "_p.psarc"),
        AudioPath = audio,
        DlcKey = "previewstart",
        IncludeVocals = false,
        SongLengthMs = 20_000,
        PreviewStartMs = previewStartMs,
        PlainPackage = true
    };

    /// <summary>Silence that steps to a loud level at <paramref name="markerSeconds" />: the clip's first loud frame says where it was cut.</summary>
    private static PcmAudio Marker(int seconds, int markerSeconds)
    {
        var samples = new short[seconds * 48_000 * 2];
        Array.Fill(samples, (short)12_000, markerSeconds * 48_000 * 2, (seconds - markerSeconds) * 48_000 * 2);
        return new PcmAudio(samples, 2, 48_000);
    }

    private static int FirstLoudFrame(PcmAudio clip)
    {
        for (var f = 0; f < clip.FrameCount; f++)
        {
            if (Math.Abs(clip.Samples[f * 2]) > 5_000) return f;
        }

        return -1;
    }

    [Fact]
    public async Task The_build_cuts_the_preview_at_the_start_in_its_options()
    {
        var project = Rs7PackageExportTests.CreateProject(8);
        var audio = Path.Combine(_dir, "audio.wav");
        PcmAudioLoader.WriteWav(audio, Marker(100, 75));

        await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(
            project, BuildOptions(audio, "a", 70_000), progress: null, TestContext.Current.CancellationToken);

        var preview = PcmAudioLoader.Load(ManagedAudioCache.PreviewPath(Path.Combine(_dir, "audio.wav")), []);
        Assert.Equal(28 * 48_000, preview.FrameCount);
        // 70 s + 28 s fits in 100 s, so the clip starts at 70 s and the 75 s step is 5 s in.
        Assert.Equal(5 * 48_000, FirstLoudFrame(preview));
    }

    [Fact]
    public async Task The_projects_own_start_is_used_when_the_options_name_none_and_a_changed_start_re_cuts()
    {
        var project = Rs7PackageExportTests.CreateProject(8);
        var audio = Path.Combine(_dir, "song.wav");
        PcmAudioLoader.WriteWav(audio, Marker(100, 45));
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());

        project.Chart.PreviewStartMs = 40_000;
        await builder.BuildPsarcAsync(project, BuildOptions(audio, "b", null), null, TestContext.Current.CancellationToken);
        var first = PcmAudioLoader.Load(ManagedAudioCache.PreviewPath(Path.Combine(_dir, "song.wav")), []);
        Assert.Equal(5 * 48_000, FirstLoudFrame(first));

        // Back to automatic: a quarter into the song (25 s), not the earlier clip left on disk.
        project.Chart.PreviewStartMs = null;
        await builder.BuildPsarcAsync(project, BuildOptions(audio, "b", null), null, TestContext.Current.CancellationToken);
        var second = PcmAudioLoader.Load(ManagedAudioCache.PreviewPath(Path.Combine(_dir, "song.wav")), []);
        Assert.Equal(20 * 48_000, FirstLoudFrame(second));
    }
}
