using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Export.Audio;

/// <summary>Stable source slots, with content and decode-policy receipts instead of codec-specific file names.</summary>
internal static class ManagedAudioCache
{
    internal static string WemPath(string audioPath, string? projectDirectory = null) => PathFor(audioPath, ".wem", projectDirectory);

    internal static string PreviewPath(string audioPath, string? projectDirectory = null) => PathFor(audioPath, ".preview.wav", projectDirectory);

    private static string PathFor(string audioPath, string extension, string? projectDirectory)
    {
        var root = projectDirectory ?? ProjectFolderLayout.RootForArtifact(audioPath);
        var identity = Path.GetRelativePath(root, Path.GetFullPath(audioPath));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Path.Combine(ProjectFolderLayout.CacheDirectory(audioPath, root), Path.GetFileName(audioPath) + "." + key + extension);
    }

    internal static bool IsCurrent(string cache, string source) =>
        File.Exists(cache + ".source") && AtomicFile.ReadAllText(cache + ".source") == ProjectVolumes.Signature(source);

    /// <summary>Persist the signature captured before decoding; a source changed during encoding must miss next time.</summary>
    internal static void Record(string cache, string sourceSignature) =>
        AtomicFile.WriteAllText(cache + ".source", sourceSignature);
}
