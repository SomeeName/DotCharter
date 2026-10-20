namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
///     The <c>rocksmith-keys.txt</c> that older builds read the keys
///     from. Nothing reads it any more: <see cref="JsonConfigurationService" /> moves
///     its values into <see cref="AppConfig.RocksmithKeys" /> ONCE and deletes it after
///     the config holding them is on disk. A malformed file is left where it is and
///     the reason logged (no key bytes in it). Format, one key per line, <c>#</c>
///     comments allowed, any line may be missing:
///     <c>psarc-toc = hex</c>, <c>sng-pc = hex</c>, <c>sng-mac = hex</c>.
/// </summary>
internal static class LegacyRocksmithKeyFile
{
    public const string FileName = "rocksmith-keys.txt";

    private const string TocKeyName = "psarc-toc";
    private const string SngPcKeyName = "sng-pc";
    private const string SngMacKeyName = "sng-mac";

    /// <summary>The file beside <paramref name="appConfigPath" />.</summary>
    public static string PathBeside(string appConfigPath) =>
        Path.Combine(Path.GetDirectoryName(appConfigPath) ?? string.Empty, FileName);

    /// <summary>The file's keys as config values (upper-case hex). Throws
    /// <see cref="FormatException" /> with a line number, never a key, when a line is
    /// not a 32-byte <c>name = hex</c> pair with a known name.</summary>
    public static RocksmithKeysConfig Parse(IEnumerable<string> lines)
    {
        var keys = new RocksmithKeysConfig();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) throw new FormatException($"line {number} is not 'name = hex'.");
            var name = line[..equals].Trim();
            var key = RocksmithKeysConfig.TryParseKey(line[(equals + 1)..], out var problem)
                      ?? throw new FormatException($"line {number} {problem ?? "is empty"}.");
            var hex = Convert.ToHexString(key);
            switch (name.ToLowerInvariant())
            {
                case TocKeyName:
                    keys.PsarcToc = hex;
                    break;
                case SngPcKeyName:
                    keys.SngPc = hex;
                    break;
                case SngMacKeyName:
                    keys.SngMac = hex;
                    break;
                default:
                    throw new FormatException($"line {number} names an unknown key '{name}'.");
            }
        }

        return keys;
    }

    /// <summary>Copies each of <paramref name="from" />'s values into an EMPTY field of
    /// <paramref name="into" />: a key already in appconfig.json wins.</summary>
    public static void FillEmpty(RocksmithKeysConfig into, RocksmithKeysConfig from)
    {
        if (string.IsNullOrWhiteSpace(into.PsarcToc)) into.PsarcToc = from.PsarcToc;
        if (string.IsNullOrWhiteSpace(into.SngPc)) into.SngPc = from.SngPc;
        if (string.IsNullOrWhiteSpace(into.SngMac)) into.SngMac = from.SngMac;
    }
}
