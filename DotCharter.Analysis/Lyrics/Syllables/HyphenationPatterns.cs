using System.IO.Compression;
using System.Text;

namespace DotCharter.Analysis.Lyrics.Syllables;

/// <summary>
///     Liang/TeX hyphenation patterns read from a LibreOffice <c>hyph_*.dic</c> file, the
///     format all four shipped languages use. Reads the charset line, skips comments and
///     keyword lines, and keeps only the plain patterns (a pattern with a non-standard
///     <c>/</c> replacement is skipped). A <c>NEXTLEVEL</c> line starts a second pass whose
///     patterns replace the first; the French file uses it only to tag apostrophes.
///     <para>
///         The result is the set of typographic break points of one word. They are a starting
///         point for a singer's syllables, not the answer: <see cref="LyricSyllabifier" />
///         applies the vowel-nucleus rules on top.
///     </para>
/// </summary>
internal sealed class HyphenationPatterns
{
    private readonly Dictionary<string, byte[]> _patterns;
    private readonly int _longest;

    private HyphenationPatterns(Dictionary<string, byte[]> patterns)
    {
        _patterns = patterns;
        _longest = patterns.Count == 0 ? 0 : patterns.Keys.Max(k => k.Length);
    }

    /// <summary>Reads a <c>.dic</c> file's bytes (already decompressed).</summary>
    public static HyphenationPatterns Parse(byte[] dic)
    {
        var charsetEnd = Array.IndexOf(dic, (byte)'\n');
        var charset = Encoding.ASCII.GetString(dic, 0, Math.Max(charsetEnd, 0)).Trim();
        var encoding = charset.StartsWith("UTF", StringComparison.OrdinalIgnoreCase)
            ? new UTF8Encoding(false)
            : Encoding.Latin1;
        var text = encoding.GetString(dic, charsetEnd + 1, dic.Length - charsetEnd - 1);

        var patterns = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0 || line[0] is '#' or '%') continue;
            if (line == "NEXTLEVEL")
            {
                patterns.Clear();
                continue;
            }

            if (line.Contains('/') || char.IsUpper(line[0]) && line.Contains(' ')) continue;
            AddPattern(patterns, line.ToLowerInvariant());
        }

        return new HyphenationPatterns(patterns);
    }

    /// <summary>Loads one of the embedded, gzip-compressed files.</summary>
    public static HyphenationPatterns LoadEmbedded(string fileName)
    {
        using var resource = typeof(HyphenationPatterns).Assembly
            .GetManifestResourceStream($"DotCharter.Analysis.Hyphenation.{fileName}")
            ?? throw new InvalidOperationException($"Missing hyphenation resource {fileName}");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        return Parse(memory.ToArray());
    }

    /// <summary>
    ///     Break points of <paramref name="word" />: index <c>i</c> means a break between
    ///     letters <c>i-1</c> and <c>i</c>. Always inside the word.
    /// </summary>
    public List<int> Breaks(string word)
    {
        var padded = "." + word.ToLowerInvariant() + ".";
        var values = new byte[padded.Length + 1];
        for (var start = 0; start < padded.Length; start++)
        {
            var maxLength = Math.Min(_longest, padded.Length - start);
            for (var length = 1; length <= maxLength; length++)
            {
                if (!_patterns.TryGetValue(padded.Substring(start, length), out var pattern)) continue;
                for (var i = 0; i < pattern.Length; i++)
                    if (pattern[i] > values[start + i]) values[start + i] = pattern[i];
            }
        }

        // values[k] sits before padded[k]; padded[1] is word[0].
        var breaks = new List<int>();
        for (var i = 1; i < word.Length; i++)
            if (values[i + 1] % 2 == 1) breaks.Add(i);
        return breaks;
    }

    private static void AddPattern(Dictionary<string, byte[]> patterns, string line)
    {
        var letters = new StringBuilder();
        var values = new List<byte> { 0 };
        foreach (var c in line)
        {
            if (c is >= '0' and <= '9')
            {
                values[^1] = (byte)(c - '0');
                continue;
            }

            letters.Append(c);
            values.Add(0);
        }

        if (letters.Length > 0) patterns[letters.ToString()] = [.. values];
    }
}
