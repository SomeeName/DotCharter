using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using DotCharter.Services.Infrastructure.Psarc;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The Rocksmith keys of the test process (#756). DotCharter ships none: the
///     product reads them from <c>appconfig.json</c>'s <c>RocksmithKeys</c>
///     (<see cref="RocksmithKeys" />, #774), and so do the tests — through the same
///     <see cref="RocksmithKeysConfig" />, never through a file the test writes.
///     <para>
///         The process default is the owner's REAL keys when there are some
///         (<see cref="KeyFileVariable" />, else the product's own appconfig.json,
///         else a not-yet-migrated <c>rocksmith-keys.txt</c> beside it — all READ
///         only), so the corpus tests can open stock packages; otherwise the
///         SYNTHETIC keys, so every encrypt/decrypt round trip runs on a public
///         checkout. A test
///         that needs the real keys calls <see cref="RequireRealKeys" /> and skips
///         without them. The real keys are recognised by their SHA-256 alone
///         (<see cref="RealKeySha256" />); <c>RocksmithKeyLeakLintTests</c>
///         keeps the keys themselves out of the tree.
///     </para>
/// </summary>
internal static class TestRocksmithKeys
{
    /// <summary>Env var naming a file with the owner's keys — an appconfig.json (its
    /// <c>RocksmithKeys</c>) or an old <c>rocksmith-keys.txt</c> — when they are not
    /// in the product's location.</summary>
    public const string KeyFileVariable = "DOTCHARTER_RS_KEYS";

    /// <summary>SHA-256 (lower-case hex) of the real PSARC TOC, PC SNG and Mac SNG keys.
    /// Fingerprints only: a hash does not give the key back.</summary>
    public static readonly string[] RealKeySha256 =
    [
        "797ed732235e757fe779ae0517d042803d06ac56a10c7ef55672507d7e67c1ca",
        "3ccc3de4e1d18edae8cca041b0f4ef44ef72e8da2295ef3ada9d1ecb202d239b",
        "71a008be85dda34fb29a59b5f0900212e10810547716995b82fc52c56fd17f5f"
    ];

    /// <summary>Synthetic keys: NOT Rocksmith's, just three distinct patterns.</summary>
    public static readonly byte[] SyntheticToc = Pattern(0x11);

    public static readonly byte[] SyntheticSngPc = Pattern(0x22);
    public static readonly byte[] SyntheticSngMac = Pattern(0x33);

    /// <summary>Points the whole test process at its keys before any test runs. Pinned:
    /// an <c>App</c> built in this process does not repoint it at a config.</summary>
    [ModuleInitializer]
    internal static void Install() => RocksmithKeys.UseProcessKeys(RealKeys() ?? Config(SyntheticToc, SyntheticSngPc, SyntheticSngMac));

    /// <summary>Whether the keys in use are the real ones.</summary>
    public static bool HasRealKeys => RocksmithKeys.Current is { } keys && IsReal(keys.TocKey, 0) && IsReal(keys.SngPcKey, 1);

    /// <summary>SKIPS the calling test unless the real keys are loaded (owner-only).</summary>
    public static void RequireRealKeys() =>
        Assert.SkipUnless(HasRealKeys,
            "The real Rocksmith keys are not on this machine: put them in appconfig.json's RocksmithKeys " +
            $"or set {KeyFileVariable} (see docs/agents/services/parsing-psarc.md).");

    /// <summary>The synthetic keys for the calling async flow.</summary>
    public static IDisposable UseSynthetic() => UseKeys(SyntheticToc, SyntheticSngPc, SyntheticSngMac);

    /// <summary>No keys at all (the default empty section) for the calling async flow.</summary>
    public static IDisposable UseNoKeys() => RocksmithKeys.UseKeysForTesting(new RocksmithKeysConfig());

    /// <summary>These keys (null = left empty) for the calling async flow.</summary>
    public static IDisposable UseKeys(byte[]? toc, byte[]? sngPc, byte[]? sngMac) =>
        RocksmithKeys.UseKeysForTesting(Config(toc, sngPc, sngMac));

    /// <summary>The config section holding these keys as hex (null = left empty).</summary>
    public static RocksmithKeysConfig Config(byte[]? toc, byte[]? sngPc, byte[]? sngMac) => new()
    {
        PsarcToc = toc == null ? string.Empty : Convert.ToHexString(toc),
        SngPc = sngPc == null ? string.Empty : Convert.ToHexString(sngPc),
        SngMac = sngMac == null ? string.Empty : Convert.ToHexString(sngMac)
    };

    /// <summary>The text of a #756-era <c>rocksmith-keys.txt</c>, for the migration tests.</summary>
    public static string LegacyKeyFileText(byte[]? toc, byte[]? sngPc, byte[]? sngMac)
    {
        var lines = new List<string> { "# synthetic test keys" };
        if (toc != null) lines.Add($"psarc-toc = {Convert.ToHexString(toc)}");
        if (sngPc != null) lines.Add($"sng-pc = {Convert.ToHexString(sngPc)}");
        if (sngMac != null) lines.Add($"sng-mac = {Convert.ToHexString(sngMac)}");
        return string.Join('\n', lines) + "\n";
    }

    private static byte[] Pattern(byte seed) => [.. Enumerable.Range(0, 32).Select(i => (byte)(seed + i * 7))];

    private static bool IsReal(byte[]? key, int index) =>
        key != null && Convert.ToHexStringLower(SHA256.HashData(key)) == RealKeySha256[index];

    /// <summary>The owner's keys, READ only: the env var's file, else the product's
    /// appconfig.json, else an old key file beside it. Null when none has a key.</summary>
    private static RocksmithKeysConfig? RealKeys()
    {
        var folder = Path.Combine(UserDataFolders.RoamingAppData, "DotCharter");
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable(KeyFileVariable),
            Path.Combine(folder, "appconfig.json"),
            Path.Combine(folder, LegacyRocksmithKeyFile.FileName)
        ];
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            if (TryRead(path) is { } keys && keys.HasAnyValue()) return keys;
        }

        return null;
    }

    private static RocksmithKeysConfig? TryRead(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (!text.TrimStart().StartsWith('{')) return LegacyRocksmithKeyFile.Parse(text.Split('\n'));
            using var json = JsonDocument.Parse(text);
            return json.RootElement.TryGetProperty(nameof(AppConfig.RocksmithKeys), out var section)
                ? section.Deserialize<RocksmithKeysConfig>()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            return null;
        }
    }
}
