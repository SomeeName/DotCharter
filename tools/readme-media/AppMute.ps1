# Mutes the Windows audio session(s) of ONE process (by PID) through the Core Audio session API, so
# a README capture can play a song on a real machine without a sound. Only the named process is
# touched; the system volume and every other application are left alone.
#   AppMute.ps1 -ProcessId 1234            # mute (also on a session that appears later: re-run)
#   AppMute.ps1 -ProcessId 1234 -Unmute
param([Parameter(Mandatory)][int]$ProcessId, [switch]$Unmute)
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class DcAppMute {
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator { int EnumAudioEndpoints(int f, int s, out IntPtr d); int GetDefaultAudioEndpoint(int f, int r, out IMMDevice d); }
  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice { int Activate(ref Guid id, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
  [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionManager2 { int a(); int b(); int GetSessionEnumerator(out IAudioSessionEnumerator e); }
  [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionEnumerator { int GetCount(out int c); int GetSession(int i, out IAudioSessionControl2 s); }
  [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionControl2 { int a(); int b(); int c(); int d(); int e(); int f(); int g(); int h(); int i(); int j(); int k(); int GetProcessId(out uint pid); }
  [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface ISimpleAudioVolume { int SetMasterVolume(float v, ref Guid c); int GetMasterVolume(out float v); int SetMute(bool m, ref Guid c); }
  public static int Set(uint pid, bool mute) {
    var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
    IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);
    var iid = typeof(IAudioSessionManager2).GUID; object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
    IAudioSessionEnumerator se; ((IAudioSessionManager2)o).GetSessionEnumerator(out se);
    int n; se.GetCount(out n); int hit = 0; var ctx = Guid.Empty;
    for (int i = 0; i < n; i++) {
      IAudioSessionControl2 s; se.GetSession(i, out s); uint p; s.GetProcessId(out p);
      if (p == pid) { ((ISimpleAudioVolume)s).SetMute(mute, ref ctx); hit++; }
    }
    return hit;
  }
}
"@
$hit = [DcAppMute]::Set([uint32]$ProcessId, -not $Unmute)
"audio sessions of pid $ProcessId $(if ($Unmute) { 'unmuted' } else { 'muted' }): $hit"
