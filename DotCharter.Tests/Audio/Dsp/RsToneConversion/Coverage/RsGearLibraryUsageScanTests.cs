namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Coverage;

/// <summary>
///     The library-usage snapshot: a pure round-trip in the default gate, and the
///     scan itself on request.
/// </summary>
public sealed class RsGearLibraryUsageScanTests
{
    /// <summary>Set to <c>1</c> to rescan the Rocksmith library and rewrite the
    /// committed snapshot. The scan opens every package READ-ONLY.</summary>
    public const string ScanVariable = "DOTCHARTER_RS_LIBRARY_SCAN";

    /// <summary>Optional: the folder to scan instead of the detected
    /// <c>&lt;Rocksmith2014&gt;\dlc</c>.</summary>
    public const string RootVariable = "DOTCHARTER_RS_LIBRARY_ROOT";

    [Fact]
    public void Snapshot_RoundTripsByteIdentically()
    {
        // The committed file must be exactly what ToJson writes for what Parse
        // reads, or a rescan of an unchanged library would show a diff.
        var path = Path.Combine(TestPaths.RepoRoot(), RsGearLibraryUsage.SnapshotRelativePath);
        Assert.True(File.Exists(path), $"missing {RsGearLibraryUsage.SnapshotRelativePath}");

        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        var usage = RsGearLibraryUsage.Parse(text);
        Assert.True(usage.Tones > 0 && usage.Gear.Count > 0, "the committed snapshot is empty");
        Assert.Equal(text, usage.ToJson());
    }

    [Fact]
    public void Snapshot_NamesOnlyRealCatalogueGear()
    {
        // A key the catalogue does not know is still worth COUNTING (custom
        // CDLC gear), but the audit then has no knob data for it, so it is
        // listed separately; this pins that every such key is at least a
        // well-formed gear key, never a song name or path leaking in.
        var usage = RsGearLibraryUsage.Snapshot();
        foreach (var key in usage.Gear.Keys)
        {
            Assert.DoesNotContain('\\', key);
            Assert.DoesNotContain('/', key);
            Assert.Matches("^[A-Za-z0-9_]+$", key);
        }
    }

    [Fact]
    public void Rescan_WritesTheSnapshot_WhenAsked()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(ScanVariable) == "1",
            $"set {ScanVariable}=1 to rescan the Rocksmith library (read-only) and rewrite the snapshot");

        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = TestGameInstall.Directories
                .Select(d => Path.Combine(d, "dlc"))
                .FirstOrDefault(Directory.Exists);
        }

        Assert.SkipWhen(root is null || !Directory.Exists(root), "no Rocksmith dlc folder found");

        var usage = RsGearLibraryUsage.Scan(root!);
        Assert.True(usage.Tones > 0, $"no tones found under {root}");

        File.WriteAllText(
            Path.Combine(TestPaths.RepoRoot(), RsGearLibraryUsage.SnapshotRelativePath),
            usage.ToJson());
    }
}
