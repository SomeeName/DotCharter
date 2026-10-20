using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
/// Smoke-tests the RSCP parser against every real-world project.rscp shipped
/// in TestData/RsCharter and TestData/EOF. Catches regressions where a new
/// element/attribute introduced by RsCharter breaks our parser.
/// </summary>
public class RscpTestDataParseAllTests
{
    private const string MissingCorpus =
        "No RSCP corpus: TestData/**/project.rscp is absent (TestData/ is git-ignored).";

    private static IEnumerable<string> EnumerateRscpFiles()
    {
        var root = FindTestDataRoot();
        if (root == null) yield break;

        foreach (var file in Directory.EnumerateFiles(root, "project.rscp", SearchOption.AllDirectories))
            yield return file;
    }

    [Fact]
    public void Load_RealWorldRscp_DoesNotThrow()
    {
        // TestData/ is gitignored. Without it the loop below has nothing to walk and
        // "failures.Count == 0" passes vacuously — SKIP instead, so the darkness shows.
        var files = EnumerateRscpFiles().ToList();
        Assert.SkipWhen(files.Count == 0, MissingCorpus);

        var serializer = new RscpChartSerializer();
        var failures = new List<string>();

        foreach (var filePath in files)
        {
            try
            {
                var project = serializer.Load(filePath);
                Assert.NotNull(project);
                Assert.NotEmpty(project.Beats);
                Assert.NotEmpty(project.Arrangements);
            }
            catch (Exception ex)
            {
                failures.Add($"{filePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RoundTrip_RealWorldRscp_PreservesCoreData()
    {
        var files = EnumerateRscpFiles().ToList();
        Assert.SkipWhen(files.Count == 0, MissingCorpus);

        var serializer = new RscpChartSerializer();
        var failures = new List<string>();

        foreach (var filePath in files)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"rscp_rt_{Guid.NewGuid():N}.rscp");
            try
            {
                var loaded = serializer.Load(filePath);
                serializer.Save(tempPath, loaded);
                var reloaded = serializer.Load(tempPath);

                Assert.Equal(loaded.Beats.Count, reloaded.Beats.Count);
                Assert.Equal(loaded.Arrangements.Count, reloaded.Arrangements.Count);
                Assert.Equal(loaded.Showlights.Count, reloaded.Showlights.Count);
                Assert.Equal(loaded.VocalTracks.Count, reloaded.VocalTracks.Count);

                for (var i = 0; i < loaded.Arrangements.Count; i++)
                {
                    // The save regenerates chord templates (a chord with none gets
                    // one), on a copy since #304, so the expectation is the loaded
                    // arrangement regenerated the same way. This used to pass only
                    // because the writer regenerated `loaded` itself.
                    var a = ChartProjectCloner.CloneArrangement(loaded.Arrangements[i]);
                    new RscpTemplateManager().RegenerateTemplates(a);
                    var b = reloaded.Arrangements[i];
                    Assert.Equal(a.ArrangementType, b.ArrangementType);
                    Assert.Equal(a.Capo, b.Capo);
                    Assert.Equal(a.CentOffset, b.CentOffset, 3);
                    Assert.Equal(a.Levels.Count, b.Levels.Count);
                    Assert.Equal(a.ChordTemplates.Count, b.ChordTemplates.Count);
                    Assert.Equal(a.Phrases.Count, b.Phrases.Count);
                    Assert.Equal(a.EventPoints.Count, b.EventPoints.Count);
                    Assert.Equal(a.ToneChanges.Count, b.ToneChanges.Count);

                    for (var li = 0; li < a.Levels.Count; li++)
                    {
                        Assert.Equal(a.Levels[li].Notes.Count, b.Levels[li].Notes.Count);
                        Assert.Equal(a.Levels[li].Chords.Count, b.Levels[li].Chords.Count);
                        Assert.Equal(a.Levels[li].HandShapes.Count, b.Levels[li].HandShapes.Count);
                        Assert.Equal(a.Levels[li].Anchors.Count, b.Levels[li].Anchors.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{filePath}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
