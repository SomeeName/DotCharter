using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>Pure tests for the local NAM-capture library brain: filtering
/// (<see cref="NamCaptureIndex.Filter" />) and the untrusted-name → safe local
/// file-name rules (<see cref="NamCaptureIndex.BuildFileName" />).</summary>
public sealed class NamCaptureIndexTests
{
    [Fact]
    public void MissingIdsKeepDifferentModelDownloadsSeparate()
    {
        var first = NamCaptureIndex.BuildFileName(0, "Amp", "standard", "nam", "https://example.test/a.nam");
        var second = NamCaptureIndex.BuildFileName(0, "Amp", "standard", "nam", "https://example.test/b.nam");
        Assert.NotEqual(first, second);
        Assert.Equal(first,
            NamCaptureIndex.BuildFileName(0, "Amp", "standard", "nam", "https://example.test/a.nam"));
    }

    private static NamCaptureManifest Capture(
        string title, string author = "someone", string gearType = "amp", string format = "nam") =>
        new(1, 1, title, author, gearType, format, "standard", "CC0", "", "", "f.nam", "00", 1,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void Filter_Empty_ReturnsEmpty() => Assert.Empty(NamCaptureIndex.Filter([], "x"));

    [Fact]
    public void Filter_Text_MatchesTitleOrAuthor_CaseInsensitive()
    {
        var all = new[]
        {
            Capture("Plexi Crunch"),
            Capture("Clean Blackface", author: "PlexiFan"),
            Capture("Fuzz Wall")
        };

        var hits = NamCaptureIndex.Filter(all, "plexi");

        Assert.Equal(2, hits.Count);
        Assert.Equal(["Plexi Crunch", "Clean Blackface"], hits.Select(m => m.Title));
    }

    [Fact]
    public void Filter_GearAndFormat_MatchExactly()
    {
        var all = new[]
        {
            Capture("A", gearType: "amp", format: "nam"),
            Capture("B", gearType: "amp-cab", format: "nam"),
            Capture("C", gearType: "amp", format: "ir")
        };

        Assert.Equal(["A", "C"], NamCaptureIndex.Filter(all, gearType: "AMP").Select(m => m.Title));
        Assert.Equal(["A", "B"], NamCaptureIndex.Filter(all, format: "nam").Select(m => m.Title));
        Assert.Equal(["A"], NamCaptureIndex.Filter(all, gearType: "amp", format: "nam").Select(m => m.Title));
    }

    [Fact]
    public void Filter_BlankCriteria_PassEverything()
    {
        var all = new[] { Capture("A"), Capture("B") };
        Assert.Equal(2, NamCaptureIndex.Filter(all, "  ", "", null).Count);
    }

    // ── file naming (untrusted remote titles) ────────────────────────────────

    [Theory]
    [InlineData("Plexi Crunch!", "standard", "nam", "9-plexi-crunch-standard.nam")]
    [InlineData("../../evil", "lite", "nam", "9-evil-lite.nam")]
    [InlineData("C:\\Windows\\cmd", "", "nam", "9-c-windows-cmd.nam")]
    [InlineData("københavn tone", "nano", "nam", "9-k-benhavn-tone-nano.nam")]
    [InlineData("", "", "nam", "9.nam")]
    [InlineData("Room IR", "", "ir", "9-room-ir.wav")]
    // aida-x / keras spellings map to .aidax
    [InlineData("Aida Amp", "", "aida-x", "9-aida-amp.aidax")]
    [InlineData("Aida Amp", "", "aidax", "9-aida-amp.aidax")]
    // Unknown / empty format falls back to .nam — NEVER the unloadable .bin.
    [InlineData("Mystery", "", "weird", "9-mystery.nam")]
    [InlineData("Mystery", "", "", "9-mystery.nam")]
    public void BuildFileName_SlugsUntrustedInput(string title, string size, string format, string expected) => Assert.Equal(expected, NamCaptureIndex.BuildFileName(9, title, size, format));

    [Theory]
    // Unknown format but a URL that names a real neural file → trust the URL ext.
    [InlineData("", "https://t3k/models/marshall.nam?sig=abc", ".nam")]
    [InlineData("", "https://t3k/models/vox.aidax", ".aidax")]
    [InlineData("", "https://t3k/models/room.wav#frag", ".wav")]
    // A non-neural URL ext is ignored; safe default .nam.
    [InlineData("", "https://t3k/download/82067.bin", ".nam")]
    // A KNOWN format always wins over the URL.
    [InlineData("nam", "https://t3k/x.aidax", ".nam")]
    public void ExtensionFor_ResolvesFromFormatThenUrl(string format, string url, string expected) => Assert.Equal(expected, NamCaptureIndex.ExtensionFor(format, url));

    [Fact]
    public void ExtensionFor_NeverReturnsBin()
    {
        foreach (var f in new[] { "", "bin", "weird", "unknown", "keras", "h5" })
            Assert.NotEqual(".bin", NamCaptureIndex.ExtensionFor(f));
    }

    [Theory]
    [InlineData("nam", "nam")]
    [InlineData("NAM", "nam")]
    [InlineData("aida-x", "aidax")]
    [InlineData("aida_x", "aidax")]
    [InlineData("keras", "aidax")]
    [InlineData("wav", "ir")]
    [InlineData("impulse", "ir")]
    [InlineData("bogus", "")]
    [InlineData("", "")]
    public void NormalizeFormat_CanonicalizesSpellings(string input, string expected) => Assert.Equal(expected, NamCaptureIndex.NormalizeFormat(input));

    [Fact]
    public void BuildFileName_NeverEmitsPathOrInvalidChars()
    {
        var name = NamCaptureIndex.BuildFileName(
            7, "  ..\\..\\ドット / <chart>: \"tone\"|?* ", "STANDARD", "NAM");

        Assert.DoesNotContain("..", name);
        Assert.Equal(name, Path.GetFileName(name)); // no separators survive
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.EndsWith(".nam", name);
    }

    [Fact]
    public void BuildFileName_CapsLongTitles()
    {
        var name = NamCaptureIndex.BuildFileName(7, new string('a', 500), "standard", "nam");
        Assert.True(name.Length < 100);
    }

    [Fact]
    public void ManifestFileName_AppendsSuffix() => Assert.Equal("9-x.nam.t3k.json", NamCaptureIndex.ManifestFileName("9-x.nam"));
}
