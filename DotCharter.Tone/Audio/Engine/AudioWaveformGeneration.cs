using JuceSharp;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>Generates a file waveform independently of the playback engine and its locks.</summary>
internal static class AudioWaveformGeneration
{
    public static Task<WaveformSamples> GenerateAsync(string filePath, IAppLogger? logger)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.FromResult(WaveformSamples.Empty);

        return Task.Run(() =>
        {
            try
            {
                // Use the shared cache resolution so an old coarse waveform can be refused.
                var blockMs = WaveformSamples.GenerationBlockMs;
                var peaks = JuceAudioEngine.GenerateWaveform(filePath, blockMs);
                return WaveformSamples.FromBasePeaks(peaks, blockMs);
            }
            catch (Exception ex)
            {
                logger?.Error(AppLogCategory.Audio, $"Waveform generation failed for '{filePath}'.", ex);
                return WaveformSamples.Empty;
            }
        });
    }
}
