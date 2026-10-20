using Avalonia.Controls;
using DotCharter.UI.Controls.Markdown;

namespace DotCharter.Tests.UI;

/// <summary>
/// Structural tests for <see cref="MarkdownView"/>: setting Markdown builds the
/// expected child-control tree with the style classes Markdown.axaml targets.
/// Pure control construction — no headless app session needed.
/// </summary>
public sealed class MarkdownViewTests
{
    [Fact]
    public void SettingMarkdown_BuildsOneChildPerBlock()
    {
        var view = new MarkdownView
        {
            Markdown = "## Heading\n\npara\n\n- bullet\n\n```\ncode\n```\n\n---"
        };

        Assert.Equal(5, view.Children.Count);
        Assert.Contains("MdH2", view.Children[0].Classes);
        Assert.Contains("MdPara", view.Children[1].Classes);
        Assert.IsType<Grid>(view.Children[2]);
        var code = Assert.IsType<Border>(view.Children[3]);
        Assert.Contains("MdCodeBlock", code.Classes);
        var rule = Assert.IsType<Border>(view.Children[4]);
        Assert.Contains("MdRule", rule.Classes);
    }

    [Fact]
    public void ChangingMarkdown_ReplacesChildren()
    {
        var view = new MarkdownView { Markdown = "one\n\ntwo\n\nthree" };
        Assert.Equal(3, view.Children.Count);

        view.Markdown = "single";
        Assert.Single(view.Children);

        view.Markdown = null;
        Assert.Empty(view.Children);
    }

    [Fact]
    public void BulletRow_HasGlyphAndContent()
    {
        var view = new MarkdownView { Markdown = "- item text" };
        var row = Assert.IsType<Grid>(Assert.Single(view.Children));
        Assert.Equal(2, row.Children.Count);
        var glyph = Assert.IsType<TextBlock>(row.Children[0]);
        Assert.Equal("•", glyph.Text);
        Assert.Contains("MdBulletGlyph", glyph.Classes);
        Assert.IsType<SelectableTextBlock>(row.Children[1]);
    }

    [Fact]
    public void InlineFormatting_LandsOnRuns()
    {
        var view = new MarkdownView { Markdown = "**bold** and [link](https://example.com)" };
        var tb = Assert.IsType<SelectableTextBlock>(Assert.Single(view.Children));
        Assert.Equal("bold and link", tb.Inlines?.Text);
    }
}
