# Sets ONE monitor's display scaling (the same call the Settings app makes,
# DisplayConfigSetDeviceInfo type -4), without a sign-out and without touching
# the other monitors. Usage: Set-MonitorScale.ps1 -Device '\\.\DISPLAY5' -Percent 125
param([string]$Device = '\\.\DISPLAY5', [int]$Percent = 100, [switch]$Query)
$src = @'
using System;
using System.Runtime.InteropServices;
public static class DpiCfg {
  [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
  [StructLayout(LayoutKind.Sequential)] public struct PathSourceInfo { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PathTargetInfo { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PathInfo { public PathSourceInfo sourceInfo; public PathTargetInfo targetInfo; public uint flags; }
  [StructLayout(LayoutKind.Sequential)] public struct ModeInfo { public uint infoType; public uint id; public LUID adapterId; public ulong a; public ulong b; public ulong c; public ulong d; public ulong e; public ulong f; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct SourceName { public int type; public int size; public LUID adapterId; public uint id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
  [StructLayout(LayoutKind.Sequential)] public struct GetScale { public int type; public int size; public LUID adapterId; public uint id; public int minRel; public int curRel; public int maxRel; }
  [StructLayout(LayoutKind.Sequential)] public struct SetScale { public int type; public int size; public LUID adapterId; public uint id; public int scaleRel; }
  [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
  [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
  [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetSourceName(ref SourceName p);
  [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetScaleInfo(ref GetScale p);
  [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] public static extern int SetScaleInfo(ref SetScale p);
  public static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

  // Returns: found, minRel, curRel, maxRel; and sets the scale when set != int.MinValue.
  public static string Run(string device, int targetPercent, bool set) {
    uint np, nm;
    if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0) return "GetDisplayConfigBufferSizes failed";
    var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
    if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return "QueryDisplayConfig failed";
    for (int i = 0; i < np; i++) {
      var sn = new SourceName { type = 1, size = Marshal.SizeOf(typeof(SourceName)), adapterId = paths[i].sourceInfo.adapterId, id = paths[i].sourceInfo.id };
      if (GetSourceName(ref sn) != 0) continue;
      if (!string.Equals(sn.viewGdiDeviceName, device, StringComparison.OrdinalIgnoreCase)) continue;
      var gs = new GetScale { type = -3, size = Marshal.SizeOf(typeof(GetScale)), adapterId = sn.adapterId, id = sn.id };
      var rc = GetScaleInfo(ref gs);
      if (rc != 0) return "GetScale failed rc=" + rc;
      var msg = device + " minRel=" + gs.minRel + " curRel=" + gs.curRel + " maxRel=" + gs.maxRel;
      if (!set) return msg;
      // Recommended index is where rel == 0. We do not know the absolute current index
      // from this API alone, so the caller passes the CURRENT percent via the
      // environment (DOTCHARTER_ORCH_CUR_PCT) the first time; afterwards we track it.
      int curPct = int.Parse(Environment.GetEnvironmentVariable("DOTCHARTER_ORCH_CUR_PCT") ?? "100");
      int curIdx = Array.IndexOf(Steps, curPct);
      int recIdx = curIdx - gs.curRel;
      int tIdx = Array.IndexOf(Steps, targetPercent);
      if (tIdx < 0) return "unsupported percent";
      int rel = tIdx - recIdx;
      if (rel < gs.minRel || rel > gs.maxRel) return msg + " -> target rel " + rel + " out of range";
      var ss = new SetScale { type = -4, size = Marshal.SizeOf(typeof(SetScale)), adapterId = sn.adapterId, id = sn.id, scaleRel = rel };
      rc = SetScaleInfo(ref ss);
      return msg + " -> set rel=" + rel + " (" + targetPercent + " %) rc=" + rc;
    }
    return "device not found: " + device;
  }
}
'@
Add-Type -TypeDefinition $src
[DpiCfg]::Run($Device, $Percent, -not $Query)
