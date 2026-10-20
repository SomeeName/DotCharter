using System.Runtime.InteropServices;

namespace DotCharter.Tone.Audio.Midi;

/// <summary>
/// Windows Multimedia (winmm.dll) backed <see cref="IMidiOutput"/>. Sends
/// short MIDI messages to a chosen output device (e.g. "Microsoft GS
/// Wavetable Synth" or a loopMIDI port that feeds Ample Guitar). All calls
/// are synchronous and lock-free aside from a tiny gate around the device
/// handle so close-during-send cannot race.
/// </summary>
// ReSharper disable once InconsistentNaming -- "WinMM" is the established Win32 API initialism.
public sealed class WinMMMidiOutput : IMidiOutput
{
    private const int MaxPNameLen = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPNameLen)]
        public string ProductName;
        public ushort Technology;
        public ushort Voices;
        public ushort Notes;
        public ushort ChannelMask;
        public uint Support;
    }

    [DllImport("winmm.dll", EntryPoint = "midiOutGetNumDevs")]
    private static extern uint MidiOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "midiOutGetDevCapsW")]
    private static extern uint MidiOutGetDevCaps(UIntPtr deviceId, ref MidiOutCaps caps, uint size);

    [DllImport("winmm.dll", EntryPoint = "midiOutOpen")]
    private static extern uint MidiOutOpen(out IntPtr midiOut, int deviceId, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", EntryPoint = "midiOutClose")]
    private static extern uint MidiOutClose(IntPtr midiOut);

    [DllImport("winmm.dll", EntryPoint = "midiOutShortMsg")]
    private static extern uint MidiOutShortMsg(IntPtr midiOut, uint message);

    [DllImport("winmm.dll", EntryPoint = "midiOutReset")]
    private static extern uint MidiOutReset(IntPtr midiOut);

    private readonly object _gate = new();
    private IntPtr _handle = IntPtr.Zero;

    public WinMMMidiOutput(int deviceIndex) => DeviceIndex = deviceIndex;

    public bool IsOpen => _handle != IntPtr.Zero;
    public int DeviceIndex { get; }

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Enumerate available MIDI output devices. Index -1 is the
    /// "MIDI Mapper" (system default), which on every Windows install
    /// resolves to Microsoft GS Wavetable Synth.</summary>
    public static IReadOnlyList<MidiOutputDeviceInfo> EnumerateDevices()
    {
        var result = new List<MidiOutputDeviceInfo>();
        if (!IsSupported) return result;
        result.Add(new MidiOutputDeviceInfo(-1, "Default MIDI Mapper"));
        try
        {
            var n = MidiOutGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                var caps = default(MidiOutCaps);
                if (MidiOutGetDevCaps(i, ref caps, (uint)Marshal.SizeOf<MidiOutCaps>()) == 0)
                    result.Add(new MidiOutputDeviceInfo((int)i, caps.ProductName));
            }
        }
        catch
        {
            // winmm not available — just return the mapper entry.
        }
        return result;
    }

    public void Open()
    {
        if (!IsSupported) return;
        lock (_gate)
        {
            if (_handle != IntPtr.Zero) return;
            var rc = MidiOutOpen(out var h, DeviceIndex, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0)
                throw new InvalidOperationException($"midiOutOpen failed (device {DeviceIndex}, rc={rc})");
            _handle = h;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            try { MidiOutReset(_handle); } catch { /* ignore */ }
            try { MidiOutClose(_handle); } catch { /* ignore */ }
            _handle = IntPtr.Zero;
        }
    }

    public void NoteOn(int channel, int note, int velocity) => Send(0x90 | (channel & 0x0F), note & 0x7F, velocity & 0x7F);

    public void NoteOff(int channel, int note) =>
        // NoteOff via NoteOn velocity=0 (universal, avoids release-curve oddities).
        Send(0x90 | (channel & 0x0F), note & 0x7F, 0);

    public void ProgramChange(int channel, int program) => Send(0xC0 | (channel & 0x0F), program & 0x7F, 0);

    public void ChannelVolume(int channel, int level) => Send(0xB0 | (channel & 0x0F), 7, Math.Clamp(level, 0, 127));

    public void AllSoundOff(int channel)
    {
        Send(0xB0 | (channel & 0x0F), 120, 0); // CC 120 = All Sound Off
        Send(0xB0 | (channel & 0x0F), 123, 0); // CC 123 = All Notes Off
    }

    private void Send(int status, int data1, int data2)
    {
        IntPtr h;
        lock (_gate) { h = _handle; }
        if (h == IntPtr.Zero) return;
        var msg = (uint)((status & 0xFF) | ((data1 & 0xFF) << 8) | ((data2 & 0xFF) << 16));
        try { MidiOutShortMsg(h, msg); } catch { /* ignore */ }
    }

    public void Dispose() => Close();
}

public readonly record struct MidiOutputDeviceInfo(int Index, string Name);
