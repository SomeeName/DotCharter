using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>
///     The audio a chart is exported with must be the audio it was charted
///     against. The editor plays a project's audio through the native engine's
///     decoder (<c>JuceAudioDecoder</c>, JUCE's basic format set), so
///     <see cref="PcmAudioLoader" /> — which feeds the Rocksmith <c>.wem</c>, the
///     audio lint, lyric auto-sync and tone generation — must place every sound
///     at the same time. It did not for MP3: ffmpeg decoded first and drops the
///     encoder's leading padding, the engine's Windows Media reader keeps it, and
///     every MP3 project's export ran 1,729 samples (36 ms at 48 kHz) ahead of its
///     chart. FLAC, OGG and WAV were already identical.
///     <para>
///         The MP3 fixtures are encoded on the fly with the repo's cached ffmpeg
///         (tools/redist/win-x64); skipped where that or the native DLL is missing.
///     </para>
/// </summary>
public sealed class PcmAudioLoaderEditorParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void AnMp3_IsExportedWithItsSoundsWhereTheEditorPlaysThem(int sourceRate)
    {
        var mp3 = EncodeMp3(sourceRate);

        // What the editor plays: the engine's own decoder, at the file's own rate.
        var editor = JuceSharp.AudioIo.JuceAudioDecoder.Decode(mp3);
        var editorOnsetSeconds = FirstLoud(editor.Interleaved, editor.Channels, 0.25f) / (double)editor.SampleRate;

        // What the export encodes: 48 kHz stereo 16-bit.
        var exported = PcmAudioLoader.Load(mp3, [], RedistFfmpeg());
        Assert.Equal(PcmAudioLoader.RocksmithSampleRate, exported.SampleRate);
        var exportOnsetSeconds = FirstLoud(
            exported.Samples.Select(s => s / 32768f).ToArray(), exported.Channels, 0.25f) / (double)exported.SampleRate;

        var driftMs = (exportOnsetSeconds - editorOnsetSeconds) * 1000.0;
        Assert.True(Math.Abs(driftMs) < 0.5,
            $"the exported audio is {driftMs:+0.00;-0.00} ms off what the editor plays for a {sourceRate} Hz MP3 "
            + $"(editor onset {editorOnsetSeconds * 1000:0.00} ms, export onset {exportOnsetSeconds * 1000:0.00} ms) "
            + "— every MP3 chart would export out of sync");
    }

    /// <summary>A lossless file was never affected; pinned so the MP3 fix cannot
    /// move it.</summary>
    [Fact]
    public void AFlac_IsExportedSampleForSampleAsTheEditorPlaysIt()
    {
        var flac = Encode(48000, "flac", []);
        var editor = JuceSharp.AudioIo.JuceAudioDecoder.Decode(flac);
        var exported = PcmAudioLoader.Load(flac, [], RedistFfmpeg());

        Assert.Equal(FirstLoud(editor.Interleaved, editor.Channels, 0.25f),
            FirstLoud(exported.Samples.Select(s => s / 32768f).ToArray(), exported.Channels, 0.25f));
    }

    // ---- fixtures ------------------------------------------------------------

    /// <summary>Half a second of silence, then a 30 ms 1 kHz burst, then silence —
    /// a sound with one unambiguous start.</summary>
    private string WriteBurstWav(int rate)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"burst{rate}.wav");
        var frames = rate * 2;
        var samples = new short[frames * 2];
        var start = rate / 2;
        var length = rate * 30 / 1000;
        for (var i = 0; i < length; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 20000);
            samples[(start + i) * 2] = v;
            samples[(start + i) * 2 + 1] = v;
        }

        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 2, rate));
        return path;
    }

    private string EncodeMp3(int rate) => Encode(rate, "mp3", ["-c:a", "libmp3lame", "-b:a", "192k"]);

    private string Encode(int rate, string extension, string[] codecArgs)
    {
        var decoderProbe = PcmAudioLoader.TryLoadWithNativeDecoder(WriteBurstWav(rate), []);
        Assert.SkipWhen(decoderProbe is null, "juce_sharp.dll not available in this run.");
        var ffmpeg = RedistFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        var output = Path.Combine(_dir, $"burst{rate}.{extension}");
        var info = new System.Diagnostics.ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", "-i", Path.Combine(_dir, $"burst{rate}.wav")])
            info.ArgumentList.Add(a);
        foreach (var a in codecArgs) info.ArgumentList.Add(a);
        info.ArgumentList.Add(output);
        using var encode = System.Diagnostics.Process.Start(info)!;
        encode.WaitForExit(60_000);
        Assert.SkipWhen(!File.Exists(output) || new FileInfo(output).Length == 0, $"{extension} encode failed on this machine.");
        return output;
    }

    private static int FirstLoud(float[] interleaved, int channels, float threshold)
    {
        for (var f = 0; f < interleaved.Length / channels; f++)
            if (Math.Abs(interleaved[f * channels]) > threshold) return f;
        throw new InvalidOperationException("the fixture's burst was not found in the decoded audio");
    }

    private static string? RedistFfmpeg()
    {
        var repo = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && repo != null; i++, repo = Path.GetDirectoryName(repo))
        {
            var candidate = Path.Combine(repo, "tools", "redist", "win-x64", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
