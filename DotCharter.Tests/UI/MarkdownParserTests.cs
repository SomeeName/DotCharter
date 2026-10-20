using DotCharter.UI.Controls.Markdown;

namespace DotCharter.Tests.UI;

/// <summary>
/// The in-game updater renders GitHub release-note markdown through
/// <see cref="MarkdownParser"/>. The contract: understand the subset our
/// release notes actually use, and degrade everything else to literal text
/// without ever throwing.
/// </summary>
public sealed class MarkdownParserTests
{
    private static string PlainText(MdBlock block) =>
        string.Concat(block.Inlines.Select(i => i.IsLineBreak ? "\n" : i.Text));

    // ── blocks ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    public void EmptyInput_YieldsNoBlocks(string? input) => Assert.Empty(MarkdownParser.Parse(input));

    [Theory]
    [InlineData("# Title", 1)]
    [InlineData("## Section", 2)]
    [InlineData("### Sub ###", 3)]
    public void Headings_CarryLevelAndText(string line, int level)
    {
        var block = Assert.Single(MarkdownParser.Parse(line));
        Assert.Equal(MdBlockKind.Heading, block.Kind);
        Assert.Equal(level, block.HeadingLevel);
        Assert.DoesNotContain("#", PlainText(block));
    }

    [Fact]
    public void HashWithoutSpace_IsNotAHeading()
    {
        var block = Assert.Single(MarkdownParser.Parse("#hashtag"));
        Assert.Equal(MdBlockKind.Paragraph, block.Kind);
        Assert.Equal("#hashtag", PlainText(block));
    }

    [Fact]
    public void ParagraphLines_JoinWithHardBreaks_AndCrlfIsNormalized()
    {
        var block = Assert.Single(MarkdownParser.Parse("line one\r\nline two"));
        Assert.Equal(MdBlockKind.Paragraph, block.Kind);
        Assert.Equal("line one\nline two", PlainText(block));
    }

    [Fact]
    public void BlankLine_SplitsParagraphs()
    {
        var blocks = MarkdownParser.Parse("first\n\nsecond");
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(MdBlockKind.Paragraph, b.Kind));
    }

    [Fact]
    public void Bullets_CaptureIndentLevels()
    {
        var blocks = MarkdownParser.Parse("- top\n  - nested\n    - deep");
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(MdBlockKind.Bullet, b.Kind));
        Assert.Equal(new[] { 0, 1, 2 }, blocks.Select(b => b.IndentLevel));
    }

    [Fact]
    public void OrderedList_KeepsItsMarker()
    {
        var blocks = MarkdownParser.Parse("1. first\n2) second");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("1.", blocks[0].Marker);
        Assert.Equal("2.", blocks[1].Marker);
        Assert.Equal("second", PlainText(blocks[1]));
    }

    [Fact]
    public void FencedCode_IsVerbatim_HeadingsAndBulletsInsideStayLiteral()
    {
        var blocks = MarkdownParser.Parse("```powershell\n# not a heading\n- not a bullet\n```\nafter");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(MdBlockKind.Code, blocks[0].Kind);
        Assert.Equal("# not a heading\n- not a bullet", blocks[0].Literal);
        Assert.Equal(MdBlockKind.Paragraph, blocks[1].Kind);
    }

    [Fact]
    public void UnterminatedFence_StillShowsItsContent()
    {
        var block = Assert.Single(MarkdownParser.Parse("```\ntrapped"));
        Assert.Equal(MdBlockKind.Code, block.Kind);
        Assert.Equal("trapped", block.Literal);
    }

    [Fact]
    public void HorizontalRule_IsARule_NotABullet()
    {
        var blocks = MarkdownParser.Parse("above\n\n---\n\nbelow");
        Assert.Equal(3, blocks.Count);
        Assert.Equal(MdBlockKind.Rule, blocks[1].Kind);
    }

    [Fact]
    public void BlockquotePrefix_IsStripped()
    {
        var block = Assert.Single(MarkdownParser.Parse("> quoted text"));
        Assert.Equal("quoted text", PlainText(block));
    }

    // ── inlines ─────────────────────────────────────────────────────────

    [Fact]
    public void BoldItalicCode_SetTheirFlags()
    {
        var inlines = MarkdownParser.ParseInlines("**bold** *italic* `code` plain");
        Assert.Equal("bold", Assert.Single(inlines, i => i.Bold).Text);
        Assert.Equal("italic", Assert.Single(inlines, i => i.Italic).Text);
        Assert.Equal("code", Assert.Single(inlines, i => i.Code).Text);
        Assert.Contains(inlines, i => i.Text.Contains("plain") && i is { Bold: false, Italic: false, Code: false });
    }

    [Fact]
    public void TripleEmphasis_IsBoldAndItalic()
    {
        var hit = Assert.Single(MarkdownParser.ParseInlines("***both***"));
        Assert.True(hit.Bold);
        Assert.True(hit.Italic);
        Assert.Equal("both", hit.Text);
    }

    [Fact]
    public void NestedEmphasis_InheritsOuterFlags()
    {
        var inlines = MarkdownParser.ParseInlines("**bold and *both* here**");
        var both = Assert.Single(inlines, i => i.Italic);
        Assert.True(both.Bold);
        Assert.Equal("both", both.Text);
        Assert.All(inlines, i => Assert.True(i.Bold));
    }

    [Fact]
    public void UnclosedEmphasis_StaysLiteral()
    {
        var inlines = MarkdownParser.ParseInlines("2 * 3 * 4 and **unclosed");
        Assert.All(inlines, i => Assert.False(i.Bold || i.Italic));
        Assert.Equal("2 * 3 * 4 and **unclosed", string.Concat(inlines.Select(i => i.Text)));
    }

    [Fact]
    public void EscapedAsterisk_IsLiteral()
    {
        var inlines = MarkdownParser.ParseInlines(@"\*not italic\*");
        Assert.All(inlines, i => Assert.False(i.Italic));
        Assert.Equal("*not italic*", string.Concat(inlines.Select(i => i.Text)));
    }

    [Fact]
    public void Strikethrough_SetsStrike()
    {
        var hit = Assert.Single(MarkdownParser.ParseInlines("~~gone~~"));
        Assert.True(hit.Strike);
        Assert.Equal("gone", hit.Text);
    }

    [Fact]
    public void Link_CarriesUrl_LabelKeepsFormatting()
    {
        var inlines = MarkdownParser.ParseInlines("see [the **big** changelog](https://example.com/log) now");
        var linked = inlines.Where(i => i.LinkUrl == "https://example.com/log").ToList();
        Assert.Equal("the big changelog", string.Concat(linked.Select(i => i.Text)));
        Assert.True(linked.Single(i => i.Text == "big").Bold);
    }

    [Fact]
    public void BareUrl_Autolinks_AndTrailingPunctuationStaysOut()
    {
        var inlines = MarkdownParser.ParseInlines("Full changelog: https://github.com/o/r/compare/a...b.");
        var link = Assert.Single(inlines, i => i.LinkUrl != null);
        Assert.Equal("https://github.com/o/r/compare/a...b", link.LinkUrl);
        Assert.Equal(link.Text, link.LinkUrl);
    }

    [Fact]
    public void Image_DegradesToLinkedAltText()
    {
        var inlines = MarkdownParser.ParseInlines("![screenshot](https://example.com/s.png)");
        var link = Assert.Single(inlines, i => i.LinkUrl != null);
        Assert.Equal("screenshot", link.Text);
        Assert.DoesNotContain(inlines, i => i.Text.Contains('!'));
    }

    [Fact]
    public void RealReleaseNotes_ParseIntoTheExpectedShape()
    {
        const string notes = """
            The consolidation release.

            ## Fixed: dead air

            Some CDLC imports park a bogus beat at t=0 — the count-in trusted it.

            ## Editing

            - **The NOTE tab speaks icons now**: seven technique flags.
            - Render-loop allocation sweep: `InvalidateVisual()` discipline.

            Suite: **4,732 green**.
            """;
        var blocks = MarkdownParser.Parse(notes);
        Assert.Equal(2, blocks.Count(b => b.Kind == MdBlockKind.Heading));
        Assert.Equal(2, blocks.Count(b => b.Kind == MdBlockKind.Bullet));
        Assert.Equal(3, blocks.Count(b => b.Kind == MdBlockKind.Paragraph));
        var lastPara = blocks.Last(b => b.Kind == MdBlockKind.Paragraph);
        Assert.Contains(lastPara.Inlines, i => i is { Bold: true, Text: "4,732 green" });
    }

    [Fact]
    public void HostileInput_NeverThrows()
    {
        string[] nasty =
        [
            "***", "``", "[]()", "[a](", "![", "\\", "~~", "* ", "1.", "> ", "```",
            new string('*', 500), new string('#', 40) + " x",
            "[[[[((((]]]]))))", "https://", "**`*[_~~\\", " ￿🎸"
        ];
        foreach (var input in nasty)
        {
            var blocks = MarkdownParser.Parse(input);
            Assert.NotNull(blocks);
        }
    }
}
