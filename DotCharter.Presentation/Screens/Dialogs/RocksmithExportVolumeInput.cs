using DotCharter.Core.Domain.Project;
using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The immutable chart/source inputs of one dialog measurement, captured before any await.</summary>
internal sealed class RocksmithExportVolumeInput
{
    private readonly Task<Sources?> _sources;
    private readonly ProjectVolumeFields _fields;
    private int _retired;

    public RocksmithExportVolumeInput(RocksmithExportViewModel screen, ChartData chart, IAppLogger logger,
        Func<string, string>? signature = null, int targetLufs = RocksmithAutoVolume.DefaultTargetLufs)
    {
        AudioPath = screen.AudioPath;
        PreviewPath = screen.PreviewAudioPath;
        TargetLufs = targetLufs;
        Snapshot = new ChartData { PreviewStartMs = chart.PreviewStartMs };
        _fields = ProjectVolumeFields.From(chart);
        _fields.ApplyTo(Snapshot);
        _sources = Task.Run(() => Identify(logger, signature ?? ProjectVolumes.Signature));
    }

    public string AudioPath { get; }
    public string PreviewPath { get; }
    public int TargetLufs { get; }
    public ChartData Snapshot { get; }
    public Task Ready => _sources;
    public bool IsRetired => Volatile.Read(ref _retired) != 0;
    public void Retire() => Interlocked.Exchange(ref _retired, 1);

    public async Task<bool> MatchesAsync(RocksmithExportVolumeInput? other, RocksmithExportContinuation? continuation = null)
    {
        if (other is null || AudioPath != other.AudioPath || PreviewPath != other.PreviewPath || TargetLufs != other.TargetLufs ||
            Snapshot.PreviewStartMs != other.Snapshot.PreviewStartMs || _fields != other._fields)
        {
            return false;
        }
        var sources = await _sources.WaitAsync(continuation?.Token ?? default);
        if (continuation?.IsCurrent == false) return false;
        var otherSources = await other._sources.WaitAsync(continuation?.Token ?? default);
        return continuation?.IsCurrent != false && sources is not null && sources == otherSources;
    }

    private Sources? Identify(IAppLogger logger, Func<string, string> signature)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AudioPath)) return null;
            return new Sources(signature(AudioPath.Trim()),
                string.IsNullOrWhiteSpace(PreviewPath) ? null : signature(PreviewPath.Trim()), PreviewClip.DefaultLengthMs);
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Project, "Could not identify pending export-volume inputs; a fresh measurement will use the normal fallback.", ex);
            return null;
        }
    }

    // Content plus PCM policy fixes decoded duration, and therefore the effective clip clamp and length.
    private sealed record Sources(string Audio, string? Preview, double ClipLengthMs);
}
