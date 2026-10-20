using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The REAL Rocksmith game tables for the owner-only tests (#755). The repository
///     holds none of them; they are built here the way the app builds them — from the
///     Rocksmith 2014 install (<see cref="LocalFixtures.RocksmithInstall" />, which also
///     needs the real key file) through the plugin that reads the game install (<c>TestGameInstall</c>), or from a folder of
///     files already extracted from the game gear package (<see cref="GearDirsVariable" />).
///     Every other test runs on <see cref="RsGameTables.Empty" /> (the process default)
///     or on <c>SyntheticRsGame</c>. The real tables' native amp chains are handed to the
///     engine (process-wide, as the app does: JuceSharp #25) the first time they are
///     built, so a real-table render runs the game's own chains.
/// </summary>
internal static class TestRsGameTables
{
    /// <summary>Env var: folders (separated by <see cref="Path.PathSeparator" />) holding
    /// gear manifests and soundbanks extracted from the game gear package, flat. Beats the install.</summary>
    public const string GearDirsVariable = "DOTCHARTER_RS_GEAR_DIRS";

    private static readonly Lazy<(RsGameTables? Tables, string Source)> Real = new(Load);

    /// <summary>Where the real tables came from, for a skip or a failure message.</summary>
    public static string Source => Real.Value.Source;

    /// <summary>Whether the real tables came from a partial extract rather than the install.</summary>
    public static bool FromExtract => Environment.GetEnvironmentVariable(GearDirsVariable) is { Length: > 0 };

    /// <summary>The real tables, or null on a machine without them.</summary>
    public static RsGameTables? TryReal() => Real.Value.Tables;

    /// <summary>What the engine said to the real tables' native amp chains: true
    /// accepted, false refused, null not offered (no tables, or no native library).</summary>
    public static bool? NativeAmpTableAccepted { get; private set; }

    /// <summary>The real tables; SKIPS the calling test without them.</summary>
    public static RsGameTables RequireReal()
    {
        var tables = Real.Value.Tables;
        Assert.SkipWhen(tables is null,
            $"The Rocksmith game tables need a Rocksmith 2014 install and the real key file ({LocalFixtures.RocksmithDirVariable}, " +
            $"{TestRocksmithKeys.KeyFileVariable}) or extracted gear files ({GearDirsVariable}). {Real.Value.Source}");
        return tables!;
    }

    /// <summary>The real tables for the calling async flow; SKIPS without them.</summary>
    public static IDisposable UseReal() => RsGameTables.Scope(RequireReal());

    private static (RsGameTables? Tables, string Source) Load()
    {
        var (tables, source) = Build();
        if (tables?.NativeAmpTable is { } blob)
        {
            try
            {
                NativeAmpTableAccepted = JuceRsAmpTable.TryLoad(blob);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                source += $"; native amp chains not loaded ({ex.GetType().Name})";
            }
        }

        return (tables, source);
    }

    private static (RsGameTables? Tables, string Source) Build()
    {
        if (Environment.GetEnvironmentVariable(GearDirsVariable) is { Length: > 0 } dirs)
        {
            if (TestGameInstall.TableBuilder is not { } builder) return (null, "no plugin that reads the game install");
            var source = new DirectoriesSource(dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            var tables = builder.Build(source, out var problems);
            return (tables, $"built from {GearDirsVariable}; problems: {string.Join("; ", problems)}");
        }

        if (TestGameInstall.TableBuilder is not { } installBuilder) return (null, "no plugin that reads the game install");
        if (LocalFixtures.RocksmithInstall() is not { } install) return (null, "no install with real keys");
        var cache = Path.Combine(Path.GetTempPath(), $"dc-rs-game-tables-{Environment.ProcessId}.json");
        try
        {
            var (built, outcome, detail) = installBuilder.LoadOrBuild(install, cache);
            return (built.IsEmpty ? null : built, $"{outcome} from the install; {detail}");
        }
        finally
        {
            File.Delete(cache);
        }
    }

    /// <summary>Several flat folders as one source; the first folder holding a name wins.</summary>
    private sealed class DirectoriesSource(IReadOnlyList<string> directories) : IRsGameFileSource
    {
        public IEnumerable<string> Names =>
            directories.Where(Directory.Exists).SelectMany(Directory.EnumerateFiles).Select(Path.GetFileName).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase);

        public byte[]? Read(string name)
            => directories.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists) is { } path ? File.ReadAllBytes(path) : null;
    }
}
