namespace DotCharter.Services.Library;

public sealed class LibraryLoadedSong
{
    public ChartProject Project { get; init; } = new();
    public string AudioEntryPath { get; init; } = "";
    public string ExtractedAudioPath { get; init; } = "";
    public string PlayableAudioPath { get; init; } = "";
    public bool AudioConverted { get; init; }

    public bool AudioExtracted => !string.IsNullOrWhiteSpace(ExtractedAudioPath);
    public bool HasPlayableAudio => !string.IsNullOrWhiteSpace(PlayableAudioPath);
}
