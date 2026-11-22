using JuceSharp;

namespace DotCharter.Tests.Infrastructure;

internal sealed class RecordingRebuildPlugin(byte[] state) : IJuceAudioPlugin
{
    public byte[] State { get; private set; } = state;
    public byte[]? StateAtAttach { get; private set; }
    public Func<int, byte[]>? OnRead { get; set; }
    private int _reads;
    public int ReadCount => _reads;
    public string Path => "effect.vst3";
    public bool IsDisposed { get; private set; }
    public nint Handle => 1;
    public int InputPlayer => 0;
    public bool Bypass { get; set; }
    public int ParameterCount => 0;
    public int LatencySamples => 0;
    public event Action? EditorClosedByUser { add { } remove { } }
    public void AttachToInput(int player = 0) => StateAtAttach = [.. State];
    public void DetachFromInput() { }
    public void AttachToInstrument() => StateAtAttach = [.. State];
    public void DetachFromInstrument() { }
    public void ShowEditor() { }
    public void HideEditor() { }
    public byte[] SaveState()
    {
        _reads++;
        var payload = OnRead?.Invoke(_reads) ?? State;
        return [.. payload];
    }
    public void LoadState(byte[] data) => State = [.. data];
    public float GetParameter(int index) => throw new NotSupportedException();
    public void SetParameter(int index, float value) => throw new NotSupportedException();
    public JuceParameterInfo GetParameterInfo(int index) => throw new NotSupportedException();
    public JuceParameterInfo[] EnumerateParameters() => [];
    public void Dispose() => IsDisposed = true;
}
