using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotCharter.Tests.Services.Update;

/// <summary>Controls NTFS change time independently of last-write time for robocopy fixtures.</summary>
internal static class UpdaterFileMetadata
{
    private const int FileBasicInfo = 0;
    // FILE_BASIC_INFO: four 8-byte timestamps, 4-byte attributes, 4-byte padding.
    private const int BasicInfoSize = 40;
    private const int ChangeTimeOffset = 24;

    public static long GetChangeTime(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return BinaryPrimitives.ReadInt64LittleEndian(ReadBasicInfo(handle).AsSpan(ChangeTimeOffset));
    }

    public static void SetChangeTime(string path, long changeTime)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var info = ReadBasicInfo(handle);
        BinaryPrimitives.WriteInt64LittleEndian(info.AsSpan(ChangeTimeOffset), changeTime);
        if (!SetFileInformationByHandle(handle, FileBasicInfo, info, BasicInfoSize))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static byte[] ReadBasicInfo(SafeFileHandle handle)
    {
        var info = new byte[BasicInfoSize];
        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, info, BasicInfoSize))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass,
        [Out] byte[] fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        [In] byte[] fileInformation, uint bufferSize);
}
