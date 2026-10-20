// UnsupportedExternalProcessInsert: the IExternalProcessInsert of an app without the JUCE engine (the null playback service): never opens.

namespace DotCharter.Tone.Audio.ExternalInsert;

/// <summary>An insert that is never supported, so a session over it attaches and sends tones but never routes audio.</summary>
public sealed class UnsupportedExternalProcessInsert : IExternalProcessInsert
{
    public static UnsupportedExternalProcessInsert Instance { get; } = new();

    private UnsupportedExternalProcessInsert()
    {
    }

    public bool IsSupported => false;

    public bool IsOpen => false;

    public bool Open(ExternalInsertEndpoint endpoint) => false;

    public void Close()
    {
    }

    public ExternalInsertStats? ReadStats() => null;
}
