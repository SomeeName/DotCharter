namespace DotCharter.Services.State;

/// <summary>Persistent session changes outside history which an unrelated snapshot must preserve.</summary>
internal readonly struct ProjectSnapshotExternalState(ChartProject? live, ChartProject before, ChartProject after)
{
    private readonly string? _audio = before.AudioFileName == after.AudioFileName ? live?.AudioFileName : null;
    private readonly bool? _locked = before.IsBeatMapLocked == after.IsBeatMapLocked ? live?.IsBeatMapLocked : null;

    public void Restore(ChartProject project)
    {
        if (_audio != null) project.AudioFileName = _audio;
        if (_locked.HasValue) project.IsBeatMapLocked = _locked.Value;
    }
}
