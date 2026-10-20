using DotCharter.Services.Export.Audio;
using DotCharter.Services.Infrastructure.Psarc.Write;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #747: the PSARC name list was written with <c>Encoding.ASCII</c>, which turns every
///     non-ASCII character into '?'. The package listed "dcavemujica???a_lead.sng" while its
///     manifests named the real file, and the game resolved nothing. A name the format cannot
///     spell now fails the write, naming the entry, and leaves no file behind.
/// </summary>
public sealed class PsarcWriterAsciiNamesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_psarc_ascii_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void A_non_ascii_entry_name_fails_the_write_instead_of_becoming_question_marks()
    {
        const string name = "songs/bin/generic/dcavemujicaザホールa_lead.sng";
        var path = Path.Combine(_dir, "song_p.psarc");

        var error = Assert.ThrowsAny<ArgumentException>(() =>
            PsarcWriter.WriteFile(path, [new PsarcWriteEntry(name, [1, 2, 3])]));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_non_ascii_sound_bank_name_is_refused()
    {
        using var audio = new MemoryStream(new byte[80_000]);
        using var bank = new MemoryStream();

        var error = Assert.Throws<ArgumentException>(() =>
            SoundBankWriter.Generate("DCAveMujicaザ", audio, bank, 0f, false, WemCodec.Vorbis));

        Assert.Contains("DCAveMujicaザ", error.Message, StringComparison.Ordinal);
    }
}
