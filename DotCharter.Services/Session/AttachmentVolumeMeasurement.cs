using DotCharter.Services.Export.Audio;

namespace DotCharter.Services.Session;

/// <summary>Measures a detached copy; only the current attachment may apply its derived values.</summary>
internal static class AttachmentVolumeMeasurement
{
    public static async Task<bool> MeasureAsync(ChartProject project, string audioPath, Func<bool> mayApply, IAppLogger logger,
        int targetLufs = RocksmithAutoVolume.DefaultTargetLufs)
    {
        var chart = project.Chart;
        var reference = project.AudioFileName;
        var before = ProjectVolumeFields.From(chart);
        var previewStart = chart.PreviewStartMs;
        var measured = new ChartData
        {
            AudioVolume = chart.AudioVolume, AudioVolumeSignature = chart.AudioVolumeSignature,
            AudioVolumeManual = chart.AudioVolumeManual, PreviewVolume = chart.PreviewVolume,
            PreviewVolumeSignature = chart.PreviewVolumeSignature, PreviewVolumeManual = chart.PreviewVolumeManual,
            PreviewStartMs = chart.PreviewStartMs
        };
        try
        {
            if (!await Task.Run(() => ProjectVolumes.Measure(measured, audioPath, null, null,
                    warning => logger.Warning(AppLogCategory.Audio, warning), targetLufs)))
            {
                return false;
            }
            if (!mayApply() || project.AudioFileName != reference || !ReferenceEquals(chart, project.Chart)) return false;
            if (chart.PreviewStartMs != previewStart || ProjectVolumeFields.From(chart) != before) return false;
            var changed = false;
            if (!chart.AudioVolumeManual && (chart.AudioVolume != measured.AudioVolume || chart.AudioVolumeSignature != measured.AudioVolumeSignature))
            {
                chart.AudioVolume = measured.AudioVolume;
                chart.AudioVolumeSignature = measured.AudioVolumeSignature;
                changed = true;
            }
            if (!chart.PreviewVolumeManual && (chart.PreviewVolume != measured.PreviewVolume || chart.PreviewVolumeSignature != measured.PreviewVolumeSignature))
            {
                chart.PreviewVolume = measured.PreviewVolume;
                chart.PreviewVolumeSignature = measured.PreviewVolumeSignature;
                changed = true;
            }
            return changed;
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Audio, $"Could not measure the export volumes of '{audioPath}'", ex);
            return false;
        }
    }

}
