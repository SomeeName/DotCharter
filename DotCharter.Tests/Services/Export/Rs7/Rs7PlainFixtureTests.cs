using DotCharter.Services.Export;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>The tests of <see cref="Rs7PlainFixtureTests" /> that need no package reader; its fixtures are in <c>Rs7PlainFixtureTests.Fixtures.cs</c>.</summary>
public sealed partial class Rs7PlainFixtureTests
{
    [Fact]
    public async Task Write_the_fixture()
    {
        var outputDir = Environment.GetEnvironmentVariable("DOTCHARTER_RS7_FIXTURE_OUT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(outputDir), "Opt-in: set DOTCHARTER_RS7_FIXTURE_OUT.");

        var wem = Path.Combine(_tempDir, "audio.wem");
        var data = new byte[4_000];
        new Random(7).NextBytes(data);
        await File.WriteAllBytesAsync(wem, data, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(outputDir);
        await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(
            Rs7PackageExportTests.CreateProject(7),
            new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(outputDir, "rs7fixture7_p.psarc"),
                AudioPath = wem,
                DlcKey = "RS7Fixture7",
                IncludeVocals = false,
                SongLengthMs = 64 * Rs7TestSupport.BeatMs,
                PreviewStartMs = 0
            }, progress: null, TestContext.Current.CancellationToken);
    }
}
