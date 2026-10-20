using DotCharter.Services.Gameplay.Profiles;

namespace DotCharter.Services.Infrastructure.Storage;

public enum ArtworkKind
{
    /// <summary>An image file by path: a song's extracted cover, a picked picture.</summary>
    File,

    /// <summary>A player profile's stored avatar, by profile id.</summary>
    ProfileAvatar
}

/// <summary>What a view model names instead of holding a bitmap: which artwork,
/// not its pixels.</summary>
public readonly record struct ArtworkKey(ArtworkKind Kind, string Id)
{
    public static ArtworkKey File(string path) => new(ArtworkKind.File, path);

    public static ArtworkKey ProfileAvatar(Guid profileId) => new(ArtworkKind.ProfileAvatar, profileId.ToString("D"));
}

/// <summary>
///     The bytes behind cover art and avatars, by key. The UI decodes what it
///     opens here into bitmaps (<c>SongCoverImage</c>, <c>ProfileAvatarImage</c>);
///     Services stays free of an image dependency. Also stages the temporary
///     file an avatar import re-encodes into before the profile service copies it.
/// </summary>
public interface IArtworkSource
{
    bool Exists(ArtworkKey key);

    /// <summary>Opens the image for reading, or null when there is none.</summary>
    Stream? Open(ArtworkKey key);

    /// <summary>Creates an empty temporary file for a re-encoded image;
    /// <paramref name="path" /> is where it lives until <see cref="DeleteStaged" />.</summary>
    Stream CreateStaged(string extension, out string path);

    /// <summary>Deletes a file <see cref="CreateStaged" /> made. Never throws.</summary>
    void DeleteStaged(string path);
}

/// <inheritdoc />
public sealed class ArtworkSource(IPlayerProfileService? profiles = null) : IArtworkSource
{
    public bool Exists(ArtworkKey key) => Resolve(key) is { } path && System.IO.File.Exists(path);

    public Stream? Open(ArtworkKey key) =>
        Resolve(key) is { } path && System.IO.File.Exists(path) ? System.IO.File.OpenRead(path) : null;

    public Stream CreateStaged(string extension, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"dotcharter-avatar-{Guid.NewGuid():N}{extension}");
        return System.IO.File.Create(path);
    }

    public void DeleteStaged(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup: the OS temp folder is swept anyway.
        }
    }

    private string? Resolve(ArtworkKey key) => key.Kind switch
    {
        ArtworkKind.File => string.IsNullOrWhiteSpace(key.Id) ? null : key.Id,
        ArtworkKind.ProfileAvatar => Guid.TryParse(key.Id, out var id) ? profiles?.GetAvatarPath(id) : null,
        _ => null
    };
}
