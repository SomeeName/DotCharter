using DotCharter.Services.Library;


namespace DotCharter.Tests.Services.Parsing.Psarc;

/// <summary>
///     A real library can hold byte-for-byte COPIES of a multi-song pack under
///     different file names, so the same song identity
///     (<c>SongKey|Charter|Version</c>) surfaces as two or three interchangeable
///     rows. <see cref="LibraryDedupe.CollapseByIdentity(IEnumerable{LibrarySongEntry})" />
///     folds those to one deterministic winner (smallest ordinal path) while
///     leaving genuinely distinct rows alone. LibraryDoctor keeps reporting the
///     duplicates; the lists collapse them.
/// </summary>
public sealed class LibraryDedupeTests
{
    private static LibrarySongEntry Entry(string songKey, string path,
        string charter = "", string version = "", string title = "") => new()
        {
            SongKey = songKey,
            Charter = charter,
            Version = version,
            Title = title,
            PackagePath = path,
        };

    [Fact]
    public void CollapsesTwoCopiesOfTheSameIdentity_KeepingSmallestPath()
    {
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("X", @"F:\dlc\copy_b.psarc"),
            Entry("X", @"F:\dlc\copy_a.psarc"),
        ]);

        Assert.Equal(@"F:\dlc\copy_a.psarc", Assert.Single(kept).PackagePath);
    }

    /// <summary>
    ///     A PC and a Mac package of one DLC share an identity but are NOT
    ///     interchangeable: the song loader reads PC SNGs only, so a Mac winner
    ///     fails to load ("unsupported compression method"). Plain smallest-path
    ///     picked <c>_m</c> over <c>_p</c> every time ('m' &lt; 'p') — the
    ///     owner's "Three Little Birds" failed exactly that way (2026-09-25).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APcPackageBeatsItsMacTwin_InEitherOrder(bool macFirst)
    {
        var pc = Entry("BobMThre", @"F:\dlc\bobmthre_p.psarc");
        var mac = Entry("BobMThre", @"F:\dlc\BOBMTHRE_M.PSARC");

        var kept = LibraryDedupe.CollapseByIdentity(macFirst ? [mac, pc] : [pc, mac]);

        Assert.Equal(@"F:\dlc\bobmthre_p.psarc", Assert.Single(kept).PackagePath);
    }

    /// <summary>A Mac package with no PC twin is still listed — dedupe only ranks copies.</summary>
    [Fact]
    public void AMacOnlyPackage_IsKept()
    {
        var kept = LibraryDedupe.CollapseByIdentity([Entry("Solo", @"F:\dlc\solo_m.psarc")]);

        Assert.Equal(@"F:\dlc\solo_m.psarc", Assert.Single(kept).PackagePath);
    }

    [Fact]
    public void WinnerIsDeterministic_RegardlessOfInputOrder()
    {
        var ascending = LibraryDedupe.CollapseByIdentity(
        [
            Entry("X", @"F:\a.psarc"),
            Entry("X", @"F:\b.psarc"),
            Entry("X", @"F:\c.psarc"),
        ]);
        var descending = LibraryDedupe.CollapseByIdentity(
        [
            Entry("X", @"F:\c.psarc"),
            Entry("X", @"F:\b.psarc"),
            Entry("X", @"F:\a.psarc"),
        ]);

        Assert.Equal(@"F:\a.psarc", Assert.Single(ascending).PackagePath);
        Assert.Equal(@"F:\a.psarc", Assert.Single(descending).PackagePath);
    }

    [Fact]
    public void DoesNotCollapse_WhenCharterDiffers()
    {
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("Song", @"F:\a.psarc", charter: "Alice"),
            Entry("Song", @"F:\b.psarc", charter: "Bob"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void DoesNotCollapse_WhenVersionDiffers()
    {
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("Song", @"F:\a.psarc", charter: "Alice", version: "1"),
            Entry("Song", @"F:\b.psarc", charter: "Alice", version: "2"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void DoesNotCollapse_WhenSongKeyDiffers()
    {
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("SongA", @"F:\a.psarc"),
            Entry("SongB", @"F:\b.psarc"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void CollapseIsCaseAndWhitespaceInsensitive_MirroringTheLeaderboardKey()
    {
        // Same logical identity, differing only in case + surrounding whitespace.
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("BobMThre", @"F:\a.psarc", charter: "Ace", version: "1"),
            Entry(" bobmthre ", @"F:\b.psarc", charter: " ACE ", version: " 1 "),
        ]);

        Assert.Equal(@"F:\a.psarc", Assert.Single(kept).PackagePath);
    }

    [Fact]
    public void MultiSongPack_AllSongsSurvive_TheyShareAPathButNotAnIdentity()
    {
        // One pack file, three distinct songs — none collapse.
        const string pack = @"F:\dlc\rs_compat_pack_p.psarc";
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("SongA", pack),
            Entry("SongB", pack),
            Entry("SongC", pack),
        ]);

        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void MisnamedPackCopy_CollapsesEachSongOnceAcrossBothFiles()
    {
        // The verified owner scenario: a byte copy of a 3-song pack under a
        // different name yields 6 rows that must collapse back to 3.
        const string original = @"F:\dlc\rs_compat_pack_p.psarc";
        const string copy = @"F:\dlc\my_backup_of_rs1.psarc";
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("SongA", original), Entry("SongB", original), Entry("SongC", original),
            Entry("SongA", copy), Entry("SongB", copy), Entry("SongC", copy),
        ]);

        Assert.Equal(3, kept.Count);
        // 'd' (dlc\my...) sorts before 'd' (dlc\rs1...) — 'm' < 'r', so the copy wins each.
        Assert.All(kept, e => Assert.Equal(copy, e.PackagePath));
    }

    [Fact]
    public void SameFileTwice_KeepsOne()
    {
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("X", @"F:\x.psarc"),
            Entry("X", @"F:\x.psarc"),
        ]);

        Assert.Equal(@"F:\x.psarc", Assert.Single(kept).PackagePath);
    }

    [Fact]
    public void BlankSongKeyRows_AreNeverCollapsed()
    {
        // Un-keyed rows cannot be identified — every one is kept, as the doctor does.
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("", @"F:\a.psarc"),
            Entry("", @"F:\b.psarc"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void PreservesFirstAppearanceOrderOfSurvivors()
    {
        var kept = LibraryDedupe.CollapseByIdentity(
        [
            Entry("First", @"F:\z.psarc"),
            Entry("Dup", @"F:\b.psarc"),
            Entry("Second", @"F:\y.psarc"),
            Entry("Dup", @"F:\a.psarc"), // collapses into the earlier "Dup" slot
        ]);

        Assert.Equal(["First", "Dup", "Second"], kept.Select(e => e.SongKey));
        // Even though the collapsed "Dup" keeps its position, the smaller path wins.
        Assert.Equal(@"F:\a.psarc", kept[1].PackagePath);
    }

    // ---- generic wrapper overload (the shape both list surfaces use) -------------

    private sealed record Wrapper(LibrarySongEntry Song);

    [Fact]
    public void GenericOverload_CollapsesWrappersByTheirEntryIdentity()
    {
        var kept = LibraryDedupe.CollapseByIdentity(
            new[]
            {
                new Wrapper(Entry("X", @"F:\p.psarc", title: "keep-me")),
                new Wrapper(Entry("X", @"F:\a.psarc", title: "winner")),
                new Wrapper(Entry("Y", @"F:\c.psarc")),
            },
            static w => w.Song);

        Assert.Equal(2, kept.Count);
        Assert.Equal("winner", kept[0].Song.Title); // smallest path won, in the X slot
        Assert.Equal("Y", kept[1].Song.SongKey);
    }
}
