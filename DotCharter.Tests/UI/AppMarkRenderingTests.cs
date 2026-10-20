using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The app mark and wordmark in the Play Mode header rendered badly (owner,
///     2026-09-07): the 256 px icon was drawn at 28 px with the default bitmap
///     sampling, which skips most source pixels and leaves a jagged edge, and
///     the letter-spaced wordmark carried ClearType colour fringes on the dark
///     ground. Both fixes are <c>RenderOptions</c> attributes, which a Style
///     cannot set (AVLN2000), so they live inline on every site — and only a
///     source lint notices one going missing.
/// </summary>
public sealed class AppMarkRenderingTests
{
    [Fact]
    public void EveryAppIconImage_AsksForHighQualityScaling()
    {
        var images = Elements(@"<Image[^>]*app-icon\.png[^>]*/>");
        Assert.NotEmpty(images);
        foreach (var (file, element) in images)
        {
            Assert.True(element.Contains("RenderOptions.BitmapInterpolationMode=\"HighQuality\""),
                $"{file}: an app-icon Image must set HighQuality scaling: {element}");
        }
    }

    [Fact]
    public void EveryWordmark_UsesGreyscaleAntialiasing()
    {
        var wordmarks = Elements(@"<TextBlock[^>]*Classes=""FireWordmark[^""]*""[^>]*/>");
        Assert.NotEmpty(wordmarks);
        foreach (var (file, element) in wordmarks)
        {
            Assert.True(element.Contains("TextOptions.TextRenderingMode=\"Antialias\""),
                $"{file}: a FireWordmark must set greyscale antialiasing: {element}");
        }
    }

    private static List<(string File, string Element)> Elements(string pattern)
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var result = new List<(string, string)>();
        foreach (var file in UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var text = UiSourceTree.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.Singleline))
                result.Add((Path.GetFileName(file), m.Value));
        }

        return result;
    }
}
