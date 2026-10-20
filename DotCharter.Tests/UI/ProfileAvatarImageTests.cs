using Avalonia;
using DotCharter.UI.ViewModels.Application;

namespace DotCharter.Tests.UI;

/// <summary>
///     Player-profile avatars are a file the USER picked, and both ends of that
///     were unbounded: <c>PlayerProfileService.SetAvatar</c> is a plain
///     <c>File.Copy</c> with no resize (Services owns no image dependency), and
///     the view models decoded the result with <c>new Bitmap(stream)</c>. A 12 MP
///     phone photo is 80-250 ms and ~48 MB of RGBA <b>retained per profile</b>,
///     for a circle that is at most 20 px across.
///     <para>
///         The pixel work itself needs a Skia platform, which the default gate
///         deliberately does not boot (initializing one here would also weaken
///         the headless-platform guards, whose whole point is that no
///         platform is present). So the POLICY — the half that decides how big
///         anything gets — is a pure function tested directly, and the wiring is a
///         source lint, exactly like <c>StartupWorkDeferralTests</c>.
///     </para>
/// </summary>
public sealed class ProfileAvatarImageTests
{
    [Fact]
    public void ADisplaySizedDecodeIsSmallerThanEveryCircleThatShowsIt()
    {
        // The biggest avatar circle in the app is 20 px (Play Mode front door and
        // the switcher flyout); the status bar's is 14 px. The decode width has to
        // cover those with room for DPI + the global UI scale, and still be a tiny
        // fraction of the stored picture.
        Assert.True(ProfileAvatarImage.DecodeWidth >= 20 * 4);
        Assert.True(ProfileAvatarImage.DecodeWidth <= ProfileAvatarImage.StoredWidth);
    }

    [Theory]
    [InlineData(4032, 256)] // 12 MP phone photo
    [InlineData(1920, 256)]
    [InlineData(257, 256)]
    public void AnOversizedPictureIsDownscaledOnImport(int sourceWidth, int expected)
        => Assert.Equal(expected, ProfileAvatarImage.StoredWidthFor(sourceWidth));

    [Theory]
    [InlineData(256)]
    [InlineData(64)]
    [InlineData(1)]
    public void AnAlreadySmallPictureIsStoredVerbatim(int sourceWidth) =>
        // Never upscale: a re-encode that only adds pixels costs quality and bytes
        // and buys nothing, and storing verbatim also keeps the source's format.
        Assert.Null(ProfileAvatarImage.StoredWidthFor(sourceWidth));

    [Theory]
    [InlineData(4000, 3000, 256, 192)] // 4:3
    [InlineData(1000, 1000, 256, 256)] // square
    [InlineData(800, 2400, 256, 768)]  // tall portrait keeps its ratio, never clipped to a square
    public void TheDownscaleKeepsTheAspectRatio(int w, int h, int target, int expected)
        => Assert.Equal(expected, ProfileAvatarImage.ScaledHeightFor(new PixelSize(w, h), target));

    [Fact]
    public void ADegeneratePixelSizeNeverProducesAZeroHeightBitmap()
    {
        Assert.Equal(1, ProfileAvatarImage.ScaledHeightFor(new PixelSize(0, 0), 256));
        Assert.Equal(1, ProfileAvatarImage.ScaledHeightFor(new PixelSize(4000, 1), 256));
    }

    [Fact]
    public void NoAvatarSurfaceDecodesAtFullResolution()
    {
        // Source lint — see the class summary for why the decode itself cannot be
        // exercised in the default gate.
        var statusBar = ReadSource("DotCharter.UI", "ViewModels", "Application", "StatusBarViewModel.cs");
        Assert.DoesNotContain("new Bitmap(stream)", statusBar, StringComparison.Ordinal);
        Assert.Contains("ProfileAvatarImage.Load(", statusBar, StringComparison.Ordinal);

        // …and the one decode site really is width-limited.
        var helper = ReadSource("DotCharter.UI", "ViewModels", "Application", "ProfileAvatarImage.cs");
        Assert.Contains("Bitmap.DecodeToWidth(stream, DecodeWidth", helper, StringComparison.Ordinal);

        // The import path resizes before handing the file to the (image-free)
        // profile service, and cleans its temp file up.
        Assert.Contains("ProfileAvatarImage.PrepareForImport(path, _artwork)", statusBar, StringComparison.Ordinal);
        Assert.Contains("_playerProfiles.SetAvatar(_playerProfiles.ActiveProfile.Id, prepared)", statusBar,
            StringComparison.Ordinal);
    }

    // ---- the HEIGHT bound (2026-08-29) ------------------------------------------

    /// <summary>
    ///     <c>DecodeToWidth</c> bounds one axis. A 1 × 100,000 PNG is a few
    ///     hundred bytes — inside every byte cap on the LAN wire — and decodes at
    ///     width 96 to 96 × 9,600,000, ~3.7 GB of RGBA. The policy is pure: what
    ///     would this source decode TO, and is that under the ceiling?
    /// </summary>
    [Theory]
    [InlineData(64, 64, true)]
    [InlineData(4000, 3000, true)] // a landscape photo: 96 × 72
    [InlineData(1000, 4000, true)] // exactly 4:1 portrait: 96 × 384
    [InlineData(1000, 4001, false)] // just past it
    [InlineData(1, 100_000, false)] // the strip
    [InlineData(50, 400, false)] // narrower than the decode width: decodes at its own height
    [InlineData(50, 384, true)]
    [InlineData(0, 10, false)]
    [InlineData(10, 0, false)]
    public void TheDecodedHeightIsBoundedByPolicy(int w, int h, bool decodable)
        => Assert.Equal(decodable, ProfileAvatarImage.IsDecodableSize(w, h));

    [Fact]
    public void TheGuardReadsTheHeaderBeforeAnyDecoderIsAsked()
    {
        using (var tall = new MemoryStream(TinyPng.Encode(1, 100_000)))
            Assert.False(ProfileAvatarImage.DecodeGuard(tall));

        using (var fine = new MemoryStream(TinyPng.Encode(256, 256)))
        {
            Assert.True(ProfileAvatarImage.DecodeGuard(fine));
            Assert.Equal(0, fine.Position); // rewound: the decoder reads from the start
        }

        // A header the probe cannot read is NOT refused here — a user's small
        // avatar in an exotic format still loads as it always did — and Load
        // bounds the RESULT instead (see the lint below).
        using var unknown = new MemoryStream("definitely not a raster"u8.ToArray());
        Assert.True(ProfileAvatarImage.DecodeGuard(unknown));
    }

    /// <summary>The decode site really is behind both halves: the guard runs
    /// before <c>DecodeToWidth</c>, and what comes back is checked against
    /// <c>MaxDecodeHeight</c> for the formats the guard let through unread.</summary>
    [Fact]
    public void LoadConsultsTheGuardBeforeDecoding_AndBoundsWhatComesBack()
    {
        var helper = ReadSource("DotCharter.UI", "ViewModels", "Application", "ProfileAvatarImage.cs");
        var load = helper.IndexOf("public static Bitmap? Load(", StringComparison.Ordinal);
        Assert.True(load >= 0);
        var body = helper[load..];
        var guard = body.IndexOf("if (!DecodeGuard(stream)) return null;", StringComparison.Ordinal);
        var decode = body.IndexOf("Bitmap.DecodeToWidth(stream, DecodeWidth", StringComparison.Ordinal);
        var bound = body.IndexOf("bitmap.PixelSize.Height > MaxDecodeHeight", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Load no longer consults DecodeGuard");
        Assert.True(decode > guard, "the guard has to run BEFORE the decoder is asked");
        Assert.True(bound > decode, "the post-decode height bound is gone");
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
