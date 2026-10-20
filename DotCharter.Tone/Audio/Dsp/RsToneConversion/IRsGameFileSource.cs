// IRsGameFileSource: the files an RsGameTables builder reads, by archive entry name — the seam between the tables and whoever reads the game install.
namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>The files a game-table builder reads: the gear manifests and soundbanks of
/// the game's gear archive and <c>init.bnk</c> (the Game Parameter defaults, in
/// the game's audio archive), by archive entry name (or a flat folder of them).</summary>
internal interface IRsGameFileSource
{
    /// <summary>Every entry name.</summary>
    IEnumerable<string> Names { get; }

    /// <summary>The bytes of <paramref name="name" />, or null when it cannot be read.</summary>
    byte[]? Read(string name);
}
