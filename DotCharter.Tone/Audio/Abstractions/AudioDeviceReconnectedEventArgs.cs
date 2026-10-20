namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
/// Raised by <see cref="IAudioDeviceControl.DeviceReconnected"/> after the
/// engine successfully recovers from a device-lost event (driver reload,
/// USB reconnect, sample-rate change in the host).
/// </summary>
public sealed class AudioDeviceReconnectedEventArgs : EventArgs
{
    public AudioDeviceReconnectedEventArgs(string deviceName, int sampleRate, int bufferFrames)
    {
        DeviceName = deviceName;
        SampleRate = sampleRate;
        BufferFrames = bufferFrames;
    }

    public string DeviceName { get; }
    public int SampleRate { get; }
    public int BufferFrames { get; }
}
