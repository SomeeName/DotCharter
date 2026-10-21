using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A file-system seam whose every file REPORTS <see cref="Length" /> bytes and
///     which fails the test if anything is actually read - so "refused without
///     being read" is asserted on the seam, with no oversized file on disk.
/// </summary>
public sealed class OversizedFileWriter(long length) : IExportTargetWriter
{
    public long Length { get; } = length;

    public int Reads { get; private set; }

    public long FileLength(string path) => Length;

    public byte[] ReadAllBytes(string path)
    {
        Reads++;
        throw new InvalidOperationException("the file was read although it is over the size cap");
    }

    public Task<string> ReadAllTextAsync(string path)
    {
        Reads++;
        throw new InvalidOperationException("the file was read although it is over the size cap");
    }

    public bool FileExists(string path) => true;

    public bool DirectoryExists(string path) => true;

    public void CopyFile(string source, string destination) => throw new NotSupportedException();

    public void WriteAllBytes(string path, byte[] bytes) => throw new NotSupportedException();

    public Task WriteAllTextAsync(string path, string text) => throw new NotSupportedException();

    public Stream Create(string path) => throw new NotSupportedException();

    public void DeleteIfExists(string path) => throw new NotSupportedException();

    public string[] FindFiles(string folder, string pattern) => throw new NotSupportedException();

    public string DocumentsFolder => "";
}
