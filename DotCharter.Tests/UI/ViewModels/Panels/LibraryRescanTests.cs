using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     <see cref="LibraryRescan" />, the merge behind the Library's Refresh (#186). Its
///     <c>SameEntry</c> is a hand-written comparer over every field of a scanned song, so it is
///     guarded by REFLECTION: a field added to <see cref="LibrarySongEntry" /> or
///     <see cref="LibraryArrangementEntry" /> and not compared would keep a stale row (and load the
///     old arrangement list) after the archive changed.
/// </summary>
public sealed class LibraryRescanTests
{
    private static readonly FieldExclusion[] Exclusions =
    [
        new(typeof(LibrarySongEntry), nameof(LibrarySongEntry.CoverImagePath),
            "where a decoded cover was saved on disk, not what the archive holds: comparing it would report a song as changed " +
            "because it was looked at, and release the cover it just decoded")
    ];

    public static TheoryData<string> SongFields => [.. Fields(typeof(LibrarySongEntry))];

    public static TheoryData<string> ArrangementFields => [.. Fields(typeof(LibraryArrangementEntry))];

    [Fact]
    public void TwoEntriesWithTheSameValues_AreTheSameSong() =>
        Assert.True(LibraryRescan.SameEntry(DomainFieldProbe.Populated<LibrarySongEntry>(), DomainFieldProbe.Populated<LibrarySongEntry>()));

    [Theory]
    [MemberData(nameof(SongFields))]
    public void EveryFieldOfASong_IsCompared(string field)
    {
        var changed = DomainFieldProbe.Populated<LibrarySongEntry>();
        var property = typeof(LibrarySongEntry).GetProperty(field)!;
        property.SetValue(changed, DomainFieldProbe.Alternate(property.PropertyType));

        Assert.False(LibraryRescan.SameEntry(DomainFieldProbe.Populated<LibrarySongEntry>(), changed), $"a change to {field} reads as the same song");
    }

    [Theory]
    [MemberData(nameof(ArrangementFields))]
    public void EveryFieldOfAnArrangement_IsCompared(string field)
    {
        var arrangement = DomainFieldProbe.Populated<LibraryArrangementEntry>();
        var property = typeof(LibraryArrangementEntry).GetProperty(field)!;
        property.SetValue(arrangement, DomainFieldProbe.Alternate(property.PropertyType));
        var changed = DomainFieldProbe.Populated<LibrarySongEntry>();
        typeof(LibrarySongEntry).GetProperty(nameof(LibrarySongEntry.Arrangements))!.SetValue(changed, new List<LibraryArrangementEntry> { arrangement });

        Assert.False(LibraryRescan.SameEntry(DomainFieldProbe.Populated<LibrarySongEntry>(), changed), $"a change to the arrangement's {field} reads as the same song");
    }

    [Fact]
    public void EveryExclusion_NamesAPropertyThatExists() =>
        Assert.All(Exclusions, e => Assert.NotNull(e.Owner.GetProperty(e.Property)));

    /// <summary>An unchanged package whose playability changed (ffmpeg installed or removed since the
    /// last scan, #708) is a NEW row: the kept one would stay disabled, or look live, for good.</summary>
    [Fact]
    public void AnUnchangedPackage_WhosePlayabilityChanged_GetsANewRow()
    {
        static LibrarySongEntry Package() => new()
        {
            SongKey = "k", Title = "t", PackagePath = @"C:\lib\k.dcsong", SongBank = "audio/song.opus",
            Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "chart.rscp" }]
        };
        var loc = FakeLocalization.English();
        var blocked = new PsarcSongListItem(Package(), loc, LibrarySongBlock.AudioDecoderMissing);

        var nowPlayable = LibraryRescan.Merge([blocked], [Package()], loc, _ => LibrarySongBlock.None);
        var stillBlocked = LibraryRescan.Merge([blocked], [Package()], loc, _ => LibrarySongBlock.AudioDecoderMissing);

        var fresh = Assert.Single(nowPlayable.Songs);
        Assert.NotSame(blocked, fresh);
        Assert.True(fresh.CanLoad);
        Assert.False(blocked.CanLoad);
        Assert.Same(blocked, Assert.Single(stillBlocked.Songs));
        Assert.True(stillBlocked.IsUnchanged);
    }

    /// <summary>Two listed songs under one key (an archive with two manifests for one song key) are
    /// two songs: a rescan that finds both again keeps both and reports nothing.</summary>
    [Fact]
    public void ASharedKey_IsMatchedOneToOne()
    {
        static LibrarySongEntry Twin() => new() { SongKey = "k", Title = "t", PackagePath = @"C:\lib\k_p.psarc" };
        var listed = new[] { new PsarcSongListItem(Twin(), FakeLocalization.English()), new PsarcSongListItem(Twin(), FakeLocalization.English()) };

        var merge = LibraryRescan.Merge(listed, [Twin(), Twin()], FakeLocalization.English());

        Assert.True(merge.IsUnchanged);
        Assert.Equal(listed, merge.Songs);
    }

    private static IEnumerable<string> Fields(Type type) =>
        DomainFieldProbe.SettableProperties(type)
            .Where(p => !Exclusions.Any(e => e.Owner == type && e.Property == p.Name))
            .Select(p => p.Name);
}
