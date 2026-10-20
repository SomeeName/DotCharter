using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The three bundled "Fire" typefaces, checked at the ASSET level.
///     <para>
///         A missing or misnamed font is the quietest failure in the whole
///         theme: <c>FontFamily="avares://…#Space Grotesk"</c> that resolves to
///         nothing produces no warning, no exception and no log line — the text
///         simply renders in the default face, and the screen looks *almost*
///         right. Nothing else in the suite would notice.
///     </para>
///     <para>
///         The load-bearing assertion is
///         <see cref="EveryDeclaredFamily_IsActuallyPresentInABundledFile" />: it
///         reads the family name out of each TTF's own <c>name</c> table and
///         compares it with the string the token actually asks for. That is what
///         catches a typo, a renamed file, and the specific trap this bundle was
///         built around — Google's static Medium/SemiBold instances carry
///         nameID 1 = "Space Grotesk SemiBold", i.e. a family of their OWN, so
///         dropping one into the folder would NOT add a weight to
///         "Space Grotesk"; it would add a family nothing references.
///     </para>
///     <para>
///         Pure and platform-free, so it runs in the default gate. Its runtime
///         counterpart — does Avalonia's real <c>FontManager</c> hand back a
///         glyph typeface — is
///         <c>DotCharter.Tests.Perf.FireFontResolutionUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class FireFontAssetTests
{
    /// <summary>Every cut we ship, and the family each one must join.</summary>
    private static readonly (string File, string Family)[] Bundled =
    [
        ("AtkinsonHyperlegible-Regular.ttf", "Atkinson Hyperlegible"),
        ("AtkinsonHyperlegible-Bold.ttf", "Atkinson Hyperlegible"),
        ("JetBrainsMono-Regular.ttf", "JetBrains Mono"),
        ("JetBrainsMono-Bold.ttf", "JetBrains Mono")
    ];

    private static readonly string[] Licences =
        ["OFL-AtkinsonHyperlegible.txt", "OFL-JetBrainsMono.txt"];

    [Fact]
    public void EveryBundledCut_IsOnDiskAndIsARealTrueTypeFile()
    {
        foreach (var (file, _) in Bundled)
        {
            var path = Path.Combine(FontDir(), file);
            Assert.True(File.Exists(path), $"{file} is missing from DotCharter.UI/Assets/Fonts");

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 10_000, $"{file} is {bytes.Length} bytes — that is not a font");

            // sfnt version: 0x00010000 (TrueType outlines) or 'true'.
            var tag = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            Assert.True(tag is 0x00010000 or 0x74727565,
                $"{file} does not start with a TrueType sfnt header (0x{tag:X8})");
        }
    }

    /// <summary>
    ///     THE guard. Each `FontFamily` token names a family after the `#`; that
    ///     string must equal a family name a bundled file actually declares, or
    ///     the app renders in the fallback face and says nothing about it.
    /// </summary>
    [Fact]
    public void EveryDeclaredFamily_IsActuallyPresentInABundledFile()
    {
        var declared = DeclaredFamilies();
        Assert.Equal(3, declared.Count); // display / ui / mono — the scan must not pass by finding none

        var present = Bundled
            .Select(b => FamilyNameOf(Path.Combine(FontDir(), b.File)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (key, family) in declared)
        {
            Assert.True(present.Contains(family),
                $"{key} asks for the font family '{family}', but no bundled file declares that name. " +
                $"Bundled families: {string.Join(", ", present.Order())}. " +
                "Avalonia resolves this silently — the text would render in the fallback face.");
        }
    }

    /// <summary>
    ///     The mirror image: no file may sit in the folder under a family name
    ///     nothing references. A Google static Medium/SemiBold instance would
    ///     land here (its nameID 1 is "&lt;Family&gt; SemiBold"), look like it added
    ///     a weight, and do nothing at all.
    /// </summary>
    [Fact]
    public void NoBundledFile_DeclaresAFamilyNothingReferences()
    {
        var declared = DeclaredFamilies().Values.ToHashSet(StringComparer.Ordinal);

        foreach (var file in UiSourceTree.EnumerateFiles(FontDir(), "*.ttf"))
        {
            var family = FamilyNameOf(file);
            Assert.True(declared.Contains(family),
                $"{Path.GetFileName(file)} declares family '{family}', which no FontFamily token asks for. " +
                "Either reference it or delete it — an unreferenced face is dead weight in every build.");
        }
    }

    /// <summary>Each file must also join the family its NAME claims, so a
    /// mis-copied download cannot quietly land in the wrong family.</summary>
    [Fact]
    public void EveryBundledCut_JoinsTheFamilyItsFilenameClaims()
    {
        foreach (var (file, family) in Bundled)
            Assert.Equal(family, FamilyNameOf(Path.Combine(FontDir(), file)));
    }

    /// <summary>OFL 1.1 requires the licence to travel with the font.</summary>
    [Fact]
    public void EveryFamily_ShipsItsOflLicenceBesideTheFont()
    {
        foreach (var licence in Licences)
        {
            var path = Path.Combine(FontDir(), licence);
            Assert.True(File.Exists(path), $"{licence} is missing — OFL 1.1 requires it to ship with the font");
            Assert.Contains("SIL Open Font License", UiSourceTree.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The fonts only reach the app if the csproj embeds them. `Assets\**`
    ///     already globs them today; this fails loudly if that glob is ever
    ///     narrowed, because the symptom would otherwise be "the text looks
    ///     slightly wrong in a release build".
    /// </summary>
    [Fact]
    public void TheFontFolder_IsEmbeddedAsAnAvaloniaResource()
    {
        var csproj = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "DotCharter.UI.csproj")));
        Assert.Matches(new Regex(@"<AvaloniaResource\s+Include=""Assets\\(\*\*|Fonts\\\*\*)"""), csproj);
    }

    /// <summary>
    ///     The mockups load the three families from Google Fonts. The APP must
    ///     not: a font fetched over the network is a startup dependency on
    ///     somebody else's CDN, and it fails by silently rendering in a fallback.
    /// </summary>
    [Fact]
    public void NoAxamlFile_ReferencesAWebFont()
    {
        // XML comments are stripped first: FireTokens.axaml documents the trap
        // by NAMING the CDN the mockups load from, and a scan that cannot tell
        // prose from markup would flag its own documentation. (It did, the first
        // time this ran — which is the cheapest possible proof the scan reads
        // these files at all.)
        var offenders = UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => Regex.Replace(UiSourceTree.ReadAllText(f), "<!--.*?-->", string.Empty, RegexOptions.Singleline)
                .Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"these files pull a font over the network: {string.Join(", ", offenders)}");
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    /// <summary>The `FontFamily` tokens of the Fire layer, as key → family
    /// (the fragment after the `#` in the avares URI).</summary>
    private static Dictionary<string, string> DeclaredFamilies()
    {
        // The file is a CONTAINER over `Styles/FireTokens/` — the FontFamily
        // tokens live in its Palette part.
        var text = StyleFileText.ReadWithIncludes(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens.axaml"));

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text,
                     @"<FontFamily\s+x:Key=""(?<key>[^""]+)"">\s*avares://[^#<]+#(?<family>[^<]+?)\s*</FontFamily>"))
        {
            found[m.Groups["key"].Value] = m.Groups["family"].Value;
        }

        return found;
    }

    /// <summary>
    ///     nameID 1 out of a TrueType `name` table — the family a font joins.
    ///     Written out rather than taken from a library on purpose: this test
    ///     must be able to disagree with whatever Avalonia thinks.
    /// </summary>
    private static string FamilyNameOf(string path)
    {
        var d = File.ReadAllBytes(path);
        var tables = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));

        var nameOffset = -1;
        for (var i = 0; i < tables; i++)
        {
            var rec = 12 + 16 * i;
            if (Encoding.ASCII.GetString(d, rec, 4) != "name") continue;
            nameOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec + 8));
            break;
        }

        Assert.True(nameOffset > 0, $"{Path.GetFileName(path)} has no 'name' table");

        var count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(nameOffset + 2));
        var storage = nameOffset + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(nameOffset + 4));

        for (var i = 0; i < count; i++)
        {
            var rec = nameOffset + 6 + 12 * i;
            var platform = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(rec));
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(rec + 6));
            if (nameId != 1) continue;

            var length = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(rec + 8));
            var offset = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(rec + 10));
            var raw = d.AsSpan(storage + offset, length);

            // Platform 3 (Windows) stores UTF-16BE; platform 1 (Mac) is 8-bit.
            return platform == 3
                ? Encoding.BigEndianUnicode.GetString(raw)
                : Encoding.ASCII.GetString(raw);
        }

        throw new InvalidOperationException($"{Path.GetFileName(path)} declares no family name (nameID 1)");
    }

    private static string FontDir() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Assets", "Fonts");
}
