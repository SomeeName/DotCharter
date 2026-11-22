namespace DotCharter.Services.Export;

/// <summary>The export audio seam: project padding can be supplied here without changing any writer.</summary>
internal static class ProjectExportAudio
{
    internal static string ForSnapshot(ChartProject snapshot, string workingAudioPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return workingAudioPath;
    }
}
