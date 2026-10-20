using DotCharter.Services.Library;

namespace DotCharter.Tests.Services.Library;

/// <summary>
///     #891: the public library names no rule for telling stock from custom entries. Without an
///     plugin's classifier every entry is <see cref="LibraryOrigin.Unknown" />; a registered one decides.
/// </summary>
public sealed class LibraryOriginsTests
{
    private sealed class Fixed(LibraryOrigin answer) : ILibraryOriginClassifier
    {
        public LibraryOrigin Classify(LibrarySongEntry song) => answer;
    }

    [Fact]
    public void Without_a_classifier_every_entry_is_unknown()
    {
        var entry = new LibrarySongEntry { Charter = "someone", Arrangements = [new() { MasterId = 32701 }] };

        Assert.Equal(LibraryOrigin.Unknown, entry.Origin);
    }

    [Fact]
    public void A_registered_classifier_decides_and_the_scope_ends_with_the_test()
    {
        using (LibraryOrigins.UseForTesting(new Fixed(LibraryOrigin.Stock)))
            Assert.Equal(LibraryOrigin.Stock, new LibrarySongEntry().Origin);

        Assert.Equal(LibraryOrigin.Unknown, new LibrarySongEntry().Origin);
    }
}
