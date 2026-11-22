using JuceSharp;

namespace DotCharter.Tone.Audio.Vst;

internal static class VstRebuildState
{
    /// <summary>The caller holds _vstGate, which owns plugin teardown, across the wrapper capture.</summary>
    public static byte[]? Capture(IJuceAudioPlugin? plugin, IAppLogger? logger)
    {
        try
        {
            if (plugin is null) return null;
            return plugin.SaveState();
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Audio, "VST state capture before engine rebuild failed", ex);
            return null;
        }
        finally
        {
            GC.KeepAlive(plugin);
        }
    }

    public static List<VstChainEntry>? Merge(List<VstChainEntry>? queued, List<VstChainEntry>? captured)
    {
        if (queued is null) return captured;
        if (captured is null) return queued;
        var previous = captured.ToDictionary(entry => entry.Id);
        foreach (var entry in queued)
        {
            if (entry.State is null && previous.TryGetValue(entry.Id, out var saved)
                && string.Equals(entry.PluginPath, saved.PluginPath, StringComparison.OrdinalIgnoreCase))
            {
                entry.State = saved.State;
            }
        }
        return queued;
    }

    public static void Restore(IJuceAudioPlugin plugin, byte[]? state, Action attach)
    {
        if (state is not null) plugin.LoadState(state);
        attach();
    }
}
