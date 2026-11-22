using DotCharter.Services.State;

namespace DotCharter.Services.Export.Audio;

/// <summary>A one-work-item guard for adopting measured fields; no scheduling, settled-input cache or dirty state.</summary>
internal sealed class ProjectVolumeAdoption
{
    private readonly WeakReference<ChartProject> _project;
    private readonly WeakReference<ChartData> _chart;
    private readonly ProjectVolumeFields _before;
    private readonly string _path;
    private readonly string? _livePath;
    private readonly string _audio;
    private readonly double? _preview;
    private readonly long _revision;

    private ProjectVolumeAdoption(ProjectState state, string path, ChartProject snapshot)
    {
        _project = new WeakReference<ChartProject>(state.ActiveProject!);
        _chart = new WeakReference<ChartData>(state.ActiveProject!.Chart);
        _before = ProjectVolumeFields.From(snapshot.Chart);
        _path = path;
        _livePath = state.CurrentProjectPath;
        _audio = snapshot.AudioFileName;
        _preview = snapshot.Chart.PreviewStartMs;
        _revision = state.SongAudioRevision;
    }

    public static ProjectVolumeAdoption? Capture(ProjectState state, string path, ChartProject snapshot) =>
        state.ActiveProject is null ? null : new ProjectVolumeAdoption(state, path, snapshot);

    public bool SourceMatches(ChartProject snapshot, IAppLogger logger, int targetLufs)
    {
        if (ProjectVolumeFields.From(snapshot.Chart) == _before) return false;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            var audio = SaveCompanionService.ResolveAudio(directory, _audio);
            if (audio is null) return false;
            var signature = ProjectVolumes.VolumeSignature(ProjectVolumes.Signature(audio), targetLufs);
            if (!_before.AudioManual && snapshot.Chart.AudioVolumeSignature != signature) return false;
            return _before.PreviewManual || snapshot.Chart.PreviewVolumeSignature == ProjectVolumes.ClipSignature(signature, _preview);
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Project, "Automatic export volumes were not adopted because the source identity could not be checked.", ex);
            return false;
        }
    }

    public bool Apply(ProjectState state, ChartProject snapshot)
    {
        if (state.ActiveProject is not { } project || !_project.TryGetTarget(out var source) || !ReferenceEquals(project, source)) return false;
        if (!_chart.TryGetTarget(out var chart) || !ReferenceEquals(project.Chart, chart)) return false;
        if (state.CurrentProjectPath != _path && state.CurrentProjectPath != _livePath) return false;
        if (state.SongAudioRevision != _revision) return false;
        if (project.AudioFileName != _audio || chart.PreviewStartMs != _preview || ProjectVolumeFields.From(chart) != _before) return false;
        var measured = ProjectVolumeFields.From(snapshot.Chart);
        var after = _before with
        {
            Audio = _before.AudioManual ? _before.Audio : measured.Audio,
            AudioSignature = _before.AudioManual ? _before.AudioSignature : measured.AudioSignature,
            Preview = _before.PreviewManual ? _before.Preview : measured.Preview,
            PreviewSignature = _before.PreviewManual ? _before.PreviewSignature : measured.PreviewSignature
        };
        if (after == _before) return false;
        after.ApplyTo(chart);
        return true;
    }
}
