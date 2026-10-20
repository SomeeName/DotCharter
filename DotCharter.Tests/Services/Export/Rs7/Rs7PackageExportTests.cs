using System.Buffers.Binary;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>The tests of <see cref="Rs7PackageExportTests" /> that need no package reader; its fixtures are in <c>Rs7PackageExportTests.Fixtures.cs</c>.</summary>
public sealed partial class Rs7PackageExportTests
{
    [Fact]
    public async Task Without_keys_a_seven_string_package_is_plain_with_the_warning()
    {
        using var _ = TestRocksmithKeys.UseNoKeys();
        var wem = Path.Combine(_tempDir, "nokeys.wem");
        await File.WriteAllBytesAsync(wem, new byte[4_000], TestContext.Current.CancellationToken);
        var result = await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(
            ChartProjectCloner.Clone(CreateProject(7)),
            new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(_tempDir, "rs7nokeys_p.psarc"),
                AudioPath = wem,
                DlcKey = "rs7nokeys",
                IncludeVocals = false,
                SongLengthMs = BeatCount * Rs7TestSupport.BeatMs,
                PreviewStartMs = 0
            }, progress: null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("PLAIN package (unencrypted)", StringComparison.Ordinal));
        var bytes = await File.ReadAllBytesAsync(result.PsarcPath, TestContext.Current.CancellationToken);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(28)) & 4u);
    }

    [Fact]
    public async Task An_export_removes_the_sidecar_an_older_build_left()
    {
        var stale = Rs7ChordSidecar.PathFor(Path.Combine(_tempDir, "rs7stale_p.psarc"), null);
        var unrelated = Path.Combine(_tempDir, "rs7stale_p.notes.json");
        await File.WriteAllTextAsync(stale, "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(unrelated, "{}", TestContext.Current.CancellationToken);

        await BuildAsync(CreateProject(7), "rs7stale_p.psarc", plain: null);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(unrelated), "a file the exporter never wrote was deleted");
    }

    [Fact]
    public void Seven_string_guitar_is_a_warning_naming_the_mod_not_an_error()
    {
        var findings = new ChartExportValidator().Validate(CreateProject(7));
        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyStringOutOfRange);
        var notice = Assert.Single(findings, f => f.MessageKey == ChartExportValidator.KeyExtendedRangeGuitar);
        Assert.Equal(ExportFindingSeverity.Warning, notice.Severity);
        Assert.Equal("7", notice.Detail);
        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyEightStringMod);
    }

    [Fact]
    public async Task Eight_string_guitar_is_refused_by_the_validator_and_the_builder()
    {
        var project = CreateProject(8);
        var findings = new ChartExportValidator().Validate(project);
        Assert.Equal(ExportFindingSeverity.Error,
            Assert.Single(findings, f => f.MessageKey == ChartExportValidator.KeyEightStringMod).Severity);
        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyExtendedRangeGuitar);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BuildAsync(project, "rs7eight_p.psarc", plain: null));
        Assert.Contains("8-string guitar", ex.ToString(), StringComparison.Ordinal);
        Assert.Contains("not exported yet", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_six_string_package_stays_stock()
    {
        var project = CreateProject(7);
        var arrangement = project.Arrangements[0];
        arrangement.StringCount = 6;
        arrangement.TuningPitches = [.. Enumerable.Range(0, 6).Select(_ => new TuningOffset(0))];
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes) note.StringIndex = Math.Min(note.StringIndex, 5);
            level.Chords.Clear();
        }

        Assert.Null(Rs7Projection.TryCreate(arrangement));
        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyExtendedRangeGuitar);
    }
}
