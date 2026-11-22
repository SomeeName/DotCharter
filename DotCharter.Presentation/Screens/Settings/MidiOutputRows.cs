using System.Collections.ObjectModel;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The MIDI output rows of Settings ▸ Audio: the names the screen lists, the winmm ids behind them, and the row the saved device resolves to.</summary>
internal static class MidiOutputRows
{
    /// <summary>Replaces the names and ids from one enumeration and returns the row the saved device lands on (0 when none matches).</summary>
    public static int Fill(
        ObservableCollection<string> names, List<int> ids, IReadOnlyList<MidiOutputDeviceInfo> devices, AppConfig config)
    {
        names.Clear();
        ids.Clear();
        foreach (var d in devices)
        {
            names.Add(d.Name);
            ids.Add(d.Index);
        }

        var resolved = MidiDeviceResolver.Resolve(config.MidiOutputDevice, config.MidiOutputDeviceName, devices);
        for (var i = 0; i < devices.Count; i++)
        {
            if (devices[i].Index == resolved.Index) return i;
        }

        return 0;
    }

    /// <summary>
    ///     Resolves the row against the list <see cref="Fill" /> BUILT, never a
    ///     fresh enumeration. An empty list means the probe has not landed, and
    ///     the stored device is left exactly as it was.
    /// </summary>
    public static void WriteTo(AppConfig config, IReadOnlyList<int> ids, IReadOnlyList<string> names, int row)
    {
        if (ids.Count == 0) return;
        var valid = row >= 0 && row < ids.Count;
        config.MidiOutputDevice = valid ? ids[row] : -1;
        config.MidiOutputDeviceName = valid ? names[row] : string.Empty;
    }
}
