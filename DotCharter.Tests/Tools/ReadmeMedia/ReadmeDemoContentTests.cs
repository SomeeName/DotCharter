using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Tools.ReadmeMedia;

/// <summary>
///     The README demo content generator (issue #912) as a test: it always writes the demo set
///     to a temporary folder and checks it is a chart worth photographing, and with
///     <c>DOTCHARTER_README_DEMO_DIR</c> set it writes to that folder instead, which is how
///     <c>tools/readme-media/Capture-ReadmeMedia.ps1</c> gets the content it points the app at.
/// </summary>
public sealed class ReadmeDemoContentTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dc-readme-demo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DemoSet_isAWorthwhileChart_withBothArrangementsAndTheTechniquesTheReadmeShows()
    {
        var root = Environment.GetEnvironmentVariable("DOTCHARTER_README_DEMO_DIR") is { Length: > 0 } target
            ? target
            : _temp;
        var (path, packages) = await ReadmeDemoContent.WriteAllAsync(root);

        var project = new RscpChartSerializer().Load(path);
        Assert.Equal("DotCharter Demo", project.Chart.SongName);
        Assert.Equal([6, 7], project.Arrangements.Select(a => a.StringCount).Order());
        var lead = project.Arrangements.Single(a => a.StringCount == 6).Levels[0];
        var seven = project.Arrangements.Single(a => a.StringCount == 7).Levels[0];
        Assert.Contains(lead.Notes, n => n.SlideTo.HasValue);
        Assert.Contains(lead.Notes, n => n.BendValues.Count > 0);
        Assert.Contains(lead.Notes, n => n.Hopo == HopoType.HammerOn);
        Assert.Contains(lead.Notes, n => n.Hopo == HopoType.PullOff);
        Assert.Contains(lead.Notes, n => n.Vibrato);
        Assert.NotEmpty(lead.Chords);
        Assert.All(project.Arrangements.Single(a => a.StringCount == 6).ChordTemplates, t => Assert.NotEmpty(t.Fingers));
        Assert.Contains(seven.Notes, n => n.StringIndex == 0);
        Assert.NotEmpty(project.VocalTracks.Single().Phrases);
        Assert.Equal(3, project.ToneDefinitions.Count);
        Assert.True(new FileInfo(Path.Combine(root, ReadmeDemoContent.ProjectFolder, "song.wav")).Length > 5_000_000);
        Assert.True(new FileInfo(Path.Combine(root, ReadmeDemoContent.ProjectFolder, "cover.png")).Length > 10_000);
        Assert.Single(Directory.GetFiles(Path.Combine(root, ReadmeDemoContent.ProjectFolder, "ai-chats", project.ProjectId), "*.json"));
        // One package per song when ffmpeg is there, none when it is not.
        Assert.True(packages.Count is 0 or 5, $"expected 0 or 5 packages, got {packages.Count}.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
