using DotCharter.Services.State;

namespace DotCharter.Services.Export.Audio;

/// <summary>The persisted loudness values, their source signatures and override flags.</summary>
public sealed record ProjectVolumeFields(double? Audio, double? Preview, string AudioSignature,
    string PreviewSignature, bool AudioManual, bool PreviewManual)
{
    public static ProjectVolumeFields From(ChartData chart) => new(chart.AudioVolume, chart.PreviewVolume,
        chart.AudioVolumeSignature, chart.PreviewVolumeSignature, chart.AudioVolumeManual, chart.PreviewVolumeManual);

    public void ApplyTo(ChartData chart)
    {
        chart.AudioVolume = Audio;
        chart.PreviewVolume = Preview;
        chart.AudioVolumeSignature = AudioSignature;
        chart.PreviewVolumeSignature = PreviewSignature;
        chart.AudioVolumeManual = AudioManual;
        chart.PreviewVolumeManual = PreviewManual;
    }
}

/// <summary>One persisted volume change; undo restores the measurement provenance too.</summary>
public sealed class SetProjectVolumesAction(ChartData chart, ProjectVolumeFields before, ProjectVolumeFields after)
    : IChartAction, ILinkPolicyAction
{
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;
    public bool HasEffect => before != after;
    public void Execute() => after.ApplyTo(chart);
    public void Undo() => before.ApplyTo(chart);
}
