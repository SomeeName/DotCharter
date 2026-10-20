using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Export;

/// <summary>
/// The Rocksmith DLC key rules. The key and every name derived from it (sng,
///     manifests, xblock, bank and wem names, aggregate graph, showlights, album art) must be
///     ASCII <c>[A-Za-z0-9]</c>: the game does not list a package whose key carries kana, kanji
///     or any other non-ASCII letter, and CDLC toolkits enforce the same set.
///     <c>char.IsLetterOrDigit</c> is true for every Unicode letter, so it must never decide a
///     key character.
/// </summary>
public static class RocksmithDlcKey
{
    /// <summary>The game wants a key of at least this many characters.</summary>
    public const int MinimumLength = 6;

    private const int ArtistChars = 10;
    private const int TitleChars = 12;

    /// <summary>Letters that canonical/compatibility decomposition does not split into an
    /// ASCII base, spelled the way a Latin reader would.</summary>
    private static readonly Dictionary<char, string> LatinLigatures = new()
    {
        ['ß'] = "ss", ['æ'] = "ae", ['Æ'] = "AE", ['œ'] = "oe", ['Œ'] = "OE", ['ø'] = "o", ['Ø'] = "O",
        ['ł'] = "l", ['Ł'] = "L", ['đ'] = "d", ['Đ'] = "D", ['ð'] = "d", ['Ð'] = "D", ['þ'] = "th",
        ['Þ'] = "Th", ['ı'] = "i"
    };

    /// <summary>
    ///     The default key: <c>DC</c>, up to 10 ASCII characters of the artist and 12 of the
    ///     title, Latin diacritics folded ("Beyoncé" → "Beyonce"). When a letter or digit had to
    ///     be DROPPED (it has no ASCII spelling, e.g. Japanese), or the key would be shorter than
    ///     <see cref="MinimumLength" />, an 8-hex-digit hash of the ORIGINAL artist and title is
    ///     appended — stable across runs and machines, and different for two titles that fold to
    ///     the same ASCII. An all-ASCII artist and title produce the same plain key as always.
    /// </summary>
    public static string Create(string? artist, string? title)
    {
        var (artistAscii, artistLost) = Fold(artist);
        var (titleAscii, titleLost) = Fold(title);
        var key = "DC" + Truncate(artistAscii, ArtistChars) + Truncate(titleAscii, TitleChars);
        return artistLost || titleLost || key.Length < MinimumLength ? key + StableHash(artist, title) : key;
    }

    /// <summary>
    ///     The ASCII letters and digits <paramref name="value" /> spells, diacritics and
    ///     compatibility forms folded (full-width letters, ligatures), plus whether any letter or
    ///     digit had no ASCII spelling and was dropped.
    /// </summary>
    public static (string Ascii, bool DroppedLetters) Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ("", false);

        var builder = new StringBuilder(value.Length);
        var dropped = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                if (char.IsAsciiLetterOrDigit((char)rune.Value)) builder.Append((char)rune.Value);
                continue;
            }

            if (!Rune.IsLetterOrDigit(rune)) continue;
            if (rune.IsBmp && LatinLigatures.TryGetValue((char)rune.Value, out var spelled))
            {
                builder.Append(spelled);
                continue;
            }

            var folded = rune.ToString().Normalize(NormalizationForm.FormKD)
                .Where(char.IsAsciiLetterOrDigit)
                .ToArray();
            if (folded.Length == 0)
                dropped = true;
            else
                builder.Append(folded);
        }

        return (builder.ToString(), dropped);
    }

    /// <summary>True when <paramref name="key" /> holds any character outside ASCII — a typed
    /// key the build refuses rather than silently rewrites.</summary>
    public static bool HasNonAscii(string? key) => key is not null && key.Any(c => c > '\u007F');

    /// <summary>
    ///     Whether a key the charter TYPED is unusable: non-blank and either carrying a non-ASCII
    ///     character or fewer than <see cref="MinimumLength" /> ASCII letters and digits. ASCII
    ///     punctuation and spaces are still allowed and stripped by the build.
    /// </summary>
    public static bool IsInvalidTypedKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && (HasNonAscii(key) || key.Count(char.IsAsciiLetterOrDigit) < MinimumLength);

    /// <summary>
    ///     Whether a DISPLAY string (song, artist or album name) holds a character the game's
    ///     font most likely lacks. The game's song list renders Latin script; anything past
    ///     Latin Extended-B that is not common punctuation (CJK, kana, Hangul, Cyrillic, emoji…)
    ///     is reported so the charter can type an English or romanised name. Never rewritten.
    /// </summary>
    public static bool LacksGameGlyphs(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var rune in value.EnumerateRunes())
        {
            var code = rune.Value;
            if (code <= 0x024F) continue; // Basic Latin … Latin Extended-B
            if (code is >= 0x2000 and <= 0x206F) continue; // General Punctuation (dashes, quotes, …)
            if (code is >= 0x20A0 and <= 0x20CF) continue; // Currency symbols (€)
            if (code == 0x2122) continue; // ™
            return true;
        }

        return false;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string StableHash(string? artist, string? title)
    {
        var source = Encoding.UTF8.GetBytes((artist ?? "").Trim() + "\u001F" + (title ?? "").Trim());
        var hash = SHA256.HashData(source);
        return Convert.ToHexString(hash, 0, 4);
    }
}
