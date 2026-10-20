namespace DotCharter.Tests.Infrastructure;

public sealed class SourceTextTests
{
    private const string Code =
        "var a = 1; // trailing\n" +
        "    // whole line\n" +
        "/* block\n   spanning */ var b = \"http://x\";\n";

    [Fact]
    public void StripComments_CutsBlocksWholeLinesAndTrailingComments()
    {
        var stripped = SourceText.StripComments(Code);

        Assert.DoesNotContain("trailing", stripped);
        Assert.DoesNotContain("whole line", stripped);
        Assert.DoesNotContain("spanning", stripped);
        Assert.Contains("var a = 1;", stripped);
        Assert.Contains("var b = \"http:", stripped);
        Assert.DoesNotContain("http://x", stripped);
    }

    [Fact]
    public void StripCommentLines_KeepsEveryCodeLineWhole()
    {
        var stripped = SourceText.StripCommentLines(Code);

        Assert.Contains("var a = 1; // trailing", stripped);
        Assert.Contains("var b = \"http://x\";", stripped);
        Assert.DoesNotContain("whole line", stripped);
        Assert.DoesNotContain("spanning", stripped);
    }

    [Fact]
    public void BothFlavours_KeepCarriageReturns()
    {
        const string crlf = "var a = 1; // note\r\n// gone\r\nvar b = 2;\r\n";

        Assert.Equal("var a = 1; \r\n\r\nvar b = 2;\r\n", SourceText.StripComments(crlf));
        Assert.Equal("var a = 1; // note\r\n\r\nvar b = 2;\r\n", SourceText.StripCommentLines(crlf));
    }

    [Fact]
    public void StripCommentsKeepingLines_StripsLikeStripComments_ButEveryLineStaysWhereItWas()
    {
        var stripped = SourceText.StripCommentsKeepingLines(Code);

        Assert.DoesNotContain("trailing", stripped);
        Assert.DoesNotContain("spanning", stripped);
        Assert.Equal(Code.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
        Assert.Equal(4, SourceText.LineAt(stripped, stripped.IndexOf("var b", StringComparison.Ordinal)));
        Assert.Equal(1, SourceText.LineAt(stripped, 0));
    }

    [Fact]
    public void LintReport_NamesTheRuleEachOffenderTheFixAndTheGuide()
    {
        var message = LintReport.Of("RULE", ["a.cs:3: x", "b.cs:9: y"], "do z", "docs/g.md");

        Assert.Equal(string.Join(Environment.NewLine, "RULE", "  a.cs:3: x", "  b.cs:9: y", "Fix: do z", "Guide: docs/g.md"), message);
        Assert.DoesNotContain("Guide:", LintReport.Of("RULE", "a.cs", "do z"));
    }

    [Fact]
    public void StripMarkupComments_RemovesMultiLineXmlComments()
    {
        const string markup = "<Grid>\n  <!-- a\n  <Button /> -->\n  <TextBlock />\n</Grid>";

        var stripped = SourceText.StripMarkupComments(markup);

        Assert.DoesNotContain("Button", stripped);
        Assert.Contains("<TextBlock />", stripped);
    }
}
