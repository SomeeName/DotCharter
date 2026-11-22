using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Infrastructure;

/// <summary>A self-made mono source and legacy metadata shared by save and autosave migration cases.</summary>
internal static class AutomaticVolumeFixture
{
    public static string WriteMono(string directory)
    {
        Directory.CreateDirectory(directory);
        const int rate = 48000;
        var samples = Enumerable.Range(0, rate * 3)
            .Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 440 * i / rate))).ToArray();
        var path = Path.Combine(directory, "mono.wav");
        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 1, rate));
        return path;
    }

    public static ProjectVolumeFields Legacy(ChartData chart, bool manual)
    {
        var fields = new ProjectVolumeFields(31, 32, "legacy-audio", "legacy-preview", manual, manual);
        fields.ApplyTo(chart);
        return fields;
    }

    public static ProjectVolumeFields Expected(string audio, bool manual)
    {
        var chart = new ChartData();
        if (manual) return Legacy(chart, true);
        Assert.True(ProjectVolumes.Measure(chart, audio, null, null));
        return ProjectVolumeFields.From(chart);
    }
}
