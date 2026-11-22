namespace DotCharter.Tone.Audio.Vst;

/// <summary>Checks request ownership after admission to serialized native VST work.</summary>
internal static class VstChainWork
{
    public static async Task RunAsync(SemaphoreSlim gate, Func<bool> isCurrent, Func<Task> apply)
    {
        if (!await gate.TryEnterAsync().ConfigureAwait(false)) return;
        try
        {
            if (isCurrent()) await apply().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
