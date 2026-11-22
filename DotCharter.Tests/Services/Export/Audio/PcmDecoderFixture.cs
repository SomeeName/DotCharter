using System.Collections.Concurrent;
using DotCharter.Services.Export.Audio;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>Decoder selection is explicit; optional tool integration never searches parent checkouts or PATH.</summary>
internal static class PcmDecoderFixture
{
    private static readonly ConcurrentDictionary<string, string> EncoderLists = new(StringComparer.OrdinalIgnoreCase);

    public static string Ffmpeg()
    {
        var path = Environment.GetEnvironmentVariable("DOTCHARTER_TEST_FFMPEG")
            ?? Path.Combine(TestPaths.RepoRoot(), "tools", "redist", "win-x64", "ffmpeg.exe");
        Assert.SkipWhen(!File.Exists(path), "Set DOTCHARTER_TEST_FFMPEG to an installed tool for the explicit ffmpeg integration cases.");
        return path;
    }

    /// <summary>The tool for a case that ENCODES its fixture with <paramref name="encoder"/>; skips when the tool lacks that encoder.</summary>
    public static string Ffmpeg(string encoder)
    {
        var path = Ffmpeg();
        RequireEncoder(path, encoder);
        return path;
    }

    /// <summary>Skips the case when <paramref name="ffmpeg"/> cannot encode with <paramref name="encoder"/> (a decode-only redist build).</summary>
    public static void RequireEncoder(string ffmpeg, string encoder)
        => Assert.SkipUnless(CanEncode(EncoderLists.GetOrAdd(ffmpeg, ListEncoders), encoder),
            $"{Path.GetFileName(ffmpeg)} has no '{encoder}' encoder; set DOTCHARTER_TEST_FFMPEG to a full ffmpeg build.");

    /// <summary>True when <paramref name="encodersText"/> (the output of <c>-encoders</c>) lists an audio encoder named exactly <paramref name="encoder"/>.</summary>
    public static bool CanEncode(string encodersText, string encoder)
    {
        foreach (var line in encodersText.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'A' && string.Equals(parts[1], encoder, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    public static PcmAudio Load(string path, bool native, bool ffmpeg)
        => PcmAudioLoader.Load(path, [], native ? JuceAudioDecoder.Decode : _ => null, ffmpeg ? Ffmpeg() : null);

    private static string ListEncoders(string ffmpeg)
    {
        var info = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            CreateNoWindow = true, UseShellExecute = false,
            ArgumentList = { "-hide_banner", "-encoders" }
        };
        return BoundedProcess.Run(info, TimeSpan.FromMinutes(2), "ffmpeg (encoder list)").StandardOutput;
    }
}
