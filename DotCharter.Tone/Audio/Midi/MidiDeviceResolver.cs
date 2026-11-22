namespace DotCharter.Tone.Audio.Midi;

/// <summary>What <see cref="MidiDeviceResolver.Resolve"/> chose: the WinMM index to open
/// and, when it is the mapper fallback, why.</summary>
public readonly record struct MidiDeviceResolution(int Index, string? FallbackReason);

/// <summary>
/// Pure resolver for the saved MIDI output device. WinMM indices shift when a device is
/// plugged or unplugged, so the saved NAME wins over the saved index; the index is only
/// trusted for configs that predate the name. A device that is gone falls back to the
/// system mapper (-1) with a reason the caller shows.
/// </summary>
public static class MidiDeviceResolver
{
    public const int MapperIndex = -1;

    public static MidiDeviceResolution Resolve(
        int savedIndex, string? savedName, IReadOnlyList<MidiOutputDeviceInfo> devices)
    {
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            foreach (var d in devices)
            {
                if (string.Equals(d.Name, savedName, StringComparison.OrdinalIgnoreCase)) return new MidiDeviceResolution(d.Index, null);
            }
            return new MidiDeviceResolution(MapperIndex,
                $"MIDI output device '{savedName}' is not present; using the default MIDI mapper.");
        }

        if (savedIndex == MapperIndex) return new MidiDeviceResolution(MapperIndex, null);
        foreach (var d in devices)
        {
            if (d.Index == savedIndex) return new MidiDeviceResolution(savedIndex, null);
        }
        return new MidiDeviceResolution(MapperIndex,
            $"MIDI output device index {savedIndex} is not present; using the default MIDI mapper.");
    }
}
