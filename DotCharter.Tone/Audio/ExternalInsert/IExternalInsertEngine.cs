// IExternalInsertEngine: the three native calls of the external process insert (ABI v47), as a seam so ExternalProcessInserts is testable without juce_sharp.dll; JuceExternalInsertEngine is the one real implementation.
using JuceSharp;

namespace DotCharter.Tone.Audio.ExternalInsert;

/// <summary>One engine's external process insert surface (ABI v47).</summary>
internal interface IExternalInsertEngine
{
    ExternalProcessInsertOpenResult Open(int player, string mappingName, string? magic);

    bool Close(int player);

    bool TryGetStats(int player, out ExternalProcessInsertStats stats);
}

/// <summary>The real engine: each call pins the native handle (JuceAudioEngine does).</summary>
internal sealed class JuceExternalInsertEngine(JuceAudioEngine engine) : IExternalInsertEngine
{
    public ExternalProcessInsertOpenResult Open(int player, string mappingName, string? magic)
        => engine.OpenExternalProcessInsert(player, mappingName, magic);

    public bool Close(int player) => engine.CloseExternalProcessInsert(player);

    public bool TryGetStats(int player, out ExternalProcessInsertStats stats)
        => engine.TryGetExternalProcessInsertStats(player, out stats);
}
