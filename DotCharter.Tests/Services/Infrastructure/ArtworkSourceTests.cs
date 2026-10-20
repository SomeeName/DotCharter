using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>The bytes behind cover art and avatars: what a key resolves to, and
/// the temporary file an avatar import is re-encoded into.</summary>
public sealed class ArtworkSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-artwork-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void AFileKey_OpensTheFile()
    {
        var path = Path.Combine(_dir, "cover.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        var artwork = new ArtworkSource();

        Assert.True(artwork.Exists(ArtworkKey.File(path)));
        using var stream = artwork.Open(ArtworkKey.File(path));
        Assert.NotNull(stream);
        Assert.Equal(3, stream!.Length);
    }

    [Fact]
    public void AMissingFileOrAnEmptyKey_OpensNothing()
    {
        var artwork = new ArtworkSource();

        Assert.False(artwork.Exists(ArtworkKey.File(Path.Combine(_dir, "missing.png"))));
        Assert.Null(artwork.Open(ArtworkKey.File(Path.Combine(_dir, "missing.png"))));
        Assert.Null(artwork.Open(ArtworkKey.File("")));
    }

    [Fact]
    public void AnAvatarKey_WithoutAProfileService_OpensNothing()
        => Assert.Null(new ArtworkSource().Open(ArtworkKey.ProfileAvatar(Guid.NewGuid())));

    [Fact]
    public void AStagedFile_IsWritable_AndDeletedAfterwards()
    {
        var artwork = new ArtworkSource();

        string path;
        using (var stream = artwork.CreateStaged(".png", out path)) stream.WriteByte(7);
        Assert.Equal(".png", Path.GetExtension(path));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(path));

        artwork.DeleteStaged(path);
        Assert.False(File.Exists(path));
        artwork.DeleteStaged(path); // twice: never throws
    }
}
