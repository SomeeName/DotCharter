using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>
///     The mirror's file operations on the real disk. Identities come from
///     <c>GetFileInformationByHandle</c> on a handle opened with NO read or write access; hardlinks
///     from <c>CreateHardLinkW</c>; junctions are mount-point reparse points (<c>mklink /J</c>
///     semantics, no administrator rights), removed with <c>RemoveDirectoryW</c> alone. Real files
///     are only created new (<see cref="FileMode.CreateNew" />), never opened for writing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32GameMirrorFileSystem : IGameMirrorFileSystem
{
    private const int ErrorNotSameDevice = 17;
    private const uint FileShareAll = 0x00000007;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint ReparseTagMountPoint = 0xA0000003;

    public static Win32GameMirrorFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path) || IsJunction(path);

    public string? JunctionTarget(string path)
    {
        if (!IsJunction(path)) return null;
        return new DirectoryInfo(path).LinkTarget is { } target ? StripNtPrefix(target) : null;
    }

    public GameFileId? FileId(string path)
    {
        if (!File.Exists(path)) return null;
        // Access 0: attributes and identity only, the file's data is never opened.
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return new GameFileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    public IReadOnlyList<string> FileNames(string directory, string pattern) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>().ToList()
            : [];

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CreateHardLink(string link, string existing)
    {
        if (CreateHardLinkW(link, existing, IntPtr.Zero)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotSameDevice) throw new GameMirrorCrossVolumeException($"{link} and {existing} are on different volumes");
        throw new IOException($"CreateHardLink {link} -> {existing}: {new Win32Exception(error).Message}", error);
    }

    public void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        try
        {
            // The new, empty folder of the mirror itself is the only thing opened for writing here.
            using var handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException($"open {link}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            var buffer = MountPointBuffer(Path.GetFullPath(target));
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new IOException($"junction {link} -> {target}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }
        }
        catch
        {
            RemoveDirectoryW(link);
            throw;
        }
    }

    public void RemoveJunction(string path)
    {
        if (!IsJunction(path)) throw new IOException($"{path} is not a junction; it is not removed");
        if (!RemoveDirectoryW(path)) throw new IOException($"RemoveDirectory {path}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void CopyToNewFile(string source, string destination) => File.Copy(source, destination, overwrite: false);

    public void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    /// <summary>
    ///     The <c>REPARSE_DATA_BUFFER</c> of a mount point: tag, data length, reserved, then the
    ///     substitute name (<c>\??\C:\…</c>) and the print name, each NUL-terminated UTF-16.
    /// </summary>
    internal static byte[] MountPointBuffer(string fullTarget)
    {
        var target = Path.TrimEndingDirectorySeparator(fullTarget);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var pathBytes = substitute.Length + 2 + print.Length + 2;
        var dataLength = 8 + pathBytes;
        var buffer = new byte[8 + dataLength];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, ReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)print.Length);
        substitute.CopyTo(span[16..]);
        print.CopyTo(span[(16 + substitute.Length + 2)..]);
        return buffer;
    }

    private static bool IsJunction(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
    }

    private static string StripNtPrefix(string target) => target.StartsWith(@"\??\", StringComparison.Ordinal) ? target[4..] : target;

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>: the FILETIMEs are two DWORDs each, so the layout packs at 4 (52 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveDirectoryW(string pathName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint controlCode, [In] byte[] inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
