namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>A single-use child process and its uniquely named native bridge objects.</summary>
internal interface IVstSandboxHost : IVstSandboxChannel, IDisposable
{
    string ShmName { get; }
    string RequestEventName { get; }
    string ResponseEventName { get; }
    event Action<bool>? Exited;
    event Action<string>? EditorClosedByUser;
    event Action<string>? ChildLog;
    Task StartAsync(int sampleRate, int blockSize, TimeSpan? connectTimeout = null);
}
