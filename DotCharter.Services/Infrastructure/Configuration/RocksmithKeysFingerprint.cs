using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
/// a SHA-256 record that the user once had Rocksmith keys in
///     <c>appconfig.json</c>, kept in <see cref="FileName" /> beside it. Builds before
///     v0.70 delete the <c>RocksmithKeys</c> section when they save; this record is
///     how a later build notices. It holds a hash of the well-formed keys under a
///     fixed domain prefix, NEVER a key, and the file is not part of any backup or
///     update payload.
/// </summary>
internal static class RocksmithKeysFingerprint
{
    public const string FileName = "rocksmith-keys.fingerprint";

    private const string DomainPrefix = "dotcharter-rocksmith-keys-v1";

    /// <summary>What the user is told when the keys are gone but the record remains.</summary>
    public const string VanishedMessage =
        "Your Rocksmith keys were removed from appconfig.json, probably by an older DotCharter version. " +
        "Paste them into the RocksmithKeys section again.";

    /// <summary>The record beside <paramref name="appConfigPath" />.</summary>
    public static string PathBeside(string appConfigPath) =>
        Path.Combine(Path.GetDirectoryName(appConfigPath) ?? string.Empty, FileName);

    /// <summary>The fingerprint of the well-formed keys, lower-case hex, or null when
    /// there is none. Malformed values are ignored, as the product ignores them.</summary>
    public static string? Compute(RocksmithKeysConfig keys)
    {
        var parts = new (string Name, string Value)[]
        {
            (nameof(RocksmithKeysConfig.PsarcToc), keys.PsarcToc),
            (nameof(RocksmithKeysConfig.SngPc), keys.SngPc),
            (nameof(RocksmithKeysConfig.SngMac), keys.SngMac)
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(DomainPrefix));
        var any = false;
        foreach (var (name, value) in parts)
        {
            var key = RocksmithKeysConfig.TryParseKey(value, out _);
            if (key == null) continue;

            any = true;
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(key);
        }

        return any ? Convert.ToHexStringLower(hash.GetHashAndReset()) : null;
    }

    /// <summary>The recorded fingerprint, or null when there is no record.</summary>
    public static string? Read(string recordPath)
    {
        if (!File.Exists(recordPath)) return null;

        var text = File.ReadAllText(recordPath).Trim();
        return text.Length == 0 ? null : text;
    }
}
