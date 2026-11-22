using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Services.Export;

/// <summary>Publishes generated-media links or isolated source copies. Never truncates a linked source.</summary>
internal static class ExportAssetLink
{
    internal static void Publish(string source, string target, Action<string, string>? link = null,
        Action<string, string>? copy = null, bool allowHardLink = true) =>
        CompanionOwnership.WriteIfUnmodified(target, destination =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var staging = destination + $".{Guid.NewGuid():N}.linking";
            try
            {
                if (allowHardLink)
                {
                    try
                    {
                        (link ?? CreateLink)(staging, source);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        (copy ?? File.Copy)(source, staging);
                    }
                }
                else
                {
                    (copy ?? File.Copy)(source, staging);
                }
                // A link contains no new bytes to flush. Opening it for write would
                // also open the source inode for write, and can break links on overlay filesystems.
                AtomicFile.ReplaceWithRetry(staging, destination);
            }
            finally
            {
                if (File.Exists(staging)) RecycleBin.Remove(staging);
            }
        }, out _);

    private static void CreateLink(string link, string source)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Win32GameMirrorFileSystem.Instance.CreateHardLink(link, source);
    }
}
