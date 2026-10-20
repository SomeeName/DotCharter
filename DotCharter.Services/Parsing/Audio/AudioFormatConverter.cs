using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DotCharter.Services.Export.Audio;
using JuceSharp;

namespace DotCharter.Services.Parsing.Audio;

internal sealed class AudioFormatConverter
{
    private readonly IAppLogger _logger;
    private readonly Func<string, string, CancellationToken, bool> _nativeDecode;

    public AudioFormatConverter(IAppLogger logger)
        : this(logger, WriteNativeWave)
    {
    }

    /// <summary>Test seam: <paramref name="nativeDecode" /> (WEM path, staging WAV
    /// path, token) stands in for <see cref="WriteNativeWave" />.</summary>
    internal AudioFormatConverter(IAppLogger logger, Func<string, string, CancellationToken, bool> nativeDecode)
    {
        _logger = logger;
        _nativeDecode = nativeDecode;
    }

    /// <summary>
    ///     THE one place a library song's playable file is produced from its WEM.
    ///     The playable file is always the <c>.wav</c> beside the WEM: the engine
    ///     opens a cached WAV in ~50 ms but decodes an Ogg on every open (~0.3 s
    ///     more). (A library song's stem archive is keyed on the WEM, so
    ///     the decoder that wrote the WAV does not matter to it.) In order:
    ///     <list type="number">
    ///         <item>a fresh <c>.wav</c> already in the folder is used as-is;</item>
    /// <item>the NATIVE decoder (<see cref="JuceWemDecoder" />) decodes
    ///             the WEM straight to 16-bit PCM;</item>
    ///         <item>the fallbacks: vgmstream, then ffmpeg.</item>
    ///     </list>
    ///     An <c>.ogg</c> an older build left in the folder is deleted, never
    ///     read: the WAV is made from the WEM (there is no managed ww2ogg +
    ///     NVorbis path; every WEM decodes natively).
    ///     Every WAV is written to a staging file and moved into place, so a crash or
    ///     a cancel never leaves a truncated WAV for the freshness check to serve.
    ///     <paramref name="cancellationToken" /> (the selection prefetch) is honoured
    ///     between stages and inside the decode; Open passes none.
    /// </summary>
    public bool TryConvertWemToPlayable(string wemPath, out string playablePath,
        CancellationToken cancellationToken = default)
    {
        var wavPath = Path.ChangeExtension(wemPath, ".wav");
        playablePath = wavPath;
        DropLegacyOgg(Path.ChangeExtension(wemPath, ".ogg"));

        if (IsFreshUsableAudioFile(wavPath, wemPath) || TryDecodeNative(wemPath, wavPath, cancellationToken))
            return true;

        cancellationToken.ThrowIfCancellationRequested();

        if (TryWriteStaged(wavPath, staging => TryRunVgmstream(wemPath, staging)) ||
            TryWriteStaged(wavPath, staging => TryRunFfmpeg(wemPath, staging)))
        {
            return true;
        }

        DeleteQuietly(wavPath);
        playablePath = "";
        return false;
    }

    public static bool TryRunFfmpeg(string inputPath, string outputPath)
    {
        var executable = FindExecutable("ffmpeg.exe") ?? FindExecutable("ffmpeg");
        if (string.IsNullOrWhiteSpace(executable)) return false;

        return RunProcess(executable, ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, outputPath]) &&
               File.Exists(outputPath) && new FileInfo(outputPath).Length > 44;
    }

    /// <summary>The native decode through a staging file; any failure (no
    /// DLL, an older ABI, a WEM it cannot read) is logged and falls through to
    /// vgmstream and ffmpeg.</summary>
    private bool TryDecodeNative(string wemPath, string wavPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        try
        {
            if (TryWriteStaged(wavPath, staging => _nativeDecode(wemPath, staging, cancellationToken)))
            {
                _logger.Info(AppLogCategory.Import,
                    $"Decoded WEM to WAV natively. Source='{wemPath}', output='{wavPath}', ms={clock.ElapsedMilliseconds}.");
                return true;
            }

            _logger.Warning(AppLogCategory.Import,
                $"Native WEM decode produced nothing; trying vgmstream and ffmpeg. Source='{wemPath}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(AppLogCategory.Import,
                $"Native WEM decode failed; trying vgmstream and ffmpeg. Source='{wemPath}', type={ex.GetType().Name}, message='{ex.Message}'.");
        }

        return false;
    }

    /// <summary>
    ///     Decodes <paramref name="wemPath" /> with the native decoder into a 16-bit
    ///     WAV at <paramref name="wavPath" />. The native s16 read is the formula
    ///     every cached WAV was written with (clamp to [-1, 1], -1 maps to -32768,
    ///     everything else rounds half-to-even from <c>sample * 32767</c>), and the
    ///     header is <see cref="WriteWaveHeader" />'s, so the file has the same length
    ///     and layout as the WAVs older builds made with ww2ogg + NVorbis (samples
    ///     within 1 LSB, measured on every library song).
    /// </summary>
    internal static bool WriteNativeWave(string wemPath, string wavPath, CancellationToken cancellationToken)
    {
        using var decoder = JuceWemDecoder.OpenFile(wemPath);
        var info = decoder.Info;
        using var output = File.Create(wavPath);
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);

        WriteWaveHeader(writer, info.Channels, info.SampleRate, 0);

        var pcm = new short[info.Channels * 8192];
        long dataBytes = 0;
        int frames;
        while ((frames = decoder.Read(pcm.AsSpan())) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = pcm.AsSpan(0, frames * info.Channels);
            if (!BitConverter.IsLittleEndian)
                BinaryPrimitives.ReverseEndianness(block, block);
            writer.Write(MemoryMarshal.AsBytes(block));
            dataBytes += block.Length * sizeof(short);
        }

        writer.Flush();
        output.Position = 0;
        WriteWaveHeader(writer, info.Channels, info.SampleRate, dataBytes);
        return dataBytes > 0;
    }

    /// <summary>
    ///     Runs <paramref name="write" /> against a staging file beside
    ///     <paramref name="finalPath" /> and moves it into place only when the writer
    ///     succeeded and the result is usable. The staging file keeps the extension
    ///     (ffmpeg picks its output format from it) and never survives the call.
    /// </summary>
    internal static bool TryWriteStaged(string finalPath, Func<string, bool> write)
    {
        var staging = Path.Combine(Path.GetDirectoryName(finalPath) ?? "",
            $"{Path.GetFileNameWithoutExtension(finalPath)}.part-{Guid.NewGuid():N}{Path.GetExtension(finalPath)}");
        try
        {
            if (!write(staging) || !IsUsableAudioFile(staging)) return false;
            File.Move(staging, finalPath, overwrite: true);
            return true;
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    /// <summary>Older builds left a ww2ogg rebuild in every cache folder (~4 MB
    /// a song). Nothing reads it any more: it is deleted on sight.</summary>
    private void DropLegacyOgg(string oggPath)
    {
        if (!File.Exists(oggPath)) return;
        DeleteQuietly(oggPath);
        _logger.Debug(AppLogCategory.Import, $"Dropped a legacy WEM Ogg. Path='{oggPath}'.");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* ignored */ }
    }

    private static bool IsUsableAudioFile(string path) => File.Exists(path) && new FileInfo(path).Length > 44;

    private static bool IsFreshUsableAudioFile(string path, string sourcePath)
    {
        return IsUsableAudioFile(path) &&
               File.GetLastWriteTimeUtc(path) >= File.GetLastWriteTimeUtc(sourcePath);
    }

    private static void WriteWaveHeader(BinaryWriter writer, int channels, int sampleRate, long dataBytes)
    {
        if (channels <= 0) throw new InvalidDataException($"Invalid channel count in decoded WEM audio: {channels}.");
        if (sampleRate <= 0) throw new InvalidDataException($"Invalid sample rate in decoded WEM audio: {sampleRate}.");
        if (dataBytes > uint.MaxValue - 36)
            throw new InvalidDataException("Decoded WEM audio is too large for WAV output.");

        const short bitsPerSample = 16;
        var blockAlign = checked((short)(channels * bitsPerSample / 8));
        var byteRate = checked(sampleRate * blockAlign);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked((uint)(36 + dataBytes)));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(checked((uint)dataBytes));
    }

    private static bool TryRunVgmstream(string inputPath, string outputPath)
    {
        var executable = FindExecutable("vgmstream-cli.exe") ?? FindExecutable("vgmstream-cli");
        if (string.IsNullOrWhiteSpace(executable)) return false;

        return RunProcess(executable, ["-o", outputPath, inputPath]) && File.Exists(outputPath) &&
               new FileInfo(outputPath).Length > 44;
    }

    /// <summary>
    ///     Runs one of the external decoders (vgmstream, ffmpeg) to completion.
    ///     <para>
    ///         This must not redirect stdout AND stderr and then drain NEITHER: a
    ///         child that fills a pipe blocks forever writing into it while this
    ///         side blocks forever in <c>WaitForExit</c> — the ~4 KB Windows
    ///         anonymous pipe makes that an ordinary amount of output, and vgmstream
    ///         is chatty. The visible symptom is a two-minute freeze followed by
    ///         "conversion failed" on a machine where the tool works perfectly.
    ///         <c>MediaProcess.Run</c> is the one runner that drains both pipes
    ///         concurrently with a bounded wait and kills the whole tree on expiry;
    ///         it exists because the same defect was fixed on the ffmpeg/save path
    ///         first, and this caller was simply never swept.
    ///     </para>
    /// </summary>
    internal static bool RunProcess(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            return MediaProcess.Run(executable, arguments, ConversionTimeout).Succeeded;
        }
        catch
        {
            // A missing or broken executable is a normal outcome here — the
            // caller falls through to the next decoder.
            return false;
        }
    }

    /// <summary>Ceiling for one decoder run. Unchanged from the hand-rolled
    /// runner; what changed is that reaching it now means the tool really is
    /// stuck, not that nobody was reading its output.</summary>
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(2);

    private static string? FindExecutable(string fileName)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var localCandidates = new[]
        {
            Path.Combine(baseDirectory, fileName),
            Path.Combine(baseDirectory, "tools", fileName),
            // A split install keeps tools/ beside the launcher, above app/.
            Path.Combine(InstallLocation.InstallRoot, "tools", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", fileName)
        };

        foreach (var candidate in localCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignored */ }
        }

        return null;
    }
}
