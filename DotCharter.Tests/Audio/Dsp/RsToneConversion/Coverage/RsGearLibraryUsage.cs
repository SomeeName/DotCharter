using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Coverage;

/// <summary>
///     How often each Rocksmith gear key appears in the owner's song library: the
///     ranking the coverage audit (<see cref="RsGearCoverageAudit" />) orders its
///     gaps by, so the next phase closes the gear people actually hear first.
///     <para>
///         <b>Two halves.</b> <see cref="Scan" /> walks a folder of packages READ-ONLY
///         through the product's own reader (<c>PsarcToneReader</c> +
///         <see cref="RsToneJsonParser" />), and <see cref="Snapshot" /> is the
///         committed result (<c>rs_gear_library_usage.json</c> beside this file). The
///         audit reads only the snapshot, so it regenerates identically on CI and in
///         a worktree that has no library. Only gear KEYS and counts are stored —
///         no song names, no paths.
///     </para>
///     <para>
///         A tone counts once however many packages carry it: the library holds a
///         <c>_m</c> (Mac) and a <c>_p</c> (PC) copy of most songs, and a tone is
///         identified by its key AND a hash of its body so two songs that reuse a
///         key with different gear still count twice.
///     </para>
/// </summary>
public sealed class RsGearLibraryUsage
{
    /// <summary>The committed snapshot's path, relative to the repository root.</summary>
    public const string SnapshotRelativePath =
        "DotCharter.Tests/Audio/Dsp/RsToneConversion/Coverage/rs_gear_library_usage.json";

    private RsGearLibraryUsage(int packages, int tones, IReadOnlyDictionary<string, int> gear)
    {
        Packages = packages;
        Tones = tones;
        Gear = gear;
    }

    /// <summary>Packages that contributed at least one distinct tone (a song's
    /// second copy contributes none).</summary>
    public int Packages { get; }

    /// <summary>Distinct tones counted.</summary>
    public int Tones { get; }

    /// <summary>Gear key -> the number of distinct tones that use it (any slot).</summary>
    public IReadOnlyDictionary<string, int> Gear { get; }

    /// <summary>An empty usage table — what <see cref="Snapshot" /> returns when the
    /// file is missing, so the audit still renders (with every count 0).</summary>
    public static RsGearLibraryUsage Empty { get; } =
        new(0, 0, new Dictionary<string, int>(StringComparer.Ordinal));

    public int CountOf(string gearKey) => Gear.GetValueOrDefault(gearKey);

    /// <summary>The committed snapshot, or <see cref="Empty" /> when it is absent.</summary>
    public static RsGearLibraryUsage Snapshot()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), SnapshotRelativePath);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;
    }

    /// <summary>
    ///     Walks every <c>*.psarc</c> under <paramref name="root" /> (recursively),
    ///     opening each read-only. One unreadable package is skipped, never fatal.
    /// </summary>
    public static RsGearLibraryUsage Scan(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var gear = new Dictionary<string, int>(StringComparer.Ordinal);
        var tones = 0;
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, tone) in DistinctTones(root))
        {
            tones++;
            packages.Add(package);
            var inTone = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in tone.InSignalOrder())
            {
                if (g.GearKey.Length > 0 && g.GearKey != RsToneJsonParser.SyntheticAmpKey)
                    inTone.Add(g.GearKey);
            }

            foreach (var key in inTone)
                gear[key] = gear.GetValueOrDefault(key) + 1;
        }

        return new RsGearLibraryUsage(packages.Count, tones, gear);
    }

    /// <summary>
    ///     Every distinct tone under <paramref name="root" /> (recursively, every
    ///     <c>*.psarc</c>, opened READ-ONLY), with the package it was first seen in.
    ///     A tone is its key plus a hash of its body, so the <c>_m</c>/<c>_p</c> copies
    ///     of one song count once. Ordered by package path, then by the package's own
    ///     tone order — deterministic for a given library. One unreadable package is
    ///     skipped, never fatal. The fidelity harness renders these.
    /// </summary>
    public static IEnumerable<(string Package, RsTone Tone)> DistinctTones(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        // The package reader is the optional plugin's; without it there is nothing to read.
        if (TestPackageReader.ToneImporter is not { } importer) yield break;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.psarc", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            IReadOnlyList<PsarcToneDefinition> definitions;
            try
            {
                definitions = importer.ReadToneDefinitions(file);
            }
            catch (Exception) // The reader throws several shapes on an encrypted/odd file.
            {
                continue;
            }

            foreach (var definition in definitions)
            {
                if (!seen.Add(definition.Key + "|" + Hash(definition.Json))) continue;
                yield return (file, RsToneJsonParser.Parse(definition));
            }
        }
    }

    /// <summary>The snapshot's JSON: stable key order (count desc, then key) so a
    /// re-scan of an unchanged library rewrites the file byte-identically.</summary>
    public string ToJson()
    {
        var gearObj = new JsonObject();
        foreach (var (key, count) in Gear
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            gearObj[key] = count;
        }

        var root = new JsonObject
        {
            ["_comment"] =
                "Rocksmith gear usage in the owner song library (the Steam Rocksmith2014 dlc folder, incl. copied), " +
                "scanned read-only by RsGearLibraryUsageScanTests. Gear key: number of distinct tones using it. " +
                "Read by RsGearCoverageAudit to rank gaps; regenerate with DOTCHARTER_RS_LIBRARY_SCAN=1.",
            ["packages"] = Packages,
            ["tones"] = Tones,
            ["gear"] = gearObj
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    internal static RsGearLibraryUsage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var gear = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("gear", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in g.EnumerateObject())
            {
                if (p.Value.TryGetInt32(out var n)) gear[p.Name] = n;
            }
        }

        var packages = root.TryGetProperty("packages", out var pk) && pk.TryGetInt32(out var pv) ? pv : 0;
        var tones = root.TryGetProperty("tones", out var tn) && tn.TryGetInt32(out var tv) ? tv : 0;
        return new RsGearLibraryUsage(packages, tones, gear);
    }

    private static string Hash(string? text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")))[..16];
}
