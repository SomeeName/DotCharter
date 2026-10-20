using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     A SYNTHETIC Rocksmith gear install's game tables (#755): invented numbers, chosen so a
///     misread offset, a swapped param id or a lost scaling lands on a visibly wrong value.
///     The tables are read from a checked-in snapshot (<c>Fixtures/synthetic-rs-game-tables.json</c>,
///     the cache format of <c>RsGameTablesJson</c>): the builder that makes them from soundbank
///     bytes is in the optional plugin that reads the game install, and that plugin's tests pin
///     this snapshot to what the builder makes today. So the converter's game paths run on every
///     checkout, with or without the plugin, without a single byte of the game.
/// </summary>
internal static class SyntheticRsGame
{
    /// <summary>The fingerprint the snapshot is stamped with.</summary>
    public const string Fingerprint = "synthetic";

    private static readonly Lazy<RsGameTables> Loaded = new(Load);

    /// <summary>The synthetic install's tables.</summary>
    public static RsGameTables Tables => Loaded.Value;

    /// <summary>The synthetic tables for the calling async flow.</summary>
    public static IDisposable Use() => RsGameTables.Scope(Tables);

    /// <summary>No tables for the calling async flow: a machine without an install.</summary>
    public static IDisposable UseNone() => RsGameTables.Scope(RsGameTables.Empty);

    /// <summary>The synthetic row index of <paramref name="key" />.</summary>
    public static int Model(string key)
    {
        Assert.True(Tables.TryGetAmpIndex(key, out var model), key);
        return model;
    }

    private static RsGameTables Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Audio", "Dsp", "RsToneConversion", "Fixtures", "synthetic-rs-game-tables.json");
        return RsGameTablesJson.TryRead(File.ReadAllBytes(path), Fingerprint)
               ?? throw new InvalidDataException(
                   $"{path} is not a cache of RsGameTablesJson format {RsGameTablesJson.FormatVersion}: regenerate it from the plugin's tests (DOTCHARTER_WRITE_SYNTHETIC_FIXTURE).");
    }
}
