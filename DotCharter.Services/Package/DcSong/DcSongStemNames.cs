using System.Text;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     A project's stem names as package member names (<c>[a-z0-9_-]{1,32}</c>,
///     <see cref="DcSongFormat.IsValidStemName" />): lower-cased, every other character a
///     dash, cut to 32, and a repeat numbered. A name that is already valid is kept, so
///     the packaged chart keeps that stem's mixer state.
/// </summary>
public static class DcSongStemNames
{
    private const int MaxLength = 32;

    public static IReadOnlyList<string> ForPackage(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);
        foreach (var name in names)
        {
            var baseName = Clean(name);
            var candidate = baseName;
            for (var n = 2; !used.Add(candidate); n++)
            {
                var suffix = "-" + n;
                candidate = baseName[..Math.Min(baseName.Length, MaxLength - suffix.Length)] + suffix;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static string Clean(string? name)
    {
        var builder = new StringBuilder();
        foreach (var c in (name ?? "").Trim().ToLowerInvariant())
        {
            if (builder.Length == MaxLength) break;
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' ? c : '-');
        }

        var cleaned = builder.ToString().Trim('-');
        return cleaned.Length == 0 ? "stem" : cleaned;
    }
}
