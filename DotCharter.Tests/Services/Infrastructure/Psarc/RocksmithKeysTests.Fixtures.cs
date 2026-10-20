using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure.Psarc;

/// <summary>
///     #756: DotCharter ships no Rocksmith keys; they come from appconfig.json (#774).
///     Every key here is SYNTHETIC and reaches the product as a RocksmithKeysConfig, the
///     section the app reads. Without keys, plain packages work fully and an
///     encrypted one fails with the neutral message; the exporter writes plain.
/// </summary>
public sealed partial class RocksmithKeysTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-rskeys-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static byte[] PackSng(RocksmithSng sng)
    {
        using var output = new MemoryStream();
        RocksmithSngWriter.WritePackedPc(sng, output);
        return output.ToArray();
    }

    private static byte[] PlainSng(RocksmithSng sng)
    {
        using var output = new MemoryStream();
        RocksmithSngWriter.WritePlainPc(sng, output);
        return output.ToArray();
    }

    private static RocksmithSng Sng()
    {
        var sng = new RocksmithSng();
        sng.Beats.Add(new RsBeat(0.0f, 0, 0, 3, 0));
        sng.Phrases.Add(new RsPhrase(0, 1, "COUNT"));
        return sng;
    }
}
