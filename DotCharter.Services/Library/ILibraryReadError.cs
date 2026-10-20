namespace DotCharter.Services.Library;

/// <summary>
///     A failure to read a library song that carries its own user-facing message: the
///     resx key the song browser shows instead of the exception's (English, neutral)
///     text. <see cref="Package.DcSong.DcSongReadException" /> is one; a package source
///     from a plugin throws its own.
/// </summary>
public interface ILibraryReadError
{
    /// <summary>The resx key of the user-facing message.</summary>
    string ResourceKey { get; }
}
