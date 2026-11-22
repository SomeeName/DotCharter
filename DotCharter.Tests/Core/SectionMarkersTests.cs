namespace DotCharter.Tests.Core;

/// <summary>
///     Linked-measure creation, the link suggestions and the link panel each
///     carried their own "section at this position" lookup. <see cref="SectionMarkers" />
///     is the one answer.
/// </summary>
public sealed class SectionMarkersTests
{
    [Fact]
    public void The_latest_named_section_at_or_before_the_position_wins()
    {
        var arrangement = new Arrangement();
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Section = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(6), Section = "" });

        Assert.Equal("intro", SectionMarkers.At(arrangement, new FractionalPosition(3)));
        Assert.Equal("verse", SectionMarkers.At(arrangement, new FractionalPosition(4)));
        Assert.Equal("verse", SectionMarkers.At(arrangement, new FractionalPosition(8)));
    }

    [Fact]
    public void Before_the_first_section_is_empty() =>
        Assert.Equal("", SectionMarkers.At(new Arrangement(), new FractionalPosition(2)));

    [Fact]
    public void No_product_file_keeps_its_own_section_lookup()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (Path.GetFileName(project) == "DotCharter.Tests") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "SectionMarkers.cs") continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("!string.IsNullOrEmpty(e.Section) && e.Position <= ", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, LintReport.Of("the section at a position comes from SectionMarkers.At",
            offenders, "call DotCharter.Core.Utilities.SectionMarkers.At"));
    }
}
