namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>Remembers terminal load failures for one seat; callers hold the shared VST gate.</summary>
internal sealed class VstSandboxLoadRejections
{
    private readonly Dictionary<Guid, string> _paths = [];

    public bool Contains(VstChainEntry entry) => _paths.TryGetValue(entry.Id, out var path)
        && string.Equals(path, entry.PluginPath, StringComparison.OrdinalIgnoreCase);

    public void Remember(VstChainEntry entry, VstSandboxMessage response)
    {
        if (response.Ok == false) _paths[entry.Id] = entry.PluginPath;
    }

    public bool ShouldSkip(VstChainEntry entry, IAppLogger? logger)
    {
        if (Contains(entry)) return true;
        if (!string.IsNullOrWhiteSpace(entry.PluginPath)
            && (File.Exists(entry.PluginPath) || Directory.Exists(entry.PluginPath)))
        {
            return false;
        }
        _paths[entry.Id] = entry.PluginPath;
        logger?.Warning(AppLogCategory.Audio, $"VST3 chain (sandbox): missing plugin '{entry.PluginPath}'.");
        return true;
    }

    public void RetainUnchanged(IReadOnlyList<VstChainEntry> previous, IReadOnlyList<VstChainEntry> next)
    {
        foreach (var entry in previous)
        {
            if (!next.Any(desired => desired.Id == entry.Id && desired.Bypass == entry.Bypass
                && !VstSandboxChainPlan.MustUnload(desired, entry.PluginPath, entry.Player)))
            {
                _paths.Remove(entry.Id);
            }
        }
    }
}
