using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>IFileOperation supports recycle-only flags that the older SHFileOperation WORD cannot represent.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRecycleOperation
{
    private static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    internal static void Execute(string path, uint flags)
    {
        var type = Type.GetTypeFromCLSID(FileOperationClass, throwOnError: true)!;
        var operation = (IFileOperation)Activator.CreateInstance(type)!;
        IShellItem? item = null;
        try
        {
            var iid = ShellItemInterface;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            operation.SetOperationFlags(flags);
            operation.DeleteItem(item, IntPtr.Zero);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || File.Exists(path) || Directory.Exists(path)) throw new IOException("Recycling was refused or aborted.");
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    // Vtable order and signatures: Windows SDK ShObjIdl_core.h, IFileOperation.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
