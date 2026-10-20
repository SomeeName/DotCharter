using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Real-artifact probes over the local-only <c>TestData/EOF</c> corpus
///     (git-ignored — every test soft-skips off this machine, same pattern as
///     <c>GpLegacyImportTests.RealGp5File_WithChordDiagrams_ParsesToTheEnd</c>).
///     Format decoders pass synthetic fixtures while returning nothing on real
///     files, so the three 0.58 import fixes (name-derived arrangement types,
///     score metadata, track-name whitespace normalization) are each asserted
///     against the artifacts that exposed them: the five-track Ave Mujica
///     "Crucifix X" GP5 (two tracks NAMED Rhythm, one name with a doubled
///     space in the file bytes) and the Yorushika "Amy" pair (real
///     title/artist metadata in both container generations).
/// </summary>
public sealed class GpRealArtifactImportTests
{
    // Relative to the git-ignored TestData/EOF corpus; Resolve() finds it or skips.
    private const string CrucifixGp5 = @"Ave Mujica - Crucifix X\Ave Mujica - Crucifix X.gp5";
    private const string CrucifixGp = @"Ave Mujica - Crucifix X\Ave Mujica - Crucifix X.gp";
    private const string AmyGp5 = @"Yorushika - Amy\Yorushika - Amy.gp5";
    private const string AmyGp = @"Yorushika - Amy\Yorushika - Amy.gp";

    private static GpImportResult ImportAll(string path, out List<GpTrackInfo> tracks)
    {
        var importer = new GpImporter();
        tracks = importer.GetAvailableTracks(path);
        return importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
    }

    private static string Resolve(string relative)
    {
        var path = LocalFixtures.TestData("EOF", relative);
        Assert.SkipWhen(path == null || !File.Exists(path),
            $"No real artifact at TestData/EOF/{relative} (TestData/ is git-ignored) — this probe is DARK on this machine.");
        return path!;
    }

    [Theory]
    [InlineData(CrucifixGp5)]
    [InlineData(CrucifixGp)]
    public void CrucifixX_RhythmTracksTypeAsRhythm_LeadAsLead_BassAsBass(string path)
    {
        path = Resolve(path);

        var result = ImportAll(path, out var tracks);
        Assert.NotEmpty(result.Arrangements);

        // The audit finding: every track badged Lead, including the tracks the
        // charter NAMED Rhythm. Assert per prefix so a future re-export of the
        // reference file (track count differs between the .gp5 and the .gp)
        // keeps the probe honest without pinning an exact track list. NOTE:
        // duplicate names are real here (the .gp carries two "Rhythm (7
        // String)" tracks), so this is a pair list, not a dictionary.
        var byName = result.Arrangements.Select(a => (a.Name, a.ArrangementType)).ToList();
        Assert.Contains(byName, kv => kv.Name.StartsWith("Rhythm", StringComparison.Ordinal));
        foreach (var (name, type) in byName)
        {
            if (name.StartsWith("Rhythm", StringComparison.Ordinal))
                Assert.Equal(ArrangementType.Rhythm, type);
            else if (name.StartsWith("Lead", StringComparison.Ordinal))
                Assert.Equal(ArrangementType.Lead, type);
            else if (name.Contains("Bass", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(ArrangementType.Bass, type);
        }

        // Not every arrangement may come out Lead — that is the bug shape.
        Assert.Contains(result.Arrangements, a => a.ArrangementType != ArrangementType.Lead);

        // The doubled space is IN the file bytes ("Lead (6  String Drop D)") —
        // both the track list and the arrangements must come out normalized.
        foreach (var t in tracks) Assert.DoesNotContain("  ", t.Name);
        foreach (var a in result.Arrangements) Assert.DoesNotContain("  ", a.Name);
        Assert.Contains(result.Arrangements, a => a.Name == "Lead (6 String Drop D)");
    }

    [Theory]
    [InlineData(AmyGp5)]
    [InlineData(AmyGp)]
    public void Amy_ScoreMetadata_IsReadFromBothContainerGenerations(string path)
    {
        path = Resolve(path);

        var result = ImportAll(path, out _);
        Assert.NotEmpty(result.Arrangements);

        Assert.Equal("Amy", result.Metadata.Title);
        Assert.Equal("Yorushika", result.Metadata.Artist);

        // And the type probe on a second artifact: the bass track must not
        // default to Lead.
        Assert.Contains(result.Arrangements,
            a => a.Name.Contains("Bass", StringComparison.OrdinalIgnoreCase) &&
                 a.ArrangementType == ArrangementType.Bass);
    }
}
