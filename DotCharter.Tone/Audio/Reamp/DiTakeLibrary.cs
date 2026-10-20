using JuceSharp.AudioIo;

namespace DotCharter.Tone.Audio.Reamp;

/// <summary>One recorded or imported DI take on disk.</summary>
public sealed record DiTake(string FilePath, string Name, DateTime ModifiedUtc);

/// <summary>
///     The DI takes folder: <c>&lt;per-user folder&gt;/DI Takes</c>, i.e.
///     <c>%APPDATA%\DotCharter\DI Takes</c> in DotCharter and
///     <c>%APPDATA%\DotCharter RigLab\DI Takes</c> in RigLab (the folder follows
///     <see cref="WritablePaths.UseAppFolderName" />). Lists, names, renames,
///     deletes and imports takes, and decodes one to mono for a re-amp. Any file
///     the engine's decoder reads is a take (WAV, AIFF, FLAC, OGG, MP3).
///     Filesystem failures are reported as false / null, never thrown at the UI.
/// </summary>
public sealed class DiTakeLibrary
{
    /// <summary>The folder name under the per-user root.</summary>
    public const string FolderName = "DI Takes";

    /// <summary>Longest take a re-amp decodes (a decoded take lives in memory).</summary>
    public const double MaxLoadSeconds = 20 * 60;

    private static readonly string[] Extensions = [".wav", ".aif", ".aiff", ".flac", ".ogg", ".mp3"];

    /// <param name="rootDirectory">Where the takes live; null = <see cref="FolderName" />
    /// under <see cref="WritablePaths.ConfigRoot" />, resolved on first use.</param>
    public DiTakeLibrary(string? rootDirectory = null) => _root = rootDirectory;

    private string? _root;

    /// <summary>The takes folder (not created until something is written).</summary>
    public string RootDirectory => _root ??= Path.Combine(WritablePaths.ConfigRoot, FolderName);

    /// <summary>File extensions a take may have, for an import picker.</summary>
    public static IReadOnlyList<string> SupportedExtensions => Extensions;

    /// <summary>Every take, newest first. Empty when the folder does not exist.</summary>
    public IReadOnlyList<DiTake> List()
    {
        try
        {
            if (!Directory.Exists(RootDirectory)) return [];
            return Directory.EnumerateFiles(RootDirectory)
                .Where(IsTakeFile)
                .Select(p => new DiTake(p, Path.GetFileNameWithoutExtension(p), File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(t => t.ModifiedUtc)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>True for a file the library lists (by extension; an unfinished
    /// recording's <c>.recording</c> temp is not a take).</summary>
    public static bool IsTakeFile(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>A fresh path for a new recording, named by the local time
    /// (<c>DI yyyy-MM-dd HH-mm-ss.wav</c>), unique in the folder.</summary>
    public string NewRecordingPath(DateTime localNow)
        => UniquePath($"DI {localNow:yyyy-MM-dd HH-mm-ss}", ".wav");

    /// <summary>Renames a take (the extension is kept). Returns the renamed take,
    /// or null when the name is empty/invalid or the move failed.</summary>
    public DiTake? Rename(DiTake take, string newName)
    {
        ArgumentNullException.ThrowIfNull(take);
        var clean = SafeFileName.Make(newName?.Trim());
        if (string.IsNullOrWhiteSpace(clean)) return null;
        var ext = Path.GetExtension(take.FilePath);
        if (string.Equals(clean, take.Name, StringComparison.Ordinal)) return take;
        var target = UniquePath(clean, ext, except: take.FilePath);
        try
        {
            File.Move(take.FilePath, target);
            return new DiTake(target, Path.GetFileNameWithoutExtension(target), File.GetLastWriteTimeUtc(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Deletes a take. False when it could not be deleted.</summary>
    public bool Delete(DiTake take)
    {
        ArgumentNullException.ThrowIfNull(take);
        try
        {
            File.Delete(take.FilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Copies an audio file into the folder as a take (a unique name
    /// from its own). Null when the file is not a supported type or the copy
    /// failed.</summary>
    public DiTake? Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || !IsTakeFile(sourcePath)) return null;
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var target = UniquePath(SafeFileName.Make(Path.GetFileNameWithoutExtension(sourcePath)),
                Path.GetExtension(sourcePath).ToLowerInvariant());
            File.Copy(sourcePath, target);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            return new DiTake(target, Path.GetFileNameWithoutExtension(target), File.GetLastWriteTimeUtc(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    ///     Decodes a take to MONO (channels averaged) at the file's own rate, for
    ///     <see cref="IReampEngine.LoadTake" />. Throws <see cref="InvalidDataException" />
    ///     for a file the decoder cannot read or one longer than
    ///     <see cref="MaxLoadSeconds" />.
    /// </summary>
    public static (float[] Mono, int SampleRate) LoadMono(string path)
    {
        var (rate, channels, frames) = JuceAudioDecoder.Probe(path);
        if (rate <= 0 || channels <= 0 || frames <= 0)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' has no audio.");
        if (frames > MaxLoadSeconds * rate)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is longer than {MaxLoadSeconds / 60:0} minutes.");
        var buffer = JuceAudioDecoder.Decode(path);
        return (MixToMono(buffer.Interleaved, buffer.Channels), buffer.SampleRate);
    }

    /// <summary>Averages interleaved channels into one.</summary>
    public static float[] MixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1) return interleaved.ToArray();
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        var inv = 1f / channels;
        for (var f = 0; f < frames; f++)
        {
            var s = 0f;
            for (var c = 0; c < channels; c++) s += interleaved[f * channels + c];
            mono[f] = s * inv;
        }
        return mono;
    }

    private string UniquePath(string baseName, string ext, string? except = null)
    {
        var candidate = Path.Combine(RootDirectory, baseName + ext);
        for (var i = 2; File.Exists(candidate) && !string.Equals(candidate, except, StringComparison.OrdinalIgnoreCase); i++)
            candidate = Path.Combine(RootDirectory, $"{baseName} ({i}){ext}");
        return candidate;
    }
}
