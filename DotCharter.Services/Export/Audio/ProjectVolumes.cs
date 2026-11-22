using System.Security.Cryptography;

namespace DotCharter.Services.Export.Audio;

/// <summary>Derived gains for the packaged PCM, cached by source content, target and requested preview input.</summary>
public static class ProjectVolumes
{
    public const double DefaultAudioVolume = -8.0;
    public const double DefaultPreviewVolume = -7.0;

    /// <summary>Packaged PCM policy, source size and a hash of every byte; no decoder is needed.</summary>
    public static string Signature(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return $"packaged-pcm-v{PcmAudioLoader.DecodePolicyVersion}:{stream.Length}:{Convert.ToHexStringLower(hash)[..16]}";
    }

    /// <summary>Requested start, including automatic selection. Content identity already captures duration changes.</summary>
    public static string ClipSignature(string audioSignature, double? startMs) =>
        $"{audioSignature}@{startMs?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}:clip-input-v1";

    internal static string VolumeSignature(string sourceSignature, int targetLufs) =>
        FormattableString.Invariant($"{sourceSignature}:volume-v1:{targetLufs}");

    public static double AudioOrDefault(ChartData chart) => chart.AudioVolume ?? DefaultAudioVolume;
    public static double PreviewOrDefault(ChartData chart) => chart.PreviewVolume ?? DefaultPreviewVolume;

    /// <summary>Checks signatures before decoding. Typed values are preserved; unreadable inputs are reported through warn.</summary>
    public static bool Measure(ChartData chart, string audioPath, string? previewPath, double? previewStartMs,
        Action<string>? warn = null, int targetLufs = RocksmithAutoVolume.DefaultTargetLufs) =>
        Measure(chart, audioPath, previewPath, previewStartMs, warn, path => PcmAudioLoader.Load(path, []), targetLufs);

    internal static bool Measure(ChartData chart, string audioPath, string? previewPath, double? previewStartMs,
        Action<string>? warn, Func<string, PcmAudio> decode, int targetLufs = RocksmithAutoVolume.DefaultTargetLufs)
    {
        if (chart.AudioVolumeManual && chart.PreviewVolumeManual) return false;
        targetLufs = RocksmithAutoVolume.ClampTarget(targetLufs);
        var decoded = new Dictionary<string, PcmAudio>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        try
        {
            var audioSignature = VolumeSignature(Signature(audioPath), targetLufs);
            if (!chart.AudioVolumeManual && (chart.AudioVolume is null || chart.AudioVolumeSignature != audioSignature))
            {
                chart.AudioVolume = RocksmithAutoVolume.FromPcm(Read(RocksmithAutoVolume.SourceFor(audioPath)), targetLufs);
                chart.AudioVolumeSignature = audioSignature;
                changed = true;
            }
            changed |= MeasurePreview(chart, audioPath, audioSignature, previewPath, previewStartMs, Read, targetLufs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warn?.Invoke($"Could not measure the volume of '{Path.GetFileName(audioPath)}': {ex.Message}");
        }
        return changed;

        PcmAudio Read(string path)
        {
            if (decoded.TryGetValue(path, out var pcm)) return pcm;
            pcm = decode(path);
            decoded.Add(path, pcm);
            return pcm;
        }
    }

    private static bool MeasurePreview(ChartData chart, string audioPath, string audioSignature, string? previewPath,
        double? previewStartMs, Func<string, PcmAudio> decode, int targetLufs)
    {
        if (chart.PreviewVolumeManual) return false;
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            var fileSignature = VolumeSignature(Signature(previewPath), targetLufs);
            if (chart.PreviewVolume is not null && chart.PreviewVolumeSignature == fileSignature) return false;
            chart.PreviewVolume = RocksmithAutoVolume.FromPcm(decode(RocksmithAutoVolume.SourceFor(previewPath)), targetLufs);
            chart.PreviewVolumeSignature = fileSignature;
            return true;
        }

        var requestedStart = previewStartMs ?? chart.PreviewStartMs;
        var clipSignature = ClipSignature(audioSignature, requestedStart);
        if (chart.PreviewVolume is not null && chart.PreviewVolumeSignature == clipSignature) return false;
        var pcm = decode(audioPath);
        var totalMs = pcm.Duration.TotalMilliseconds;
        var startMs = PreviewClip.ClampStartMs(requestedStart ?? PreviewClip.DefaultStartMs(totalMs), totalMs);
        var clip = PreviewClip.Create(pcm, startMs);
        chart.PreviewVolume = RocksmithAutoVolume.FromPcm(clip, targetLufs);
        chart.PreviewVolumeSignature = clipSignature;
        return true;
    }
}
