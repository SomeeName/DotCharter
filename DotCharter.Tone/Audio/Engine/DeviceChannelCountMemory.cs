namespace DotCharter.Tone.Audio.Engine;

/// <summary>
/// The channel counts the last healthy settings apply read for each device, so
/// the watchdog's recovery rebuild does not have to ask again.
/// <para>
/// Asking means a live enumeration that rescans every driver type, once for the
/// inputs and once for the outputs. After an unplug that is slow, and it is
/// also pointless: the dead engine can only describe the device it last had
/// open, which is exactly what was remembered while it was healthy. A count of
/// 0 means "not known" and is never remembered. Thread-safe.
/// </para>
/// </summary>
internal sealed class DeviceChannelCountMemory
{
    private readonly Dictionary<(string Driver, string Device, bool Input), int> _counts = new(KeyComparer.Instance);
    private readonly Lock _lock = new();

    public void Remember(string driver, string device, bool input, int count)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(device)) return;
        lock (_lock) _counts[(driver, device, input)] = count;
    }

    public bool TryRecall(string driver, string device, bool input, out int count)
    {
        lock (_lock) return _counts.TryGetValue((driver, device, input), out count);
    }

    private sealed class KeyComparer : IEqualityComparer<(string Driver, string Device, bool Input)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Driver, string Device, bool Input) x, (string Driver, string Device, bool Input) y) =>
            x.Input == y.Input
            && StringComparer.OrdinalIgnoreCase.Equals(x.Driver, y.Driver)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Device, y.Device);

        public int GetHashCode((string Driver, string Device, bool Input) key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Driver),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Device),
                key.Input);
    }
}
