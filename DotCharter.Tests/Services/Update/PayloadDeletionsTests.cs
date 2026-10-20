using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     Every surviving line becomes a <c>del</c> argument in a generated batch
///     script that runs over the install directory. The list arrives inside a
///     downloaded archive, so it is parsed as untrusted input.
/// </summary>
public sealed class PayloadDeletionsTests
{
    [Fact]
    public void AnOrdinaryRelativePathSurvives()
    {
        Assert.Equal(["runtimes/win-x64/native/old.dll"],
            PayloadDeletions.Parse("runtimes/win-x64/native/old.dll"));
    }

    [Fact]
    public void BackslashesAreNormalisedAndBlankLinesAndCommentsIgnored()
    {
        var parsed = PayloadDeletions.Parse("# a comment\r\n\r\n  runtimes\\old.dll  \r\n");

        Assert.Equal(["runtimes/old.dll"], parsed);
    }

    /// <summary>
    ///     The install could not start afterwards, and no later update could
    ///     repair it — the updater needs the exe it just deleted.
    /// </summary>
    [Theory]
    [InlineData("DotCharter.exe")]
    [InlineData("DotCharter.runtimeconfig.json")]
    [InlineData("DotCharter.deps.json")]
    [InlineData("hostfxr.dll")]
    [InlineData("coreclr.dll")]
    [InlineData("subdir/DotCharter.exe")]
    public void ALayoutCriticalFileIsNeverDeleted(string path) => Assert.Empty(PayloadDeletions.Parse(path));

    [Theory]
    [InlineData("../../Windows/System32/evil.dll")]  // walks out of the install
    [InlineData("a/../../b.dll")]
    [InlineData("C:/Windows/System32/evil.dll")]     // rooted
    [InlineData("/etc/passwd")]
    [InlineData("./x.dll")]
    public void APathThatLeavesThePayloadIsDropped(string path) => Assert.Empty(PayloadDeletions.Parse(path));

    /// <summary>
    ///     cmd.exe would re-interpret these inside the quoted argument — a
    ///     wildcard alone turns one deletion into an unbounded one.
    /// </summary>
    [Theory]
    [InlineData("*.dll")]
    [InlineData("a?.dll")]
    [InlineData("%APPDATA%/x.dll")]
    [InlineData("a\" & del /S /Q C:\\ & rem .dll")]
    [InlineData("a|b.dll")]
    [InlineData("a>b.dll")]
    [InlineData("a^b.dll")]
    public void ShellMetacharactersAreDropped(string path) => Assert.Empty(PayloadDeletions.Parse(path));

    /// <summary>The generated script must stay plain ASCII for cmd's codepage.</summary>
    [Fact]
    public void NonAsciiIsDropped() => Assert.Empty(PayloadDeletions.Parse("resources/Ünicode.dll"));

    /// <summary>
    ///     NTFS 8.3 aliases defeated the layout-critical guard: <c>DOTCHA~1.EXE</c>
    ///     is pure ASCII, has no traversal segment and matches no
    ///     <see cref="PayloadManifest.AlwaysInclude" /> entry — yet
    ///     <c>del "…\DOTCHA~1.EXE"</c> deletes <c>DotCharter.exe</c>, verified
    ///     against real cmd.exe. That is the unbootable, unrepairable install this
    ///     class exists to prevent. No legitimate payload path contains a tilde.
    /// </summary>
    [Theory]
    [InlineData("DOTCHA~1.EXE")]
    [InlineData("dotcha~1.exe")]
    [InlineData("runtimes/DOTCHA~2.JSO")]
    [InlineData("PROGRA~1")]
    public void An83ShortNameAliasIsDropped(string path) => Assert.Empty(PayloadDeletions.Parse(path));

    [Fact]
    public void DuplicatesCollapseCaseInsensitively() => Assert.Equal(["a/b.dll"], PayloadDeletions.Parse("a/b.dll\nA/B.DLL"));

    [Fact]
    public void WriteRoundTripsThroughParse()
    {
        var text = PayloadDeletions.Write(["a/b.dll", "DotCharter.exe", "c.dll"]);

        // The exe is filtered on the way OUT as well as on the way in.
        Assert.Equal(["a/b.dll", "c.dll"], PayloadDeletions.Parse(text));
        Assert.StartsWith("#", text);
    }

    [Fact]
    public void ParseIsEmptyForNothing()
    {
        Assert.Empty(PayloadDeletions.Parse(null));
        Assert.Empty(PayloadDeletions.Parse("   "));
    }

    /// <summary>
    ///     Reading the list must also REMOVE it, or the robocopy that follows
    ///     plants it in the install where the next patch would find it again.
    /// </summary>
    [Fact]
    public void TakeFromReadsThenRemovesTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dc-del-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var listPath = Path.Combine(dir, PayloadDeletions.FileName);
            File.WriteAllText(listPath, "old/thing.dll\n");

            Assert.Equal(["old/thing.dll"], PayloadDeletions.TakeFrom(dir));
            Assert.False(File.Exists(listPath));
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    [Fact]
    public void TakeFromIsEmptyWhenThePayloadCarriesNoList()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dc-del-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            Assert.Empty(PayloadDeletions.TakeFrom(dir));
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    /// <summary>
    ///     Every surviving line becomes a <c>del</c> in a batch script that runs
    ///     over the install directory, and this list arrives inside an archive an
    ///     unauthenticated peer streamed at us. A million legal-looking relative
    ///     paths is a million-line cmd file, so the parse is capped.
    /// </summary>
    [Fact]
    public void AnAbsurdlyLongListIsCappedRatherThanParsedWhole()
    {
        var text = string.Join('\n',
            Enumerable.Range(0, PayloadDeletions.MaxEntries + 500).Select(i => $"junk/file{i}.dll"));

        Assert.Equal(PayloadDeletions.MaxEntries, PayloadDeletions.Parse(text).Count);
    }

    /// <summary>
    ///     …and the READ is bounded too. <c>TakeFrom</c> used
    ///     <c>File.ReadAllText</c>, which is unbounded, on a file the extractor's
    ///     byte budget had already been spent writing to disk. Truncation is the
    ///     right answer for a list this size: the cut mangles one line and
    ///     <c>Parse</c> drops it.
    /// </summary>
    [Fact]
    public void TakeFromReadsAtMostTheBoundedPrefix()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dc-del-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            // One legal entry, then far more than MaxListBytes of padding. If the
            // read were unbounded the padding would parse into entries too.
            var padding = string.Join('\n',
                Enumerable.Range(0, 200_000).Select(i => $"pad/{i:D12}.dll"));
            File.WriteAllText(Path.Combine(dir, PayloadDeletions.FileName), "old.dll\n" + padding);
            Assert.True(new FileInfo(Path.Combine(dir, PayloadDeletions.FileName)).Length >
                        PayloadDeletions.MaxListBytes);

            var kept = PayloadDeletions.TakeFrom(dir);

            Assert.Contains("old.dll", kept);
            Assert.True(kept.Count <= PayloadDeletions.MaxEntries, $"kept {kept.Count}");
            // The bound is on BYTES, so what survived cannot be the whole file.
            Assert.True(kept.Count < 200_000, $"the whole list was read: {kept.Count} entries");
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }
}
