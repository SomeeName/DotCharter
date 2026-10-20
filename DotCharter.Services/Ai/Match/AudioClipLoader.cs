using JuceSharp.AudioIo;

namespace DotCharter.Services.Ai.Match;

/// <summary>
///     A file on disk → <see cref="MonoAudio" />.
///     <para>
///         <b>It exists because three lanes each needed it and the contract does not
///         carry it.</b> `MatchContracts` deliberately exchanges audio as
///         <see cref="MonoAudio" /> and never as a path, which is the right rule for
///         the lanes' own seams — but somebody still has to turn the user's WAV into
///         one, and every caller that turns a file into a clip — the stem isolator and
///         the tone designer's bundled reference DI included — goes through this, so
///         there is one RIFF parser and one set of format bugs, and every caller reads
///         the float32 WAVs the stem cache writes.
///     </para>
/// </summary>
public interface IAudioClipLoader
{
    /// <summary>
    ///     Decodes <paramref name="path" /> and folds it to mono at the file's own
    ///     rate. Optionally cuts a span.
    /// </summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    /// <exception cref="InvalidDataException">Not decodable by either backend.</exception>
    MonoAudio Load(string path, TimeSpan? from = null, TimeSpan? to = null);
}

/// <summary>
///     The one loader: managed <see cref="WavIo" /> for WAV (every PCM and float
///     flavour, including the float32 the stem cache writes), and the native
///     <see cref="JuceAudioDecoder" /> for everything else.
///     <para>
///         WAV goes through the MANAGED reader first on purpose — it needs no native
///         DLL, so a clip loads in a test host, in a worktree, and on a machine whose
///         <c>juce_sharp.dll</c> failed to load, which is exactly where a decode
///         failure is hardest to diagnose.
///     </para>
/// </summary>
public sealed class AudioClipLoader : IAudioClipLoader
{
    /// <inheritdoc />
    public MonoAudio Load(string path, TimeSpan? from = null, TimeSpan? to = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);

        var buffer = Decode(path);
        var mono = buffer.ToMono();
        var rate = buffer.SampleRate;

        var start = from is null ? 0 : Math.Clamp((int)Math.Round(from.Value.TotalSeconds * rate), 0, mono.Length);
        var end = to is null ? mono.Length : Math.Clamp((int)Math.Round(to.Value.TotalSeconds * rate), start, mono.Length);
        if (start == 0 && end == mono.Length) return new MonoAudio(mono, rate);

        var slice = new float[end - start];
        Array.Copy(mono, start, slice, 0, slice.Length);
        return new MonoAudio(slice, rate);
    }

    internal static AudioBuffer Decode(string path)
    {
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return WavIo.Read(path);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // A .wav extension on something that is not RIFF: fall through rather
                // than fail, because that is what a renamed file looks like.
            }
        }

        try
        {
            return JuceAudioDecoder.Decode(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                      or EntryPointNotFoundException)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is not a WAV and the native decoder is not loadable " +
                "(juce_sharp.dll): " + ex.Message, ex);
        }
    }
}

/// <summary>The forgiving half of <see cref="IAudioClipLoader" />, for a caller that
/// turns "no clip" into a message rather than an exception.</summary>
public static class AudioClipLoading
{
    /// <summary>
    ///     <paramref name="path" /> decoded to mono, or null when it is blank, missing,
    ///     undecodable or empty. Touches the disk: call it from a worker.
    /// </summary>
    public static MonoAudio? TryLoad(this IAudioClipLoader loader, string? path)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var clip = loader.Load(path);
            return clip.Samples.Length > 0 ? clip : null;
        }
        catch (Exception)
        {
            // A missing, truncated or foreign file is "no clip", never an exception
            // out of a command.
            return null;
        }
    }
}
