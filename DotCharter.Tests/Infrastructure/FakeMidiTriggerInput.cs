using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.Tests.Infrastructure;

/// <summary>A MIDI controller for tests: <see cref="Press" /> is a pad hit, already on the UI thread.</summary>
internal sealed class FakeMidiTriggerInput : IMidiTriggerInput
{
    public event Action<MidiTrigger>? Pressed;

    public event Action? StatusChanged;

    public bool IsAvailable { get; set; } = true;

    public bool IsConnected { get; set; }

    public string? DeviceName { get; set; }

    public string? Error { get; set; }

    public int PressedSubscribers => Pressed?.GetInvocationList().Length ?? 0;

    public void Press(MidiTrigger trigger) => Pressed?.Invoke(trigger);

    public void Connect(string device)
    {
        DeviceName = device;
        IsConnected = true;
        StatusChanged?.Invoke();
    }

    public void RaiseStatusChanged() => StatusChanged?.Invoke();
}
