using DotCharter.Services.Gameplay.Profiles;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>The guest fast path's pure naming rules (v0.31 leg 3).</summary>
public sealed class GuestSessionNamingTests
{
    [Theory]
    [InlineData("  Alex  ", "Alex")]
    [InlineData("", "Guest")]
    [InlineData("   ", "Guest")]
    [InlineData(null, "Guest")]
    public void Resolve_TrimsAndFallsBack(string? requested, string expected)
        => Assert.Equal(expected, GuestSessionNaming.Resolve(requested));

    [Fact]
    public void Resolve_CapsOverlongNames()
    {
        var resolved = GuestSessionNaming.Resolve(new string('x', 100));
        Assert.Equal(GuestSessionNaming.MaxLength, resolved.Length);
    }

    [Fact]
    public void Resolve_DeduplicatesAgainstExistingProfiles_CaseInsensitive()
    {
        var existing = new[] { "alex", "Alex 2", "Sam" };
        Assert.Equal("Alex 3", GuestSessionNaming.Resolve("Alex", existing));
        Assert.Equal("Kim", GuestSessionNaming.Resolve("Kim", existing));
        Assert.Equal("Guest", GuestSessionNaming.Resolve("", new[] { "Player 1" }));
    }
}
