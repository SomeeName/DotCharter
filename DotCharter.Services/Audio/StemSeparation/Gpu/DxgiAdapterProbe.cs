using System.Runtime.InteropServices;

namespace DotCharter.Services.Audio.StemSeparation.Gpu;

/// <summary>
///     Lists the DXGI adapters in <c>IDXGIFactory1::EnumAdapters1</c> order — the order
///     ONNX Runtime's DirectML provider indexes with its <c>device_id</c>
///     (<c>dml_provider_factory.cc</c> calls <c>EnumAdapters1(device_id)</c>) — with what
///     JuceSharp's <c>OnnxGpuAdapters</c> does not report: the LUID, and whether a monitor
///     is attached (<c>IDXGIAdapter::EnumOutputs</c> finds an output).
/// </summary>
/// <remarks>
///     Raw vtable calls, as in <c>OnnxGpuAdapters</c>: three methods are needed and the
///     layouts are frozen by the DXGI ABI. Creates no D3D device, so it is cheap and safe
///     from a settings screen. Windows only; elsewhere, and on any DXGI failure, the list
///     is empty.
/// </remarks>
public static partial class DxgiAdapterProbe
{
    private const uint AdapterFlagSoftware = 2;

    // IDXGIFactory1 {770aae78-f26f-4dba-a829-253c83d1b387}
    private static readonly Guid IidDxgiFactory1 = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    // Vtable slots: IUnknown (0-2), IDXGIObject (3-6). IDXGIFactory (7-11), IDXGIFactory1::EnumAdapters1 = 12.
    // IDXGIAdapter::EnumOutputs = 7, IDXGIAdapter1::GetDesc1 = 10.
    private const int SlotRelease = 2;
    private const int SlotEnumAdapters1 = 12;
    private const int SlotEnumOutputs = 7;
    private const int SlotGetDesc1 = 10;

    /// <summary>Every adapter DXGI reports, software one included, in device-id order. Never throws.</summary>
    public static IReadOnlyList<StemGpuAdapter> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return EnumerateCore();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or SEHException)
        {
            return [];
        }
    }

    private static unsafe List<StemGpuAdapter> EnumerateCore()
    {
        var result = new List<StemGpuAdapter>();
        var iid = IidDxgiFactory1;
        if (CreateDXGIFactory1(&iid, out var factory) < 0 || factory == IntPtr.Zero) return result;
        try
        {
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VtableSlot(factory, SlotEnumAdapters1);
            for (uint i = 0; i < 64; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)VtableSlot(adapter, SlotGetDesc1);
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) < 0) continue;
                    result.Add(new StemGpuAdapter(
                        (int)i,
                        new string(desc.Description).TrimEnd('\0').Trim(),
                        desc.DedicatedVideoMemory,
                        (desc.Flags & AdapterFlagSoftware) != 0,
                        HasOutput(adapter),
                        ((ulong)(uint)desc.AdapterLuidHigh << 32) | desc.AdapterLuidLow));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return result;
    }

    private static unsafe bool HasOutput(IntPtr adapter)
    {
        var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VtableSlot(adapter, SlotEnumOutputs);
        IntPtr output;
        if (enumOutputs(adapter, 0, &output) < 0 || output == IntPtr.Zero) return false;
        Release(output);
        return true;
    }

    private static unsafe IntPtr VtableSlot(IntPtr comObject, int slot) => (*(IntPtr**)comObject)[slot];

    private static unsafe void Release(IntPtr comObject)
    {
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)VtableSlot(comObject, SlotRelease);
        release(comObject);
    }

    [LibraryImport("dxgi.dll")]
    private static unsafe partial int CreateDXGIFactory1(Guid* riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }
}
