using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Import.Audio;
using DotCharter.Tests.Analysis;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     Chart-from-audio listens to the arrangement instrument's ALREADY separated,
///     LOSSLESS stem (#391), out of a fake stem cache in a temp directory — never the
///     user's. The guards that matter: an Opus-decoded stem is never taken, a stem in an
///     unkeyed folder must be as long as the mix, and nothing is ever written.
/// </summary>
public sealed class ChartFromAudioStemLocatorTests : IDisposable
{
    private const int Rate = AnalysisTestSignals.SampleRate;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public ChartFromAudioStemLocatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string CacheRoot => Path.Combine(_dir, "cache");

    private ChartFromAudioStemLocator Locator() => new(CacheRoot);

    private string Mix(double seconds = 2.0, double pluckHz = 110.0)
    {
        var song = Path.Combine(_dir, "song");
        Directory.CreateDirectory(song);
        return WriteWav(Path.Combine(song, "mix.wav"), seconds, pluckHz);
    }

    private static string WriteWav(string path, double seconds, double pluckHz)
    {
        var buffer = new float[(int)(seconds * Rate)];
        AnalysisTestSignals.AddPluck(buffer, 0.5, pluckHz);
        WriteSamples(path, buffer);
        return path;
    }

    private static string Token(string mix) =>
        StemCacheSource.TryResolve(StemCacheIdentity.ForAudioFile(mix), mix)!.Key.DirectoryToken;

    private string PlaybackDir(string mix) => Path.Combine(CacheRoot, StemIsolator.PlaybackFolderName, Token(mix));

    private string RawDir(string mix) => Path.Combine(PlaybackDir(mix), StemIsolator.RawFolderName);

    /// <summary>An archive for <paramref name="mix" />'s key holding the given stems.</summary>
    private void WriteArchive(string mix, StemCodec codec, params string[] stems)
    {
        var source = StemCacheSource.TryResolve(StemCacheIdentity.ForAudioFile(mix), mix)!;
        var staging = Path.Combine(_dir, "staging-" + Guid.NewGuid().ToString("N"));
        var files = new List<(string, string)>();
        var manifest = new StemCacheManifest { Key = source.Key.Canonical, Codec = codec };
        foreach (var stem in stems)
        {
            var entry = stem + "." + codec.FileExtension();
            files.Add((entry, WriteWav(Path.Combine(staging, entry), 2.0, 220)));
            manifest.Stems.Add(new StemManifestEntry { Name = stem, FileName = entry });
        }

        Directory.CreateDirectory(CacheRoot);
        StemArchive.Write(Path.Combine(CacheRoot, source.Key.DirectoryToken + StemArchive.ArchiveExtension), manifest, files);
    }

    private static List<string> Snapshot(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : [];

    [Fact]
    public void A_bass_arrangement_takes_the_bass_stem_the_separator_left_raw()
    {
        var mix = Mix();
        WriteWav(Path.Combine(RawDir(mix), "guitar.wav"), 2.0, 220);
        var bass = WriteWav(Path.Combine(RawDir(mix), "bass.wav"), 2.0, 55);

        var stem = Locator().TryLocate(mix, ArrangementType.Bass);

        Assert.NotNull(stem);
        Assert.Equal("bass", stem.StemName);
        Assert.Equal(bass, stem.Path);
        Assert.Null(stem.ArchiveEntry);
    }

    [Theory]
    [InlineData(ArrangementType.Lead)]
    [InlineData(ArrangementType.Rhythm)]
    [InlineData(ArrangementType.Combo)]
    public void A_guitar_arrangement_takes_guitar_before_other(ArrangementType type)
    {
        var mix = Mix();
        WriteWav(Path.Combine(RawDir(mix), "other.wav"), 2.0, 220);
        Assert.Equal("other", Locator().TryLocate(mix, type)?.StemName);

        WriteWav(Path.Combine(RawDir(mix), "guitar.wav"), 2.0, 220);
        Assert.Equal("guitar", Locator().TryLocate(mix, type)?.StemName);
    }

    /// <summary>A bass has no fallback: "other" is defined as everything EXCEPT the bass.</summary>
    [Fact]
    public void A_bass_arrangement_never_falls_back_to_other_or_guitar()
    {
        var mix = Mix();
        WriteWav(Path.Combine(RawDir(mix), "other.wav"), 2.0, 220);
        WriteWav(Path.Combine(RawDir(mix), "guitar.wav"), 2.0, 220);

        Assert.Null(Locator().TryLocate(mix, ArrangementType.Bass));
    }

    [Fact]
    public void A_vocal_arrangement_has_no_stem_to_take() =>
        Assert.Empty(ChartFromAudioStemLocator.SourcesFor(ArrangementType.Vocal));

    /// <summary>
    ///     THE guard: WAVs the app expanded from an Opus archive are decoded Opus, and
    ///     Opus is playback-only. The same folder without the marker is taken, so the
    ///     refusal is the marker's doing and not an accident of the fixture.
    /// </summary>
    [Fact]
    public void Wavs_decoded_from_an_opus_archive_are_never_taken()
    {
        var mix = Mix();
        var playback = PlaybackDir(mix);
        WriteWav(Path.Combine(playback, "guitar.wav"), 2.0, 220);
        Assert.Equal("guitar", Locator().TryLocate(mix, ArrangementType.Lead)?.StemName);

        WriteArchive(mix, StemCodec.Opus, "guitar");
        File.WriteAllText(Path.Combine(playback, StemCacheStore.ExpandedMarkerFileName), Token(mix));

        Assert.Null(Locator().TryLocate(mix, ArrangementType.Lead));
    }

    /// <summary>A marker naming an archive that is gone: what the WAVs were decoded from cannot be told.</summary>
    [Fact]
    public void Wavs_whose_marker_names_a_missing_archive_are_refused()
    {
        var mix = Mix();
        var playback = PlaybackDir(mix);
        WriteWav(Path.Combine(playback, "guitar.wav"), 2.0, 220);
        File.WriteAllText(Path.Combine(playback, StemCacheStore.ExpandedMarkerFileName), Token(mix));

        Assert.Null(Locator().TryLocate(mix, ArrangementType.Lead));
    }

    [Fact]
    public void A_wav_archive_is_read_in_memory_and_an_opus_one_is_not_read_at_all()
    {
        var mix = Mix();
        WriteArchive(mix, StemCodec.Opus, "guitar", "bass");
        Assert.Null(Locator().TryLocate(mix, ArrangementType.Lead));

        WriteArchive(mix, StemCodec.Wav, "guitar", "bass");
        var before = Snapshot(CacheRoot);
        var stem = Locator().TryLocate(mix, ArrangementType.Bass);

        Assert.NotNull(stem);
        Assert.Equal("bass", stem.StemName);
        Assert.Equal("bass.wav", stem.ArchiveEntry);
        var (samples, rate) = WavMonoReader.Read(stem.ReadWavBytes());
        Assert.Equal(Rate, rate);
        Assert.Equal(2 * Rate, samples.Length);
        Assert.Equal(before, Snapshot(CacheRoot));
    }

    /// <summary>A WAV archive that records a different key is somebody else's stems.</summary>
    [Fact]
    public void A_wav_archive_recording_another_key_is_not_taken()
    {
        var mix = Mix();
        var source = StemCacheSource.TryResolve(StemCacheIdentity.ForAudioFile(mix), mix)!;
        var staging = WriteWav(Path.Combine(_dir, "x", "guitar.wav"), 2.0, 220);
        Directory.CreateDirectory(CacheRoot);
        StemArchive.Write(Path.Combine(CacheRoot, source.Key.DirectoryToken + StemArchive.ArchiveExtension),
            new StemCacheManifest
            {
                Key = "audio:|b=1|h=00", Codec = StemCodec.Wav,
                Stems = [new StemManifestEntry { Name = "guitar", FileName = "guitar.wav" }]
            },
            [("guitar.wav", staging)]);

        Assert.Null(Locator().TryLocate(mix, ArrangementType.Lead));
    }

    /// <summary>
    ///     <c>stems/</c> beside the audio is not keyed by content, so a stem there must be
    ///     as long as the mix — a stem left by a different, shorter recording is refused.
    /// </summary>
    [Fact]
    public void A_stems_folder_beside_the_audio_is_taken_only_when_its_stem_is_as_long_as_the_mix()
    {
        var mix = Mix(seconds: 2.0);
        var beside = Path.Combine(Path.GetDirectoryName(mix)!, "stems");
        WriteWav(Path.Combine(beside, "guitar.wav"), 1.0, 220);
        Assert.Null(Locator().TryLocate(mix, ArrangementType.Lead));

        WriteWav(Path.Combine(beside, "guitar.wav"), 2.0, 220);
        Assert.Equal(Path.Combine(beside, "guitar.wav"), Locator().TryLocate(mix, ArrangementType.Lead)?.Path);
    }

    [Fact]
    public void No_stem_anywhere_or_no_file_is_the_mix_and_writes_nothing()
    {
        var mix = Mix();
        var before = Snapshot(_dir);

        Assert.Null(Locator().TryLocate(mix, ArrangementType.Lead));
        Assert.Null(Locator().TryLocate(Path.Combine(_dir, "missing.wav"), ArrangementType.Lead));
        Assert.Null(Locator().TryLocate("", ArrangementType.Lead));
        Assert.Equal(before, Snapshot(_dir));
    }

    [Fact]
    public void The_header_length_reader_matches_the_written_length()
    {
        var path = WriteWav(Path.Combine(_dir, "len.wav"), 1.5, 110);
        Assert.Equal(1.5, ChartFromAudioStemLocator.TryReadWavSeconds(path)!.Value, 3);
        File.WriteAllText(Path.Combine(_dir, "not.wav"), "not a wav at all");
        Assert.Null(ChartFromAudioStemLocator.TryReadWavSeconds(Path.Combine(_dir, "not.wav")));
    }

    /// <summary>
    ///     End to end through the wizard's analyzer: the MIX here is silence, the bass stem
    ///     carries the notes — so a draft at all proves the stem was what it listened to,
    ///     and the draft names it.
    /// </summary>
    [Fact]
    public void The_analyzer_drafts_from_the_stem_and_says_so()
    {
        var song = Path.Combine(_dir, "song");
        Directory.CreateDirectory(song);
        var mix = Path.Combine(song, "mix.wav");
        WriteSilence(mix, 3.0);
        var raw = RawDir(mix);
        var stem = new float[3 * Rate];
        AnalysisTestSignals.AddPluck(stem, 0.5, 55.0);
        AnalysisTestSignals.AddPluck(stem, 1.5, 73.42);
        WriteSamples(Path.Combine(raw, "bass.wav"), stem);

        var bass = new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass, StringCount = 4 };
        for (var i = 0; i < 4; i++) bass.TuningPitches.Add(new TuningOffset(0));
        var beats = AnalysisTestSignals.ConstantBeats(120, 8);

        var fromStem = new ChartFromAudioAnalyzer(Locator()).Analyze(mix, beats, bass, 100);
        var fromMix = new ChartFromAudioAnalyzer(new ChartFromAudioStemLocator(Path.Combine(_dir, "empty")))
            .Analyze(mix, beats, bass, 100);

        Assert.Equal("bass", fromStem.StemName);
        Assert.NotEmpty(fromStem.Notes);
        Assert.Null(fromMix.StemName);
        Assert.Empty(fromMix.Notes);
    }

    private static void WriteSilence(string path, double seconds) => WriteSamples(path, new float[(int)(seconds * Rate)]);

    private static void WriteSamples(string path, float[] buffer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + buffer.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(buffer.Length * 2);
        foreach (var sample in buffer) writer.Write((short)(Math.Clamp(sample, -1f, 1f) * 30000));
    }
}
