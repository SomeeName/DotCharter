using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Conservative evidence for unattended recycling; any unknown setting or filesystem result refuses it.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRecycleBinProbe
{
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string SyncRootKey = ExplorerKey + @"\SyncRootManager";
    private const long MetadataHeadroom = 64 * 1024;
    private const int MaximumEntries = 8192;
    private const int EndOfStreams = 38;
    // ReparsePoint, Offline, RecallOnOpen, Unpinned, RecallOnDataAccess.
    private const FileAttributes UncertainAttributes = (FileAttributes)0x00541400;

    internal static RecycleBinStatus Read(string path)
    {
        // Reject UNC, device, relative and mapped network paths before touching their contents.
        var root = Path.GetPathRoot(path);
        if (root == null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            return Unavailable(false, false);
        if (path.Length >= 260 || path.Length <= root.Length || path[3..].IndexOfAny([':', '*', '?']) >= 0 ||
            path[3..].Split('\\').Any(component => component.EndsWith('.') || component.EndsWith(' ')))
        {
            return Unavailable(false, false);
        }
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.DriveFormat != "NTFS")
            return Unavailable(false, false);
        if (!RecyclePathSafety.TryCanonicalizeLocal(path, CanonicalLongPath, out var canonical)) return Unavailable(true, false);
        path = canonical;
        if (!HasPlainAncestry(path) || IsKnownCloudPath(path)) return Unavailable(true, false);

        return ReadVolumeStatus(path, root, drive);
    }

    private static RecycleBinStatus ReadVolumeStatus(string path, string root, DriveInfo drive)
    {
        var volume = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, volume, (uint)volume.Capacity)) throw new Win32Exception();
        var name = volume.ToString();
        if (!name.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) ||
            !Guid.TryParse(name[10..].TrimEnd('\\'), out var id))
        {
            return Unavailable(true, true);
        }

        using var settings = Registry.CurrentUser.OpenSubKey(ExplorerKey + @"\BitBucket\Volume\" + id.ToString("B"));
        // Explorer's per-volume schema is an implementation detail, not a supported public API.
        // Require both explicit DWORD values; missing or changed schema must never infer enabled/default quota.
        var configured = RecycleBinSettings.FromValues(settings?.GetValue("NukeOnDelete"), settings?.GetValue("MaxCapacity"));
        if (!PoliciesAllowRecycling() || configured.IsEnabled != true || configured.QuotaBytes is not > 0 ||
            configured.QuotaBytes > drive.TotalSize)
        {
            return new RecycleBinStatus(true, true, false, configured.QuotaBytes, null, null);
        }

        var query = new QueryInfo { Size = (uint)Marshal.SizeOf<QueryInfo>() };
        if (SHQueryRecycleBin(root, ref query) != 0 || query.Bytes < 0 || query.Items < 0)
            return new RecycleBinStatus(true, true, true, configured.QuotaBytes, null, null);
        var candidate = CandidateBytes(path);
        if (candidate > drive.AvailableFreeSpace)
            return new RecycleBinStatus(true, true, true, configured.QuotaBytes, query.Bytes, null);
        return new RecycleBinStatus(true, true, true, configured.QuotaBytes, query.Bytes, candidate);
    }

    private static RecycleBinStatus Unavailable(bool fixedLocal, bool plainPath) =>
        new(fixedLocal, plainPath, null, null, null, null);

    private static bool HasPlainAncestry(string path)
    {
        var current = Path.GetPathRoot(path)!;
        if ((File.GetAttributes(current) & UncertainAttributes) != 0) return false;
        foreach (var component in path[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & UncertainAttributes) != 0) return false;
        }
        return true;
    }

    private static string CanonicalLongPath(string path)
    {
        if (!RecyclePathSafety.TryNormalizeLocal(path, out var local)) throw new IOException("Unknown local recycle namespace.");
        var root = Path.GetPathRoot(local)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed || !HasPlainAncestry(local))
            throw new IOException("Recycle path is not a plain fixed-volume path.");
        // GetLongPathName expands 8.3 names, but preserves SUBST/drive aliases. Refuse ambiguous volume mappings too.
        var volume = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, volume, (uint)volume.Capacity)) throw new Win32Exception();
        var mounts = new char[1024];
        if (!GetVolumePathNamesForVolumeName(volume.ToString(), mounts, (uint)mounts.Length, out _)) throw new Win32Exception();
        var roots = new string(mounts).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!RecyclePathSafety.HasSingleVolumeRoot(root, roots))
            throw new IOException("Recycle volume has ambiguous mount aliases.");
        var expanded = new StringBuilder(260);
        var length = GetLongPathName(local, expanded, (uint)expanded.Capacity);
        if (length == 0) throw new Win32Exception();
        if (length >= expanded.Capacity) throw new IOException("Canonical recycle path exceeds conservative shell limits.");
        return expanded.ToString();
    }

    private static bool PoliciesAllowRecycling()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var policy = hive.OpenSubKey(PolicyKey);
            if (!RecycleBinSettings.PolicyAllowsRecycling(policy?.GetValue("NoRecycleFiles"), policy?.GetValue("RecycleBinSize")))
                return false;
            // A legacy global setting is ambiguous alongside the modern per-volume schema.
            using var legacy = hive.OpenSubKey(ExplorerKey + @"\BitBucket");
            if (legacy?.GetValue("NukeOnDelete") != null || legacy?.GetValue("MaxCapacity") != null) return false;
        }
        return true;
    }

    private static bool IsKnownCloudPath(string path)
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var cloud = Environment.GetEnvironmentVariable(variable);
            if (cloud != null && IsWithin(path, cloud)) return true;
        }
        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts != null)
        {
            foreach (var accountName in accounts.GetSubKeyNames())
            {
                using var account = accounts.OpenSubKey(accountName);
                var value = account?.GetValue("UserFolder");
                if (value is string folder && IsWithin(path, folder)) return true;
                if (value is not null and not string) throw new IOException("Unknown OneDrive folder setting.");
            }
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var manager = hive.OpenSubKey(SyncRootKey);
            if (manager == null) continue;
            foreach (var provider in manager.GetSubKeyNames())
            {
                using var roots = manager.OpenSubKey(provider + @"\UserSyncRoots");
                if (roots == null) continue;
                foreach (var user in roots.GetValueNames())
                {
                    if (roots.GetValue(user) is not string cloud) throw new IOException("Unknown cloud sync-root setting.");
                    if (IsWithin(path, cloud)) return true;
                }
            }
        }
        return false;
    }

    private static bool IsWithin(string path, string folder) =>
        !RecyclePathSafety.IsOutsideCloudRoot(path, folder, CanonicalLongPath);

    private static long CandidateBytes(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        var count = 0;
        var discovered = 1;
        var bytes = MetadataHeadroom;
        while (pending.TryPop(out var current))
        {
            if (++count > MaximumEntries) throw new IOException("Recycle candidate exceeds the bounded metadata inspection.");
            if (current.Length >= 260) throw new IOException("Recycle candidate exceeds conservative shell path limits.");
            var attributes = File.GetAttributes(current);
            if ((attributes & UncertainAttributes) != 0) throw new IOException("Recycle candidate contains a cloud or reparse entry.");
            bytes = checked(bytes + MetadataHeadroom + StreamBytes(current));
            if ((attributes & FileAttributes.Directory) == 0) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (++discovered > MaximumEntries) throw new IOException("Recycle candidate exceeds the bounded metadata inspection.");
                pending.Push(entry);
            }
        }
        return bytes;
    }

    private static long StreamBytes(string path)
    {
        // Include alternate data streams; FileInfo.Length alone can understate what the bin must hold.
        var handle = FindFirstStream(path, 0, out var stream, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == EndOfStreams) return 0;
            throw new Win32Exception(error);
        }
        try
        {
            var total = 0L;
            var count = 0;
            do
            {
                if (++count > MaximumEntries || stream.Bytes < 0) throw new IOException("Uncertain recycle stream size.");
                total = checked(total + stream.Bytes);
            } while (FindNextStream(handle, out stream));
            var error = Marshal.GetLastWin32Error();
            if (error != EndOfStreams) throw new Win32Exception(error);
            return total;
        }
        finally { FindClose(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        internal uint Size;
        internal long Bytes;
        internal long Items;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamInfo
    {
        internal long Bytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string Name;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder volume, uint length);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[] roots, uint length, out uint required);

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string path, StringBuilder expanded, uint length);

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string root, ref QueryInfo info);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out StreamInfo stream, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr handle, out StreamInfo stream);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
