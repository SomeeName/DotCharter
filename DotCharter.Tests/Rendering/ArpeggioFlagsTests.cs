namespace DotCharter.Tests.Rendering;

/// <summary>
///     An arpeggio draws a purple bracket / purple hand-shape lane (and, since
///     #480, a purple 2D hand-shape bar) instead of the plain one. Which flag
///     says "arpeggio" depends on where the chart came from, and Rendering read
///     only one of the two until v0.44 — so every RS-XML-imported arpeggio (the
///     primary Rocksmith import path) drew as a plain shape. Nothing failed; the
///     bracket was just absent. The fold was Rendering's own <c>ArpeggioFlags</c>
///     until <c>ChordTemplate.IsArpeggio</c> landed in Core; the snapshot
///     projection asks that now.
/// </summary>
public sealed class ArpeggioFlagsTests
{
    /// <summary>
    ///     the package importer / the EOF parser / the chord-template editor set
    ///     this one, from chord mask 0x1.
    /// </summary>
    [Fact]
    public void TheSngSpellingIsAnArpeggio() => Assert.True(new ChordTemplate { Arpeggio = true }.IsArpeggio);

    /// <summary>
    ///     `RsXmlSongReader` sets ONLY this one, from a `-arp` displayName suffix —
    ///     and `ChartSpacingEnforcer` re-folds a `-arp` NAME into it on every save,
    ///     so a chart can acquire it long after import. This is the half that was
    ///     missing.
    /// </summary>
    [Fact]
    public void TheRsXmlSpellingIsAlsoAnArpeggio() => Assert.True(new ChordTemplate { ForceArpeggioInRs = true }.IsArpeggio);

    [Fact]
    public void BothSpellingsAtOnceIsStillJustAnArpeggio() => Assert.True(new ChordTemplate { Arpeggio = true, ForceArpeggioInRs = true }.IsArpeggio);

    /// <summary>A plain chord must NOT acquire a bracket — the OR is not a tautology.</summary>
    [Fact]
    public void APlainTemplateIsNotAnArpeggio() => Assert.False(new ChordTemplate { Name = "Am" }.IsArpeggio);

    /// <summary>
    ///     The fold must agree with what the RS-XML WRITER derives the `-arp`
    ///     suffix from, or a chart round-trips through export as an arpeggio while
    ///     rendering as a plain shape. This pins the two to the same predicate.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void TheFoldMatchesTheRsXmlWritersArpSuffixRule(bool arpeggio, bool forceRs, bool expected)
    {
        var template = new ChordTemplate { Arpeggio = arpeggio, ForceArpeggioInRs = forceRs };

        // RsXmlSongWriter: displayName = name + (ct.Arpeggio || ct.ForceArpeggioInRs ? "-arp" : "")
        var writerSaysArpeggio = template.Arpeggio || template.ForceArpeggioInRs;

        Assert.Equal(expected, template.IsArpeggio);
        Assert.Equal(writerSaysArpeggio, template.IsArpeggio);
    }

    /// <summary>
    ///     The whole point of folding is that no renderer reads a raw flag: the
    ///     projection asks <c>ChordTemplate.IsArpeggio</c>. A renderer that reads
    ///     `template.Arpeggio` off a <see cref="ChordTemplate" /> again reintroduces
    ///     the bug for itself, silently, on one import path only — so lint for it
    ///     rather than trusting the convention.
    /// </summary>
    [Fact]
    public void NothingInRenderingReadsTheRawRsXmlFlag()
    {
        var renderingRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering");

        var offenders = Directory
            .EnumerateFiles(renderingRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => StripCommentsAndStrings(File.ReadAllText(path))
                .Contains("ForceArpeggioInRs", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(renderingRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "ChordTemplate's raw arpeggio flags must not be read in Rendering — ask ChordTemplate.IsArpeggio; " +
            "reading one alone is dead on the other import path. Offenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    ///     …and the same for the other spelling: nothing outside the single
    ///     snapshot projection may read <c>ChordTemplate.Arpeggio</c>. The
    ///     projection writes the folded value onto <c>RenderChordTemplate</c>, whose
    ///     own field is named <c>Arpeggio</c> too, so the lint keys on the
    ///     Core-typed local the projection uses (<c>t.Arpeggio</c>).
    /// </summary>
    [Fact]
    public void NoRendererProjectsTheRawCoreArpeggioFlag()
    {
        var projection = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Data", "FrameSnapshot.Events.cs");
        var source = StripCommentsAndStrings(File.ReadAllText(projection));

        Assert.DoesNotContain("Arpeggio = t.Arpeggio", source, StringComparison.Ordinal);
        Assert.Contains("Arpeggio = t.IsArpeggio", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Crude but sufficient: the lints above must not be satisfied by a mention
    ///     inside the doc comment that explains the rule.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }

            if (source[i] == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }

                continue;
            }

            sb.Append(source[i]);
        }

        return sb.ToString();
    }
}
