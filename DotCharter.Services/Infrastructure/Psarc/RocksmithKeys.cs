namespace DotCharter.Services.Infrastructure.Psarc;

/// <summary>
///     The Rocksmith 2014 keys: the PSARC table-of-contents key and the PC and Mac
/// SNG keys. DotCharter ships NONE of them. They are read from the
///     <c>RocksmithKeys</c> object of <c>appconfig.json</c>
/// (<see cref="AppConfig.RocksmithKeys" />), and nothing in the UI names
///     it. Without them, <c>.dcsong</c> works fully and the exporter writes plain
/// packages. The format is in
///     <c>docs/agents/services/parsing-psarc.md</c>.
///     <para>
///         The app points this at its configuration service once, at startup
///         (<see cref="UseConfiguration" />); every read parses the config's current
///         values (cached while they are unchanged). Tests pin the whole process to
///         synthetic keys (<see cref="UseProcessKeys" />, which a later
///         <see cref="UseConfiguration" /> does not override) or scope other keys to
///         their own async flow (<see cref="UseKeysForTesting" />).
///     </para>
/// </summary>
public static class RocksmithKeys
{
    private static readonly AsyncLocal<Func<RocksmithKeysConfig?>?> TestScope = new();
    private static readonly Lock Sync = new();
    private static volatile Func<RocksmithKeysConfig?>? _source;
    private static bool _pinned;
    private static volatile LoadedKeys? _last;

    /// <summary>The keys in use, or null when no well-formed key is configured.</summary>
    public static RocksmithKeySet? Current => Loaded.Keys;

    /// <summary>Whether an encrypted (stock) package can be written.</summary>
    public static bool CanWriteEncrypted => Current is { CanWriteEncrypted: true };

    /// <summary>Whether a Mac package can be written: it is always encrypted.</summary>
    public static bool CanWriteMac => Current is { CanWriteMac: true };

    /// <summary>Why a configured value was not used (field names only, never key
    /// content); null when every value is empty or well-formed. For the log only.</summary>
    public static string? LoadProblem => Loaded.Problem;

    private static LoadedKeys Loaded
    {
        get
        {
            var config = (TestScope.Value ?? _source)?.Invoke();
            if (_last is { } last && last.IsFor(config)) return last;
            var loaded = Load(config);
            _last = loaded;
            return loaded;
        }
    }

    /// <summary>
    ///     Reads the keys from <paramref name="configuration" />'s
    ///     <see cref="AppConfig.RocksmithKeys" />. Called once by the app at startup;
    ///     a no-op in a test process that pinned its keys (<see cref="UseProcessKeys" />).
    /// </summary>
    public static void UseConfiguration(IConfigurationService configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (Sync)
        {
            if (_pinned) return;
            _source = SourceFor(configuration);
            _last = null;
        }
    }

    /// <summary>Process-wide and final: these keys for the whole test process,
    /// whatever <see cref="UseConfiguration" /> is called with later.</summary>
    internal static void UseProcessKeys(RocksmithKeysConfig keys)
    {
        lock (Sync)
        {
            _pinned = true;
            _source = () => keys;
            _last = null;
        }
    }

    /// <summary>Test-only: <paramref name="keys" /> (null = none) for the calling async
    /// flow, so a parallel test never sees them. Dispose to restore.</summary>
    internal static IDisposable UseKeysForTesting(RocksmithKeysConfig? keys) => UseSourceForTesting(() => keys);

    /// <summary>Test-only: what <see cref="UseConfiguration" /> wires, for the calling
    /// async flow. Dispose to restore.</summary>
    internal static IDisposable UseConfigurationForTesting(IConfigurationService configuration) =>
        UseSourceForTesting(SourceFor(configuration));

    private static IDisposable UseSourceForTesting(Func<RocksmithKeysConfig?> source)
    {
        var previous = TestScope.Value;
        TestScope.Value = source;
        return new Scope(previous);
    }

    private static Func<RocksmithKeysConfig?> SourceFor(IConfigurationService configuration) =>
        () => configuration.LoadAppConfig().RocksmithKeys;

    private static LoadedKeys Load(RocksmithKeysConfig? config)
    {
        if (config == null) return new LoadedKeys(null, null, null, null, null);
        var problems = config.Problems();
        return new LoadedKeys(config.PsarcToc, config.SngPc, config.SngMac, RocksmithKeySet.FromConfig(config),
            problems.Count == 0 ? null : string.Join(" ", problems));
    }

    /// <summary>The parse of one set of config values; <see cref="IsFor" /> compares
    /// the values, so an edited config (a backup import) is re-parsed.</summary>
    private sealed record LoadedKeys(string? Toc, string? SngPc, string? SngMac, RocksmithKeySet? Keys, string? Problem)
    {
        public bool IsFor(RocksmithKeysConfig? config) =>
            config == null
                ? Toc == null && SngPc == null && SngMac == null
                : string.Equals(Toc, config.PsarcToc, StringComparison.Ordinal)
                  && string.Equals(SngPc, config.SngPc, StringComparison.Ordinal)
                  && string.Equals(SngMac, config.SngMac, StringComparison.Ordinal);
    }

    private sealed class Scope(Func<RocksmithKeysConfig?>? previous) : IDisposable
    {
        public void Dispose() => TestScope.Value = previous;
    }
}

/// <summary>
///     The well-formed keys of a <see cref="RocksmithKeysConfig" />; any may be
///     missing. A malformed value counts as missing.
/// </summary>
public sealed class RocksmithKeySet
{
    private RocksmithKeySet(byte[]? tocKey, byte[]? sngPcKey, byte[]? sngMacKey)
    {
        TocKey = tocKey;
        SngPcKey = sngPcKey;
        SngMacKey = sngMacKey;
    }

    internal byte[]? TocKey { get; }
    internal byte[]? SngPcKey { get; }
    internal byte[]? SngMacKey { get; }

    /// <summary>A stock package needs the TOC key and the PC SNG key.</summary>
    public bool CanWriteEncrypted => TocKey != null && SngPcKey != null;

    /// <summary>A Mac package needs the TOC key and the Mac SNG key.</summary>
    public bool CanWriteMac => TocKey != null && SngMacKey != null;

    /// <summary>The keys <paramref name="config" /> holds, or null when it holds no
    /// well-formed key at all.</summary>
    internal static RocksmithKeySet? FromConfig(RocksmithKeysConfig config)
    {
        var toc = RocksmithKeysConfig.TryParseKey(config.PsarcToc, out _);
        var pc = RocksmithKeysConfig.TryParseKey(config.SngPc, out _);
        var mac = RocksmithKeysConfig.TryParseKey(config.SngMac, out _);
        return toc == null && pc == null && mac == null ? null : new RocksmithKeySet(toc, pc, mac);
    }
}
