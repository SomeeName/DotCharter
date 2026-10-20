using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Tone.Audio.Equalizer;
using DotCharter.Services.Export;
using DotCharter.Tests.Services.Editing;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     Mechanical guards for the 0.13.0 localization audit.
///
///     1. <see cref="Views_HaveNoHardcodedUserFacingStrings" /> scans EVERY view
///        for literal user-facing attribute values so a regression (a new
///        hard-coded <c>Text=/Content=/ToolTip.Tip=</c>) fails the build instead
///        of silently shipping an un-localizable string. It was pointed at the
///        inspector folder alone until 0.58.
///     2. <see cref="ResxFiles_HaveIdenticalKeySets" /> enforces the repo rule
///        that the invariant, de-DE and ja-JP resources stay key-for-key in
///        lock-step.
///     3. <see cref="AllXamlLocReferences_ResolveToADefinedKey" /> and
///        <see cref="AllCodeLocalizationLookups_ResolveToADefinedKey" /> are the
///        two halves of "a referenced key exists" — the XAML markup extension
///        and the C# lookups. Neither failure mode raises anything at runtime:
///        the lookup falls back to the KEY and the identifier is what the user
///        reads.
///
///     Both locate the repository from this source file's compile-time path so
///     they work regardless of the test host's working directory.
/// </summary>
public sealed class LocalizationSweepTests
{
    // Attributes that carry user-visible text. A literal (non-binding) value on
    // any of these that contains a real word must come from a resx key.
    //
    // The (?<![\w.]) matters: without it "Content" matches the tail of
    // SizeToContent, and every window that sets SizeToContent="Height" was
    // reported as shipping the un-localized word "Height".
    private static readonly Regex AttrRegex = new(
        "(?<![\\w.])(?<attr>Text|Content|ToolTip\\.Tip|Watermark|Header|PlaceholderText|Title)\\s*=\\s*\"(?<val>[^\"]*)\"",
        RegexOptions.Compiled);

    /// <summary>
    ///     Literals that are deliberately NOT resx keys, with the reason. Kept
    ///     exhaustive rather than pattern-matched so adding one is a decision
    ///     somebody made on purpose.
    /// </summary>
    private static readonly HashSet<string> AllowedLiterals = new(StringComparer.Ordinal)
    {
        // The product name. Translating it would be wrong in every language.
        "DotCharter", "DOTCHARTER",
        // The credit TONE3000's API terms prescribe word for word (#867); a brand line, not language.
        "Powered by TONE3000",
        // Key names, spelled as the palette's own rows spell a chord ("Ctrl+N").
        "Ctrl+Shift+P", "Esc",
        // (The five log-level names lived here until 2026-08-29, on the reason
        // that a German dropdown selecting "Warnung" would write "Warning" to
        // disk. That was never true of this control: the settings combo binds
        // SelectedIndex, so the captions decide nothing — and the Diagnostics
        // window had been localizing the same words for releases. They are
        // Settings_LogLevel_* now, and the rows are gone rather than left to
        // rot: an allow-list entry that matches nothing has stopped being a
        // decision.)
    };

    /// <summary>Sample values in a watermark — a URL, an email, a JWT. They show
    /// the SHAPE of the input, and shapes are not language.</summary>
    private static readonly Regex SampleValue = new(
        @"^(https?://|[\w.+-]+@[\w.-]+\.\w+$|eyJ)", RegexOptions.Compiled);

    // At least two consecutive ASCII letters == a real word (skips glyphs like
    // "—", single-letter mnemonics "H"/"P"/"M"/"S", numbers and punctuation).
    private static readonly Regex HasWord = new("[A-Za-z]{2,}", RegexOptions.Compiled);

    // The {localization:Loc SomeKey} XAML markup extension; captures the key.
    private static readonly Regex LocRefRegex = new(
        @"\{\s*localization:Loc\s+(?<key>[A-Za-z0-9_]+)", RegexOptions.Compiled);

    /// <summary>
    ///     The other way XAML names a key: the ConverterParameter of
    ///     <c>LocalizedFormatConverter</c>. `Binding.StringFormat` is a
    ///     <c>string</c> property, so `{localization:Loc …}` cannot be put
    ///     there and a localized composite has to arrive through a converter.
    ///     Without this the key would be spelled in exactly one place in the
    ///     repo and guarded by nothing.
    /// </summary>
    private static readonly Regex LocConverterParamRegex = new(
        @"LocalizedFormatConverter\.Instance\}\s*,\s*ConverterParameter\s*=\s*(?<key>[A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    /// <summary>
    ///     <c>StringFormat</c> values whose letters are NOT language, with the
    ///     reason. Kept as an exhaustive set rather than a pattern for the same
    ///     reason <see cref="AllowedLiterals" /> is: "dB" and "ms" are two
    ///     characters from "Model:", and a rule loose enough to admit one would
    ///     admit the other.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedFormats = new(StringComparer.Ordinal)
    {
        // SI-derived unit symbols. Not abbreviations of an English word —
        // they are the same glyphs in German and Japanese.
        ["{}{0:0.##} ms"] = "millisecond, an SI unit symbol",
        // A .NET DATE FORMAT SPECIFIER, not text: HH/mm/ss are the pattern
        // characters. Translating them would break the clock, and the
        // SEPARATOR is already culture-resolved by DateTime.ToString.
        ["HH:mm:ss"] = "a DateTime format specifier, not a sentence"
    };

    /// <summary>A binding's <c>StringFormat</c>, single-quoted or bare.</summary>
    private static readonly Regex StringFormatRegex = new(
        @"StringFormat\s*=\s*(?:'(?<fmt>[^']*)'|(?<fmt>[^,}""]*))", RegexOptions.Compiled);

    /// <summary>
    ///     Scans EVERY view, not just the inspector folder the 0.13.0 audit
    ///     swept. The whole tree turned out to be clean already — the seventeen
    ///     literals left in it are the product name, two key names, three
    ///     sample values and the log-level identifiers — so there is no reason
    ///     to keep the guard pointed at one folder while the other ~200 views
    ///     ship whatever they like.
    /// </summary>
    [Fact]
    public void Views_HaveNoHardcodedUserFacingStrings()
    {
        var viewsDir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        Assert.True(Directory.Exists(viewsDir), $"Views folder not found at {viewsDir}");

        var offenders = new List<string>();
        var files = 0;
        foreach (var file in UiSourceTree.EnumerateFiles(viewsDir, "*.axaml", SearchOption.AllDirectories))
        {
            files++;
            var text = UiSourceTree.ReadAllText(file);
            foreach (Match m in AttrRegex.Matches(text))
            {
                var val = m.Groups["val"].Value;
                if (val.StartsWith('{')) continue;              // binding / markup extension
                if (!HasWord.IsMatch(val)) continue;            // glyph / mnemonic / number
                if (AllowedLiterals.Contains(val)) continue;    // named exception, see the set
                if (SampleValue.IsMatch(val)) continue;         // URL / email / token sample
                offenders.Add($"{Path.GetFileName(file)}: {m.Groups["attr"].Value}=\"{val}\"");
            }
        }

        // Non-vacuity: a wrong path or a changed extension would sweep nothing.
        Assert.True(files > 100, $"only {files} .axaml files scanned — the sweep is not reaching Views");

        Assert.True(offenders.Count == 0,
            "Un-localized user-facing literals found in DotCharter.UI/Views. Move each to a resx key " +
            "(BOTH files), or — if it is genuinely not language — add it to AllowedLiterals with the " +
            "reason:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    ///     <para>
    ///         The hole in the test above: it skips every attribute value that
    ///         starts with <c>{</c>, because that is a binding — and a binding
    ///         can carry a <c>StringFormat</c>, which is a user-facing sentence
    ///         written in the markup. Two shipped through it (2026-08-29): the
    ///         chord-bend picker's String-number format, with the German for
    ///         that word ("Saite") already in the resx ten lines above it, and
    ///         the stem dialog's model line.
    ///     </para>
    ///     <para>
    ///         <c>Binding.StringFormat</c> is a <c>string</c>, so there is no
    ///         way to put a key there — the fix is always
    ///         <c>LocalizedFormatConverter</c> with the key as the
    ///         <c>ConverterParameter</c>. A format made only of placeholders,
    ///         digits and symbols is not language and passes.
    ///     </para>
    /// </summary>
    [Fact]
    public void NoBindingFormatsAUserFacingStringInEnglish()
    {
        var uiDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var offenders = new List<string>();
        var formats = 0;
        var seen = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(uiDir, "*.axaml", SearchOption.AllDirectories))
        {
            var text = UiSourceTree.ReadAllText(file);
            foreach (Match m in StringFormatRegex.Matches(text))
            {
                formats++;
                var fmt = m.Groups["fmt"].Value;
                seen.Add(fmt);
                if (!HasWord.IsMatch(fmt)) continue; // digits, glyphs and placeholders only
                if (AllowedFormats.ContainsKey(fmt)) continue;
                offenders.Add($"{Path.GetFileName(file)}: StringFormat={fmt}");
            }
        }

        // Non-vacuity: the app really does use StringFormat, a lot, and every
        // allow-listed value is still one of them — a row that matches nothing
        // is a row that has quietly stopped being a decision.
        Assert.True(formats > 30, $"only {formats} StringFormat values found — the pattern stopped matching");
        foreach (var (format, reason) in AllowedFormats)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Allowed format '{format}' carries no reason.");
            Assert.Contains(format, seen);
        }

        Assert.True(offenders.Count == 0,
            "A binding's StringFormat is user-facing text and cannot hold a localization key. Use " +
            "LocalizedFormatConverter with the resx key as the ConverterParameter:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     <para>
    ///         A RATCHET on the number of keys whose translation is still the
    ///         English string. Key PARITY is already pinned
    ///         (<see cref="ResxFiles_HaveIdenticalKeySets" />) and it is the
    ///         reason a gap is invisible: the key is present, the value is
    ///         just never translated, so nothing renders wrong — a German
    ///         user simply reads "Tuner", "Mute", "Lyrics", "Review" scattered
    ///         through a German UI. Eleven were found that way in 2026-08-29,
    ///         every one of them ALREADY translated in Japanese, which is what
    ///         proved they were oversights and not decisions.
    ///     </para>
    ///     <para>
    ///         The ceiling is a HIGH-WATER MARK, not a target. Plenty of
    ///         identical values are correct — "Laser", "Position", "Album",
    ///         "Vibrato", "Debug", "Bass" — so the number will never reach
    ///         zero and pinning an exact figure would just churn. What it
    ///         catches is a NEW key added to the translated tables by copying
    ///         the English in. Lower it when you translate something. Raise it
    ///         only for a value that is genuinely identical in that language,
    ///         and say which key and why in the SAME change — never to make a
    ///         build green.
    ///     </para>
    ///     <para>
    ///         The one recorded raise: <c>Keybind_Mod_Ctrl/Alt/Shift</c>
    ///         (2026-08-29). German gains one (Alt is Alt) and Japanese three —
    ///         a Japanese keyboard prints those three modifiers in Latin, so
    ///         "Ctrl" IS the Japanese label. de 244 → 245, ja 22 → 25.
    ///         And <c>Rig_Cpu</c> (2026-10-13, the rig footer's CPU readout, JuceSharp
    ///         #29): "CPU" is the label in German and in Japanese alike. de 245 → 246,
    ///         ja 25 → 26.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("Resources.de-DE.resx", 246)]
    [InlineData("Resources.ja-JP.resx", 26)]
    public void UntranslatedValues_DoNotGrow(string fileName, int ceiling)
    {
        var locDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization");
        var english = ReadResxValues(Path.Combine(locDir, "Resources.resx"));
        var translated = ReadResxValues(Path.Combine(locDir, fileName));

        // Only values carrying a WORD count. A shared glyph, a number, a unit
        // or a bare placeholder is identical on purpose in every language.
        var untranslated = english
            .Where(pair => translated.TryGetValue(pair.Key, out var value)
                           && value == pair.Value
                           && Regex.IsMatch(pair.Value, "[A-Za-z]{3,}"))
            .Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(english.Count > 3000, $"only {english.Count} English keys parsed — the reader is broken");

        Assert.True(untranslated.Count <= ceiling,
            $"{fileName} now leaves {untranslated.Count} keys on their English value, up from the {ceiling} "
            + "recorded here. A key copied across untranslated reads as a working string and breaks nothing, "
            + "which is exactly why it needs a ratchet. Translate the new ones — do NOT raise the ceiling:"
            + Environment.NewLine + string.Join(", ", untranslated));
    }

    /// <summary>
    ///     Every <c>{localization:Loc Key}</c> reference across the app's XAML must
    ///     resolve to a key actually defined in the invariant resx. A typo'd or
    ///     removed key builds clean but renders as a runtime lookup miss (blank /
    ///     raw key) for users — the v0.24 polish sweep added several icon-button
    ///     accessible names and new keys, so guard the whole reference surface.
    /// </summary>
    [Fact]
    public void AllXamlLocReferences_ResolveToADefinedKey()
    {
        var uiDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        Assert.True(Directory.Exists(uiDir), $"UI project folder not found at {uiDir}");
        var defined = ReadResxKeys(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));

        var dangling = new List<string>();
        foreach (var file in UiSourceTree.EnumerateFiles(uiDir, "*.axaml", SearchOption.AllDirectories))
        {
            var text = UiSourceTree.ReadAllText(file);
            foreach (Match m in LocRefRegex.Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (!defined.Contains(key))
                    dangling.Add($"{Path.GetFileName(file)}: {{localization:Loc {key}}}");
            }

            foreach (Match m in LocConverterParamRegex.Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (!defined.Contains(key))
                    dangling.Add($"{Path.GetFileName(file)}: ConverterParameter={key}");
            }
        }

        Assert.True(dangling.Count == 0,
            "XAML references localization keys that are not defined in Resources.resx " +
            "(build-clean, but a runtime lookup miss for users):\n" + string.Join("\n", dangling));
    }

    /// <summary>
    ///     The CODE half of the same rule. XAML's <c>{localization:Loc Key}</c>
    ///     references are guarded above; the ~860 lookups view models make in C#
    ///     were not, and they fail in a NASTIER way.
    ///     <para>
    ///         <c>LocalizationService.GetString</c> returns the KEY when nothing
    ///         resolves, so <c>Format("Diagnostics_FramesFmt", 512)</c> becomes
    ///         <c>string.Format(culture, "Diagnostics_FramesFmt", 512)</c> — a
    ///         format string with no placeholders — and the Session Health card
    ///         printed the literal identifier <c>Diagnostics_FramesFmt</c> where
    ///         the audio buffer size belongs, for every user, whenever audio was
    ///         running. No exception, no log line, and the three sibling keys on
    ///         the adjacent lines all existed.
    ///     </para>
    ///     <para>
    ///         Receivers are resolved per file rather than guessed: the literal
    ///         <c>LocalizationService.Instance</c>, anything declared
    ///         <c>ILocalizationService x</c> (the injected field/parameter), and
    ///         anything assigned from <c>LocalizationService.Instance</c> (the
    ///         <c>var loc = …</c> alias 20 files use). Keying off a name list
    ///         instead would go quietly blind the day someone picks a new one.
    ///     </para>
    /// </summary>
    [Fact]
    public void AllCodeLocalizationLookups_ResolveToADefinedKey()
    {
        var defined = ReadResxKeys(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));

        var dangling = new List<string>();
        var sites = 0;
        var perProject = new Dictionary<string, int>(StringComparer.Ordinal);

        // Every project that shows localized text. A project left out of the walk
        // fails in exactly the same silent way: the key name is what the user
        // reads, and a Format() one loses its arguments with it.
        foreach (var project in LookupProjects)
        {
            var dir = Path.Combine(TestPaths.RepoRoot(), project);
            Assert.True(Directory.Exists(dir), $"{project} folder not found at {dir}");
            perProject[project] = 0;

            var sources = UiSourceTree.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(file =>
                    !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToDictionary(file => file, UiSourceTree.ReadAllText);
            var partialReceivers = ReceiversByPartialType(sources.Values);

            foreach (var (file, text) in sources)
            {
                var receivers = LocalizationReceivers(text)
                    .Concat(PartialTypesIn(text).SelectMany(type =>
                        partialReceivers.TryGetValue(type, out var shared) ? shared : []))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (receivers.Count == 0) continue;

                foreach (var m in LookupPatterns(text, receivers).SelectMany(pattern => Regex.Matches(text, pattern)))
                {
                    sites++;
                    perProject[project]++;
                    var keys = new List<string> { m.Groups["key"].Value };
                    // Plural(oneKey, manyKey, count, …) — BOTH forms must resolve or
                    // the count of 1 (or every other count) prints the key name.
                    if (m.Groups["meth"].Value is "Plural" or "PluralWith" && m.Groups["key2"].Success)
                        keys.Add(m.Groups["key2"].Value);

                    foreach (var key in keys.Where(k => !defined.Contains(k)))
                        dangling.Add($"{Path.GetFileName(file)}: \"{key}\"");
                }
            }
        }

        // Non-vacuity: this lint is a regex over source, so a receiver-detection
        // change that quietly matched nothing would leave it passing forever.
        Assert.True(sites >= 1200,
            $"only {sites} localization lookups found — the receiver " +
            "detection has gone blind; this guard asserts nothing in that state");

        // …and a PER-PROJECT floor, because the total is dominated by the UI:
        // Services could drop out of the walk entirely (a renamed folder, a
        // mis-typed path) without moving the total enough to notice.
        foreach (var (project, count) in perProject)
        {
            Assert.True(count >= LookupFloors[project],
                $"{project} contributed {count} localization lookups, below its floor of " +
                $"{LookupFloors[project]} — part of it is no longer being scanned");
        }

        Assert.True(dangling.Count == 0,
            "C# localization lookups name keys that are not defined in Resources.resx. " +
            "GetString falls back to the KEY, so each of these prints its own identifier " +
            "on screen (and a Format() one loses its arguments entirely):\n" +
            string.Join("\n", dangling.Distinct().OrderBy(d => d, StringComparer.Ordinal)));
    }

    /// <summary>
    ///     The regexes that find a literal key in <paramref name="source" />: a lookup on a
    ///     receiver (<c>_loc["Key"]</c>, <c>_loc?.Format("Key", …)</c>), and a call to a
    ///     helper that only forwards its key to one (<see cref="KeyForwardingHelpers" />).
    /// </summary>
    private static IEnumerable<string> LookupPatterns(string source, IReadOnlyList<string> receivers)
    {
        var receiverAlternation = string.Join("|", receivers.Select(Regex.Escape));
        yield return
            @"(?<!\w)(?:" + receiverAlternation + @")\s*\??\s*" +
            @"(?:\.(?<meth>Format|GetString|PluralWith|Plural)\s*\(|\[)\s*" +
            @"""(?<key>[A-Za-z0-9_.]+)""(?!\s*\+)(?:\s*,\s*""(?<key2>[A-Za-z0-9_.]+)"")?";

        var helpers = KeyForwardingHelpers(source, receiverAlternation);
        if (helpers.Count > 0)
        {
            yield return @"(?<!\w)(?:" + string.Join("|", helpers.Select(Regex.Escape)) + @")\s*\(\s*" +
                         @"""(?<key>[A-Za-z0-9_.]+)""(?!\s*\+)";
        }
    }

    /// <summary>
    ///     A one-line helper that does nothing but hand its key to a receiver —
    ///     <c>string Text(string key) =&gt; _localization?[key] ?? key;</c>, which a view
    ///     model with an OPTIONAL localization uses to print the key when it has none — is a
    ///     lookup too: its call sites name the keys, so the sweep reads them there (#549).
    /// </summary>
    private static List<string> KeyForwardingHelpers(string source, string receiverAlternation) =>
        Regex.Matches(source,
                @"\bstring\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*string\s+(?<param>[A-Za-z_][A-Za-z0-9_]*)\b[^)]*\)\s*=>\s*" +
                @"(?:" + receiverAlternation + @")\s*\??\s*" +
                @"(?:\[\s*\k<param>\s*\]|\.(?:Format|GetString|PluralWith|Plural)\s*\(\s*\k<param>\b)")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void TheLookupScan_SeesNullConditionalLookupsAndKeyForwardingHelpers()
    {
        const string source = """
            class Probe
            {
                private readonly ILocalizationService? _localization;
                internal string Text(string key) => _localization?[key] ?? key;
                private string TextFormat(string key, params object[] args) => _localization?.Format(key, args) ?? key;
                string A => Text("Probe_One");
                string B => TextFormat("Probe_Two", 1);
                string C => _localization?["Probe_Three"] ?? "";
                string D => _localization?.Format("Probe_Four", 2) ?? "";
                string E => Text(DynamicKey);
            }
            """;

        var keys = LookupPatterns(source, LocalizationReceivers(source))
            .SelectMany(pattern => Regex.Matches(source, pattern))
            .Select(m => m.Groups["key"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Probe_Four", "Probe_One", "Probe_Three", "Probe_Two"], keys);
    }

    private static readonly string[] LookupProjects =
    [
        "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone",
        "DotCharter.Presentation", "DotCharter.Tone.UI", "DotCharter.RigLab",
    ];

    private static readonly Dictionary<string, int> LookupFloors = new(StringComparer.Ordinal)
    {
        ["DotCharter.UI"] = 550,
        ["DotCharter.Services"] = 110,
        ["DotCharter.Tone"] = 15,
        ["DotCharter.Presentation"] = 300,
        ["DotCharter.Tone.UI"] = 170,
        ["DotCharter.RigLab"] = 15,
    };

    private static readonly Regex PartialTypeRegex = new(
        @"\bpartial\s+(?:class|record|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static IEnumerable<string> PartialTypesIn(string source) => PartialTypeRegex.Matches(source).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal);

    /// <summary>
    ///     A field declared in one part of a partial type is in scope in every
    ///     part, so each part's lookups resolve against the receivers of all of them.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ReceiversByPartialType(IEnumerable<string> sources)
    {
        var byType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var receivers = LocalizationReceivers(source);
            foreach (var type in PartialTypesIn(source))
            {
                if (!byType.TryGetValue(type, out var set))
                {
                    byType[type] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.UnionWith(receivers);
            }
        }

        return byType;
    }

    /// <summary>
    ///     Identifiers in <paramref name="source" /> that hold an
    ///     <see cref="DotCharter.Localization.ILocalizationService" />, plus the
    ///     singleton access path itself.
    /// </summary>
    private static List<string> LocalizationReceivers(string source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (source.Contains("LocalizationService.Instance", StringComparison.Ordinal))
            names.Add("LocalizationService.Instance");

        // var loc = LocalizationService.Instance;
        foreach (Match m in Regex.Matches(source,
                     @"(?:var|ILocalizationService|LocalizationService)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*LocalizationService\.Instance"))
        {
            names.Add(m.Groups["name"].Value);
        }

        // private readonly ILocalizationService _localization;  /  ctor parameters — nullable
        // ones too: an optional host's `ILocalizationService? _localization` is a receiver
        foreach (Match m in Regex.Matches(source,
                     @"\b(?:ILocalizationService|LocalizationService)\??\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*[;,)=]"))
        {
            names.Add(m.Groups["name"].Value);
        }

        return names.ToList();
    }

    /// <summary>
    ///     All THREE built-in tables — invariant, de-DE and ja-JP — key for key.
    ///     A key present only in the invariant file silently falls back to
    ///     English for that language's users; nothing throws and nothing is
    ///     logged, so this comparison is the only thing that ever notices.
    /// </summary>
    [Fact]
    public void ResxFiles_HaveIdenticalKeySets()
    {
        var locDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization");
        var invariant = ReadResxKeys(Path.Combine(locDir, "Resources.resx"));

        // Non-vacuity: a wrong path would compare two empty sets happily.
        Assert.True(invariant.Count > 3000,
            $"only {invariant.Count} keys read from Resources.resx — the walk is not reaching the resx");

        foreach (var fileName in TranslatedResxFiles)
        {
            var translated = ReadResxKeys(Path.Combine(locDir, fileName));

            var missingInTranslation = invariant.Except(translated).OrderBy(k => k).ToList();
            var missingInInvariant = translated.Except(invariant).OrderBy(k => k).ToList();

            Assert.True(missingInTranslation.Count == 0,
                $"Keys present in Resources.resx but missing from {fileName}:\n" +
                string.Join("\n", missingInTranslation));
            Assert.True(missingInInvariant.Count == 0,
                $"Keys present in {fileName} but missing from Resources.resx:\n" +
                string.Join("\n", missingInInvariant));
        }
    }

    /// <summary>
    ///     A duplicate key is silently resolved by MSBuild in favour of the FIRST
    ///     definition (warning MSB3568), so the later one is dropped and whatever
    ///     view binds it renders the earlier text. That is how the chord inspector
    ///     ended up showing note-oriented tooltips. Key-set comparison alone can
    ///     never catch it — a <c>HashSet</c> collapses the duplicate away.
    /// </summary>
    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void ResxFile_DefinesEachKeyExactlyOnce(string fileName)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName);
        var duplicates = ReadResxKeyList(path)
            .GroupBy(k => k, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} (x{g.Count()})")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"{fileName} defines these keys more than once; MSBuild keeps only the first " +
            $"and silently drops the rest:\n{string.Join("\n", duplicates)}");
    }

    /// <summary>
    ///     The keys nothing above can see, because their NAME is computed:
    ///     <c>Instance[$"Inspector_EqTypeShort_{Band.Type}"]</c>,
    ///     <c>EqPresets.ResourceKeyFor(key)</c>.
    ///     <para>
    ///         Two failure modes, both silent. A new enum member ships with no
    ///         string and the chip reads <c>Inspector_EqTypeShort_Allpass</c>;
    ///         or an unused-key CLEANUP deletes the family, because a scan for
    ///         the literal <c>"Inspector_EqTypeShort_Bell"</c> finds it nowhere
    ///         in the repo — which is true, and is exactly why it must be
    ///         enumerated from the TYPE here rather than from the source text.
    ///     </para>
    ///     <para>
    ///         EVERY resx file, because a family complete only in English
    ///         falls back to English for German and Japanese users without a
    ///         word of warning.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryComputedKeyFamily_IsCompleteInEveryLanguage()
    {
        var expected = new List<string>();

        // Inspector EQ (ABI v29): the band-type chip's short label and the
        // selected band's long name.
        foreach (var type in Enum.GetNames<EqBandType>())
        {
            expected.Add($"Inspector_EqTypeShort_{type}");
            expected.Add($"Inspector_EqTypeName_{type}");
        }

        // The EQ preset picker names each preset through EqPresets.ResourceKeyFor.
        expected.AddRange(EqPresets.Keys.Select(EqPresets.ResourceKeyFor));

        // The sound queue's filter dropdown.
        foreach (var filter in Enum.GetNames<SoundQueueFilter>())
            expected.Add($"Inspector_Queue_Filter_{filter}");

        // The amp popup's group headers.
        expected.AddRange(Enum.GetValues<DspModelGrouping.AmpModelGroup>()
            .Select(DspModelGrouping.AmpGroupResourceKey));

        // The cab picker's group headers (#74 added the full-range group).
        expected.AddRange(Enum.GetValues<DspModelGrouping.CabModelGroup>()
            .Select(DspModelGrouping.CabGroupResourceKey));

        // The Rocksmith export dialog's progress stage, via
        // RocksmithBuildProgress.ResourceKey. The builder used to hand the
        // dialog an English SENTENCE FRAGMENT ("Resolving audio") to drop into
        // Dialog_Export_ProgressStep's {2}, so every non-English user read half a
        // translated line.
        foreach (var stage in Enum.GetNames<RocksmithBuildStage>())
            expected.Add($"Dialog_Export_Stage_{stage}");

        // The history's step names (#185), via HistoryActionText.LabelKeyFor: one
        // per undoable action type, one per merge verb, one per labelled composite.
        expected.AddRange(HistoryLabelKeys.All());

        // The Editor on Fire round trip's kinds of loss (#638), via EofLosses.ResourceKey: one
        // name per thing the EOF writer can approximate or drop.
        expected.AddRange(Enum.GetValues<DotCharter.Services.Parsing.Eof.EofLoss>()
            .Select(DotCharter.Services.Parsing.Eof.EofLosses.ResourceKey));

        // The .dcsong package (#706): the writer's stages, its refusals and warnings,
        // and the reader's refusals, each named through a ResourceKey/KeyFor.
        expected.AddRange(Enum.GetValues<DotCharter.Services.Package.DcSong.DcSongWriteStage>()
            .Select(DotCharter.Services.Package.DcSong.DcSongWriteProgress.KeyFor));
        expected.AddRange(Enum.GetValues<DotCharter.Services.Package.DcSong.DcSongReadFailure>()
            .Select(DotCharter.Services.Package.DcSong.DcSongReadException.KeyFor));
        expected.AddRange(Enum.GetValues<DotCharter.Services.Package.DcSong.DcSongWriteFailure>()
            .Select(DotCharter.Services.Package.DcSong.DcSongWriteException.KeyFor));
        expected.AddRange(Enum.GetValues<DotCharter.Services.Package.DcSong.DcSongWriteWarning>()
            .Select(DotCharter.Services.Package.DcSong.DcSongWriteWarnings.ResourceKey));

        // The Tone designer's "from tone rig" review: one sentence per kind of loss, one name per rail.
        expected.AddRange(Enum.GetValues<DotCharter.Tone.Audio.Dsp.RsToneConversion.RigToneLossKind>()
            .Select(DotCharter.Presentation.Screens.Dialogs.RigToneImportReviewViewModel.LossKey));
        expected.AddRange(DotCharter.Presentation.Screens.Dialogs.RigToneImportReviewViewModel.Rails
            .Select(DotCharter.Presentation.Screens.Dialogs.RigToneImportReviewViewModel.RailKey));

        // Non-vacuity: if the families ever stop enumerating, this asserts
        // nothing. The number only ever grows.
        Assert.True(expected.Count >= 65,
            $"only {expected.Count} computed keys derived — a family stopped enumerating");

        var locDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization");
        foreach (var fileName in new[] { "Resources.resx" }.Concat(TranslatedResxFiles))
        {
            var values = ReadResxValues(Path.Combine(locDir, fileName));
            var missing = expected
                .Where(k => !values.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            Assert.True(missing.Count == 0,
                $"{fileName} is missing computed-lookup keys. Nothing in the repo spells these " +
                "out, so a text search calls them unused — they are built at runtime from an enum " +
                "or a catalogue and a miss prints the IDENTIFIER on screen:\n" +
                string.Join("\n", missing));
        }
    }

    /// <summary>
    ///     A translated value that drops a <c>{0}</c> its English counterpart
    ///     has is a message with the DATA missing — the number, the file name,
    ///     the song title — and nothing throws: <c>string.Format</c> is
    ///     perfectly happy to ignore an argument. The reverse (a translation
    ///     inventing an index English does not pass) is worse still:
    ///     <c>FormatException</c> at the call site, for that language's users
    ///     only.
    ///     <para>
    ///         Japanese moves the placeholders around far more than German does
    ///         — <c>{1} of {0}</c> becomes <c>{0}件中{1}件</c> — so it is the
    ///         index SET that has to match, never the order or the position.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void TranslatedValues_UseTheSamePlaceholdersAsEnglish(string fileName)
    {
        var locDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization");
        var english = ReadResxValues(Path.Combine(locDir, "Resources.resx"));
        var translated = ReadResxValues(Path.Combine(locDir, fileName));

        // Specifier- and alignment-aware: {0:N0} and {0,3} are both index 0.
        var indexes = new Regex(@"\{(\d+)(?:,-?\d+)?(?::[^}]*)?\}", RegexOptions.Compiled);
        string Indexes(string v) => string.Join(
            ",", indexes.Matches(v).Select(m => m.Groups[1].Value).Distinct().OrderBy(int.Parse));

        var mismatches = new List<string>();
        var compared = 0;
        foreach (var (key, en) in english.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!translated.TryGetValue(key, out var tr)) continue;   // key-set parity has its own test
            compared++;
            var a = Indexes(en);
            var b = Indexes(tr);
            if (a != b)
            {
                mismatches.Add(
                    $"{key}: EN uses {{{a}}}, {fileName} uses {{{b}}}\n    EN: {en}\n    XX: {tr}");
            }
        }

        Assert.True(compared > 3000, $"only {compared} key pairs compared — the walk is not reaching the resx");
        Assert.True(mismatches.Count == 0,
            $"Values in {fileName} whose composite-format arguments do not match English. A DROPPED " +
            "index prints a message with its data missing; an EXTRA one throws FormatException for " +
            "that language's users only:\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    ///     The translated sibling tables that must track the invariant one key
    ///     for key. A new built-in language is added HERE and to
    ///     <c>LocalizationService.SupportedCultures</c>.
    /// </summary>
    private static readonly string[] TranslatedResxFiles =
        ["Resources.de-DE.resx", "Resources.ja-JP.resx"];

    private static HashSet<string> ReadResxKeys(string path)
        => ReadResxKeyList(path).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ReadResxValues(string path)
    {
        Assert.True(File.Exists(path), $"resx not found at {path}");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            var name = (string?)data.Attribute("name");
            if (name != null) map[name] = data.Element("value")?.Value ?? "";
        }

        return map;
    }

    private static List<string> ReadResxKeyList(string path)
    {
        Assert.True(File.Exists(path), $"resx not found at {path}");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }
}
