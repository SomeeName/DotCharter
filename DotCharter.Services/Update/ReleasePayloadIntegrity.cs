using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotCharter.Services.Update;

/// <summary>What is wrong with one file of a staged release payload.</summary>
public enum PayloadFileFault
{
    /// <summary>Nothing — the file is what it claims to be.</summary>
    None,

    /// <summary>A <c>.dll</c> / <c>.exe</c> with no bytes at all.</summary>
    Empty,

    /// <summary>A non-empty file whose every byte is zero: allocated, never written.</summary>
    AllZeros,

    /// <summary>A <c>.dll</c> / <c>.exe</c> that does not start with the <c>MZ</c> DOS header.</summary>
    NotPortableExecutable,

    /// <summary>Starts with <c>MZ</c>, but the PE headers behind it do not parse.</summary>
    BadPeHeaders,

    /// <summary>A managed assembly whose CLI metadata does not read.</summary>
    BadMetadata
}

/// <summary>One file a payload must not ship, with the reason in words.</summary>
public sealed record PayloadFileProblem(string RelativePath, PayloadFileFault Fault, string Detail);

/// <summary>What <see cref="ReleasePayloadIntegrity.Scan" /> looked at, and what it refused.</summary>
public sealed record PayloadIntegrityReport(int FilesChecked, int ImagesChecked, IReadOnlyList<PayloadFileProblem> Problems)
{
    public bool IsClean => Problems.Count == 0;
}

/// <summary>
///     Refuses a staged release payload whose binaries are not binaries.
///     <para>
/// Incident: a restart interrupted a release run while it
///         wrote <c>bin/Release</c> and <c>obj/Release</c>. NTFS kept the files'
///         allocation but not their data, so 260 of them — the 5.8 MB
///         <c>DotCharter.dll</c> among them — were left the right size and entirely
///         zero. The next run's incremental build reused them, the zip validated
///         (the updater rules check LAYOUT, not content), and the packed
///         <c>DotCharter.exe</c> died at launch with <c>BadImageFormatException</c>.
///         Only a manual smoke run caught it.
///     </para>
///     <para>
///         So every <c>.dll</c> and <c>.exe</c> must start with <c>MZ</c>, carry PE
///         headers that parse and — for a managed assembly — metadata that reads;
///         and no file of any kind may be non-empty and all zeros. A zero-length
///         data file is allowed (a marker file is legitimately empty); a zero-length
///         image is not. The check reads only until it has an answer: the zero scan
///         stops at the first non-zero byte, so a healthy payload costs one buffer
///         per file plus the PE headers.
///     </para>
/// </summary>
public static class ReleasePayloadIntegrity
{
    private const int ScanBufferBytes = 64 * 1024;

    /// <summary>Whether a file name is one the loader maps as a PE image.</summary>
    public static bool IsExecutableImage(string fileName) =>
        fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Inspects one file's bytes. <paramref name="stream" /> must be readable and
    ///     seekable; its position is not restored.
    /// </summary>
    public static PayloadFileFault Inspect(Stream stream, bool executableImage, out string detail)
    {
        detail = string.Empty;
        if (stream.Length == 0)
        {
            if (!executableImage) return PayloadFileFault.None;
            detail = "0 bytes";
            return PayloadFileFault.Empty;
        }

        if (!executableImage)
        {
            if (!IsAllZeros(stream)) return PayloadFileFault.None;
            detail = $"all {stream.Length:N0} bytes are zero";
            return PayloadFileFault.AllZeros;
        }

        stream.Position = 0;
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        if (first != 'M' || second != 'Z')
        {
            if (IsAllZeros(stream))
            {
                detail = $"all {stream.Length:N0} bytes are zero";
                return PayloadFileFault.AllZeros;
            }

            detail = $"starts with 0x{Math.Max(first, 0):X2} 0x{Math.Max(second, 0):X2}, not 'MZ'";
            return PayloadFileFault.NotPortableExecutable;
        }

        stream.Position = 0;
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        try
        {
            _ = pe.PEHeaders;
        }
        catch (BadImageFormatException ex)
        {
            detail = ex.Message;
            return PayloadFileFault.BadPeHeaders;
        }

        if (!pe.HasMetadata) return PayloadFileFault.None;
        try
        {
            var metadata = pe.GetMetadataReader();
            _ = metadata.GetModuleDefinition().Name;
            _ = metadata.TypeDefinitions.Count;
        }
        catch (BadImageFormatException ex)
        {
            detail = ex.Message;
            return PayloadFileFault.BadMetadata;
        }

        return PayloadFileFault.None;
    }

    /// <summary>Inspects every file under <paramref name="payloadDir" />, recursively.</summary>
    public static PayloadIntegrityReport Scan(string payloadDir)
    {
        if (!Directory.Exists(payloadDir))
            throw new DirectoryNotFoundException($"No such payload directory: {payloadDir}");

        var problems = new List<PayloadFileProblem>();
        var files = 0;
        var images = 0;
        foreach (var path in Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            files++;
            var image = IsExecutableImage(path);
            if (image) images++;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            var fault = Inspect(stream, image, out var detail);
            if (fault == PayloadFileFault.None) continue;

            var relative = Path.GetRelativePath(payloadDir, path).Replace('\\', '/');
            problems.Add(new PayloadFileProblem(relative, fault, detail));
        }

        return new PayloadIntegrityReport(files, images, problems);
    }

    private static bool IsAllZeros(Stream stream)
    {
        stream.Position = 0;
        var buffer = new byte[ScanBufferBytes];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0) return false;
        }

        return true;
    }
}
