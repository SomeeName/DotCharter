using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
///     Rename-over-an-open-file, the way <see cref="AtomicFile" /> needs it.
///     <para>
///         <c>File.Move(overwrite: true)</c> is <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c>,
///         and on Windows that is DENIED ("Access to the path is denied") for as long
///         as ANY handle is open on the destination — including one opened with
///         <c>FileShare.Delete</c>, which is what <see cref="AtomicFile.ReadAllText" />
///         was introduced to do. So a reader we own could still block our own
///         writer, and the writer's bounded retry only ever won in the gaps between
///         the reader's open and close. Under a CPU-starved parallel test gate that
///         gap failed to come round for the whole 430 ms budget, twice, and the
///         save threw <c>UnauthorizedAccessException</c> out of
///         <see cref="AtomicFile.ReplaceWithRetry" />.
///     </para>
///     <para>
///         <c>SetFileInformationByHandle(FileRenameInfoEx)</c> with
///         <c>FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS</c>
///         (Windows 10 1607+, NTFS) is the rename that replaces a destination that
///         is open by share-delete handles: those handles keep reading the OLD
///         file, whole, until they close, and the name maps to the new file at
///         once. Measured on this machine before it was written: MoveFileEx failed
///         against a <c>ReadWrite | Delete</c> reader; this succeeded, the old handle
///         read the old bytes, the name read the new ones. A holder that did NOT
///         share delete (an antivirus scan, a plain <c>File.ReadAllText</c>) still
///         gets a sharing violation — that is the case the retry loop is FOR.
///     </para>
///     <para>
///         <see cref="TryReplace" /> returns false on ANY failure — not Windows, a
///         volume without the flag (FAT/exFAT/SMB), an older kernel, a
///         non-share-delete holder — and the caller falls back to
///         <c>File.Move</c>, whose exceptions the retry loop already classifies.
///         It never throws.
///     </para>
/// </summary>
public static partial class WindowsPosixRename
{
    private const uint Delete = 0x00010000;
    private const uint FileShareAll = 0x00000007; // READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const int FileRenameInfoEx = 22;
    private const uint FileRenameFlagReplaceIfExists = 0x1;
    private const uint FileRenameFlagPosixSemantics = 0x2;

    /// <summary>
    ///     Renames <paramref name="sourcePath" /> over <paramref name="destinationPath" />
    ///     with POSIX semantics. True when the destination now names the source's
    ///     bytes and the source name is gone; false when this path is unavailable
    ///     and the caller must fall back. Never throws.
    /// </summary>
    public static bool TryReplace(string sourcePath, string destinationPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return TryReplaceWindows(sourcePath, destinationPath);
        }
        catch
        {
            // A marshalling surprise must never be a WORSE outcome than the
            // classic rename the caller is about to try anyway.
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReplaceWindows(string sourcePath, string destinationPath)
    {
        // The rename information takes an NT path. \??\C:\... is the NT alias for a
        // drive-rooted Win32 path; long-path and UNC forms are left to File.Move.
        var full = Path.GetFullPath(destinationPath);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            return false;
        var ntPath = @"\??\" + full;

        // DELETE is the access a rename needs on the SOURCE handle; FileStream
        // never asks for it, hence the raw CreateFile.
        using var handle = CreateFileW(sourcePath, Delete, FileShareAll, IntPtr.Zero,
            OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        // FILE_RENAME_INFO: DWORD Flags; HANDLE RootDirectory; DWORD FileNameLength;
        // WCHAR FileName[]. Offsets on x64: 0 / 8 / 16 / 20.
        var nameBytes = ntPath.Length * 2;
        var size = FileNameOffset + nameBytes + 2; // + terminating WCHAR
        var info = new byte[size];                 // zeroed: RootDirectory = NULL, terminator = 0
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0),
            FileRenameFlagReplaceIfExists | FileRenameFlagPosixSemantics);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(FileNameLengthOffset), nameBytes);
        Encoding.Unicode.GetBytes(ntPath, info.AsSpan(FileNameOffset));

        return SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)size);
    }

    // FILE_RENAME_INFO on x64: Flags @0 (the BOOLEAN/DWORD union), RootDirectory
    // @8 (HANDLE, pointer-aligned), FileNameLength @16, FileName @20. x86: 0/4/8/12.
    private static readonly int FileNameLengthOffset = IntPtr.Size * 2;
    private static readonly int FileNameOffset = IntPtr.Size * 2 + sizeof(int);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        [In] byte[] fileInformation, uint bufferSize);
}
