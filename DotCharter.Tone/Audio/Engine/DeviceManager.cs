namespace DotCharter.Tone.Audio.Engine;

/// <summary>
/// JUCE migration shim. The original DeviceManager owned PortAudio host-API
/// enumeration and ASIO/WASAPI probe state. The new native engine
/// (DotCharter.Audio + JuceSharp.Native) will own that knowledge
/// directly, so for now this is an empty data class that lets the
/// <see cref="DotCharter.Tone.Audio.Abstractions.IAudioPlaybackService"/>
/// surface continue to compile.
/// </summary>
public sealed record DeviceManager
{
    public IReadOnlyList<string> AsioOutputDevices { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AsioInputDevices { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WasapiOutputDevices { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WasapiInputDevices { get; init; } = Array.Empty<string>();
}
