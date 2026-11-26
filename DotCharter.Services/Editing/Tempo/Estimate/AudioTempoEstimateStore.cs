namespace DotCharter.Services.Editing.Tempo.Estimate;

/// <summary>
///     The last whole-song tempo read off each audio file this session, so validation can compare the
///     chart's tempo with the audio's without decoding anything. Only Tempo map ▸ Estimate BPM fills it
///     (<see cref="TempoEstimateService" />, whole song only); until then the audio checks of
///     <c>TempoMapRule</c> stay silent.
/// </summary>
public interface IAudioTempoEstimateStore
{
    /// <summary>Remembers <paramref name="estimate" /> for the audio file at <paramref name="audioPath" />.</summary>
    void Remember(string audioPath, TempoEstimate estimate);

    /// <summary>
    ///     The estimate for the audio file named <paramref name="audioFileName" /> (a bare name or a full path:
    ///     only the file name is compared), or null when none was read this session.
    /// </summary>
    TempoEstimate? Find(string? audioFileName);
}

/// <inheritdoc cref="IAudioTempoEstimateStore" />
public sealed class AudioTempoEstimateStore : IAudioTempoEstimateStore
{
    private readonly Dictionary<string, TempoEstimate> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public void Remember(string audioPath, TempoEstimate estimate)
    {
        var name = Path.GetFileName(audioPath);
        if (name.Length == 0) return;
        lock (_gate) _byFileName[name] = estimate;
    }

    public TempoEstimate? Find(string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName)) return null;
        var name = Path.GetFileName(audioFileName);
        lock (_gate) return _byFileName.GetValueOrDefault(name);
    }
}
