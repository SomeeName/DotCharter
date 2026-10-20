using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The repository goes public without figures read out of Rocksmith's soundbanks
///     (#750, #790): the guides describe WHAT DotCharter reads from the player's install
///     and how, never the values, curve shapes or effect chains it finds there.
///     <see cref="RsGameDataLintTests" /> guards the code; this guards every Markdown
///     guide (and the HTML/JS under <c>docs/</c>) against the figures the guides used to
///     quote coming back, from an old branch, a revert or a copied paragraph.
///     The list holds only the SHA-256 of each normalized snippet and its token count —
///     never the figure itself, which would put the data back in the tree.
/// </summary>
public sealed partial class RsGameDocsLintTests
{
    /// <summary>
    ///     (tokens, SHA-256 hex of the tokens joined by one space) — see <see cref="Tokenize" />.
    ///     Every snippet starts at a token that holds a digit. To add one: normalize it the
    ///     same way OUTSIDE the repository, paste only the pair, and watch this lint fail on
    ///     a scratch guide that quotes it before deleting the scratch file.
    /// </summary>
    private static readonly (int Tokens, string Sha256)[] KnownGameFigures =
    [
        (3, "bc58b8117f584f9c80731f541ac2f94b772968ba7bdb44b2c44fa59a0bd06e06"),
        (3, "42cf25333d4fe021a763643e9c728a04d2f04fc2bd5b594bbce48a6ae98c0fa6"),
        (9, "a411c9718d690fa2256c87260aa963539a01e2bb28dc2c43adfd7990002a4de9"),
        (4, "a7dfc251285388773531b8fcba457c86478014ffda164765626ac11a9facabaf"),
        (5, "af8b79cb0c52d6bcda41e000d48a48f23c62cd29422666c41dbfc9e0e1bf2124"),
        (4, "1ab4e384d34e6707111ec25c2fd3d413fc126dfbc57c1bf130f68af9a3783078"),
        (3, "59869c97804885de68686a0e45c10edc7f0433bfdd2c94f4786ffdce315c287d"),
        (6, "6386dd20060a4bab2f475995d18d9ffdd988f5133ab1580ec8f2ea5e0813c996"),
        (3, "64e7573b2529f618bb9018400c600db6ea78a29b8b966ccaea075a2db015e31c"),
        (3, "2d4cad8091c6834b51736a57740c90a34c8b1fe6c3662d160c2c338de6f67d02"),
        (6, "a74d67bd1d35118c1d1abe7b147262464eede15f6b97137d55e71a1de06343ed"),
        (6, "92e54b65fb787a7b2644b97c2cefea9764f68e9e6ce2e09c7bf08927b661466b"),
        (1, "76a1bfe70008c1b0be1035f9d5c1654049b4090201a3688d8e834c9815a78924"),
        (1, "43d38c578df8956600509e9a94ce17432422cd26efa8adc3d807cc13ed0899b5"),
        (5, "8d1a5e497b5250e5cdf6ee6a40c7ae144b8575e41511c65b89e7cf1d091a7201"),
        (7, "0ad33b8a273ea948f0f6b3e18ac63c49da80d57bb27f8ad0480271deeec00273"),
        (4, "fb47471ef770b736b5f97b5fcc61b22fc90c7978a14477700fd8b02c5101fb6f"),
        (4, "b3edf6cad4473ad32e626fcbb1365ca620880cf82023b5f4bc6ecedbd0494fad"),
        (4, "90905dd980d8dcfd2464f152ec9c60f3df0011a29f3655e74ca1bf79ce876f2c"),
        (4, "bb7e2da6083c4ee899843709beb0a09a88f5bcb227e867e1f421923c956c78af"),
        (9, "cc2c45141d66c0ef6e2157ee2ed2b9ca254c30478e160b1476e772702cb5b73c"),
        (5, "ba4c3540c663d8a494fc4ca816a89e3c1125e0746e204d9ad4dfc90d1b654d55"),
        (8, "bec6facf575843270b3fd3c570e2d91c7ffe62baa6b871319983b9709d66a4a0"),
        (9, "b0dd3d7fe15b80936b9a7d5295564896f5a2ec361293e656c9a99c3ae3c553a1"),
        (5, "1c134e5fc7341dc0d571550555b2a8124531b0bae7d02bf120bbc84d89ec084c"),
        // Bass_Pedal_MBComp's chain (#167): it is built from the player's install.
        (8, "6f4dddb28bc1b9c34ebbada26097d15bd891345e1db161030b90d671933dc121"),
        (7, "b4786ec286f77f52d76a295be8fe23c4c266c1344e2108e205de363acaad8d2c"),
        (3, "e387df5ed91cf9d9bea5aed1a9f8c2ae6b4f55dd444ee2305b7cbfa01b661a4b"),
        (4, "a6b52ed5859a53e78add3eb9ab19f8e6a5784212cd6e3ecc349938f25086d333"),
        (6, "0fa0cda5935e2746fc6f19f61bb1dd98aadd62f80c3032fef9a0b6d2aad4da33"),
        (6, "896f9578b83be30bb53f3a25fa4f1af69bf059757e4502a992b49c942bdb02c4")
    ];

    [Fact]
    public void NoKnownGameFigure_IsInAGuide()
    {
        var root = TestPaths.RepoRoot();
        var files = GuideFiles(root).ToList();
        // The sentinels the scan must reach: a lint that lists no files passes vacuously.
        Assert.Contains(files, f => f.EndsWith("dsp-tone-rig.md", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("rs-tone-coverage.md", StringComparison.Ordinal));
        Assert.Contains(files, f => Path.GetRelativePath(root, f) == "AGENTS.md");
        Assert.Contains(files, f => Path.GetRelativePath(root, f).Replace('\\', '/') == "DotCharter.Tone/AGENTS.md");

        var hits = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            hits.AddRange(Matches(File.ReadAllText(file), KnownGameFigures)
                .Select(m => $"{relative}:{m.Line}: a figure read from the game's soundbanks (entry {m.Entry})"));
        }

        Assert.True(hits.Count == 0,
            "A guide quotes data read from Rocksmith's soundbanks again. Describe what DotCharter reads and how, " +
            "never the values, curve shapes or effect chains (#790):\n" + string.Join('\n', hits));
    }

    /// <summary>The probe twin, on an INVENTED snippet: the matcher sees through case,
    /// line breaks, Markdown and the Unicode minus, and a changed figure is not a hit.</summary>
    [Fact]
    public void TheMatcher_FindsAHashedSnippet_AndNothingElse()
    {
        (int, string)[] probe = [Entry("−7 dB @ 123 Hz shelf")];

        Assert.Single(Matches("-7 db @ 123 hz shelf", probe));
        Assert.Single(Matches("a\n    **−7 dB**\n    @ `123 Hz` Shelf, b", probe));
        Assert.Equal(3, Matches("x\ny\n−7 dB @ 123 Hz shelf", probe).Single().Line);
        Assert.Empty(Matches("−7 dB @ 124 Hz shelf", probe));
        Assert.Empty(Matches("−7 dB @ 123 Hz", probe));
        Assert.Empty(Matches("17 dB @ 123 Hz shelf", probe));
    }

    [Fact]
    public void EveryEntry_IsAHashAndATokenCount_NeverAFigure()
    {
        Assert.All(KnownGameFigures, e =>
        {
            Assert.InRange(e.Tokens, 1, 16);
            Assert.Matches("^[0-9a-f]{64}$", e.Sha256);
        });
        Assert.Equal(KnownGameFigures.Length, KnownGameFigures.Select(e => e.Sha256).Distinct().Count());
    }

    private static (int Tokens, string Sha256) Entry(string snippet)
    {
        var tokens = Tokenize(snippet).Select(t => t.Value).ToList();
        return (tokens.Count, Hash(string.Join(' ', tokens)));
    }

    private static List<(int Line, int Entry)> Matches(string text, IReadOnlyList<(int Tokens, string Sha256)> entries)
    {
        var tokens = Tokenize(text).ToList();
        var lengths = entries.Select(e => e.Tokens).Distinct().ToList();
        var found = new List<(int Line, int Entry)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Value.Any(char.IsAsciiDigit)) continue;
            foreach (var length in lengths)
            {
                if (i + length > tokens.Count) continue;
                var hash = Hash(string.Join(' ', tokens.Skip(i).Take(length).Select(t => t.Value)));
                for (var e = 0; e < entries.Count; e++)
                {
                    if (entries[e].Tokens == length && entries[e].Sha256 == hash)
                        found.Add((LineOf(text, tokens[i].Index), e));
                }
            }
        }

        return found;
    }

    /// <summary>Lower-case, the Unicode minus and en dash as '-', then words and numbers
    /// (a sign, digits and letters, '.'/',' inside a number); everything else — Markdown,
    /// arrows, units' punctuation, line breaks — separates tokens.</summary>
    private static IEnumerable<(string Value, int Index)> Tokenize(string text)
    {
        var normalized = text.ToLowerInvariant().Replace('−', '-').Replace('–', '-');
        return Token().Matches(normalized).Select(m => (m.Value, m.Index));
    }

    private static string Hash(string joined) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static IEnumerable<string> GuideFiles(string root)
    {
        string[] skipped = ["bin", "obj", ".git", ".vs", "JuceSharp", "TestData", "TestResults", "node_modules", "artifacts", "worktrees"];
        var docs = Path.Combine(root, "docs") + Path.DirectorySeparatorChar;
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!skipped.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (ext == ".md" || (ext is ".html" or ".js" && file.StartsWith(docs, StringComparison.OrdinalIgnoreCase)))
                    yield return file;
            }
        }
    }

    [GeneratedRegex(@"[±+\-]?[0-9a-z_]+(?:[.,][0-9a-z_]+)*")]
    private static partial Regex Token();
}
