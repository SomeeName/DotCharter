using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Tests.Audio.Dsp.Tones;

/// <summary>Loads the committed real RS default-tone JSON fixtures.</summary>
internal static class ToneFixtures
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Audio", "Dsp", "Tones");

    public static string ReadJson(string name) => File.ReadAllText(Path.Combine(Dir, name));

    public static RsTone ParseFixture(string name)
    {
        var json = ReadJson(name);
        return RsToneJsonParser.Parse(new PsarcToneDefinition("", "", json));
    }
}
