using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>CommandCatalog</c> calls itself "one flat list of every discoverable app
///     command", and until 0.41 it was not: it only wrapped what hung off
///     <c>MainWindowViewModel</c>, so ~45 EDITING verbs had no entry at all —
///     every technique toggle and its group variant, fret ±1/±12, string ±1, the
///     six fingering keys, the string mask, double/half time, duplicate, stamp
///     section, paste-at-mouse, delete-related and the four FHP nudges. Several
///     were reachable by KEYBIND ALONE, so <c>Ctrl+Shift+P</c> could not answer
///     "what was that key again" for the vocabulary a charter uses most. It also
///     skewed the 0.41 usage statistics, whose whole value is the DENOMINATOR —
///     a verb nobody can find reads as a verb nobody wants.
///     <para>
///         SOURCE LINTS: <c>CommandCatalog.Build</c> takes a real
///         <c>MainWindowViewModel</c>, which needs the app session, so its
///         behavioural tests would be <c>*UiPerfSmokeTests</c> and outside the
///         default gate. The claims here are about the catalogue's CONTENT, which
///         the source states directly.
///     </para>
/// </summary>
public sealed class CommandPaletteCoverageTests
{
    /// <summary>
    ///     Keybinds that deliberately have NO palette entry. Every exclusion needs
    ///     a reason, because the default is "if it is a keybind, it is findable".
    /// </summary>
    private static readonly Dictionary<string, string> NotInThePalette = new(StringComparer.Ordinal)
    {
        // Digit FAMILIES: the action needs an argument the palette cannot supply,
        // and expanding them would add 38 near-identical rows. Their one
        // argument-free member IS listed (StringMaskClear).
        ["BookmarkBase"] = "digit family (+0..9)",
        ["SetFretBase"] = "digit family (+0..9) — the pen's typed fret",
        ["StringMaskBase"] = "digit family (+1..9); StringMaskClear is listed",
        ["PenToggleStringBase"] = "digit family (+1..9) — note entry at the pen",

        // Motion, not editing: seek/scrub/zoom-of-time granularity. The palette
        // is a verb list, and "move the playhead 100 ms left" is a held key
        // rather than something anyone searches for by name.
        ["SeekStart"] = "motion", ["SeekEnd"] = "motion",
        ["SeekLeft"] = "motion", ["SeekLeftFast"] = "motion", ["SeekLeftFine"] = "motion",
        ["SeekRight"] = "motion", ["SeekRightFast"] = "motion", ["SeekRightFine"] = "motion",
        ["SeekPrev"] = "motion", ["SeekNext"] = "motion",
        ["SeekPrevExtend"] = "motion", ["SeekNextExtend"] = "motion",
        ["SeekPrevBeat"] = "motion", ["SeekNextBeat"] = "motion",
        ["SeekPrevAnchor"] = "motion", ["SeekNextAnchor"] = "motion",
        ["SeekPrevEditTarget"] = "motion", ["SeekNextEditTarget"] = "motion",
        ["SeekPrevScreen"] = "motion", ["SeekNextScreen"] = "motion",
        ["SeekPrevGridLine"] = "motion", ["SeekNextGridLine"] = "motion",
        ["SeekFirstBeat"] = "motion", ["SeekChartEnd"] = "motion",
        ["MoveToFirstItem"] = "motion", ["MoveToLastItem"] = "motion",
        ["Rewind"] = "motion",
        ["QueuePrevMatch"] = "motion (the inspector queue's own <</>>)",
        ["QueueNextMatch"] = "motion (the inspector queue's own <</>>)",
        ["ArrangementNext"] = "motion between arrangements",
        ["ArrangementPrevious"] = "motion between arrangements",

        // Continuous transport/grid adjustment — three granularities each, held.
        ["SpeedUp"] = "held adjustment", ["SpeedUpFast"] = "held adjustment",
        ["SpeedUpFine"] = "held adjustment", ["SpeedDown"] = "held adjustment",
        ["SpeedDownFast"] = "held adjustment", ["SpeedDownFine"] = "held adjustment",
        ["DoubleGrid"] = "held adjustment", ["HalveGrid"] = "held adjustment",

        // Modal / surface-scoped keys with no meaning from a search box.
        ["ClearSelection"] = "Escape; handled by MainWindowViewModel.HandleEscapeKey",
        ["TapLyric"] = "live syllable tapping — only meaningful while playing",
        ["ToggleLyricWordPart"] = "lyric inspector surface",
        ["ToggleLyricPhraseEnd"] = "lyric inspector surface",
        ["Toggle3DFingering"] = "3D overlay toggle, mirrored in Settings"
    };

    /// <summary>
    ///     The editing verbs this change added. Named explicitly as well as
    ///     covered by the reflection guard below, so a regression names the verb
    ///     that vanished instead of reporting a count.
    /// </summary>
    public static TheoryData<string, string> AddedEditingVerbs => new()
    {
        { "note.toggleHopo", "ToggleHopo" },
        { "note.toggleHopoGroup", "ToggleHopoGroup" },
        { "note.toggleHarmonic", "ToggleHarmonic" },
        { "note.toggleHarmonicGroup", "ToggleHarmonicGroup" },
        { "note.toggleMute", "ToggleMute" },
        { "note.toggleMuteGroup", "ToggleMuteGroup" },
        { "note.toggleLinkNext", "ToggleLinkNext" },
        { "note.toggleLinkNextGroup", "ToggleLinkNextGroup" },
        { "note.toggleVibrato", "ToggleVibrato" },
        { "note.toggleVibratoGroup", "ToggleVibratoGroup" },
        { "note.toggleTremolo", "ToggleTremolo" },
        { "note.toggleTremoloGroup", "ToggleTremoloGroup" },
        { "note.togglePicking", "TogglePicking" },
        { "note.togglePickingGroup", "TogglePickingGroup" },
        { "note.toggleAccent", "ToggleAccent" },
        { "note.toggleAccentGroup", "ToggleAccentGroup" },
        { "note.toggleIgnore", "ToggleIgnore" },
        { "note.toggleIgnoreGroup", "ToggleIgnoreGroup" },
        { "note.togglePassOtherNotes", "TogglePassOtherNotes" },
        { "note.togglePassOtherNotesGroup", "TogglePassOtherNotesGroup" },
        { "note.fretUp", "FretUp" },
        { "note.fretDown", "FretDown" },
        { "note.fretUpOctave", "FretUpOctave" },
        { "note.fretDownOctave", "FretDownOctave" },
        { "note.stringUp", "StringUp" },
        { "note.stringDown", "StringDown" },
        { "note.fingerNone", "SetFingerNone" },
        { "note.fingerThumb", "SetFingerThumb" },
        { "note.finger1", "SetFinger1" },
        { "note.finger2", "SetFinger2" },
        { "note.finger3", "SetFinger3" },
        { "note.finger4", "SetFinger4" },
        { "edit.stringMaskClear", "StringMaskClear" },
        { "edit.pasteAtMouse", "PasteAtMouse" },
        { "edit.duplicate", "DuplicateSelection" },
        { "edit.deleteRelated", "DeleteRelated" },
        { "tools.doubleTime", "DoubleTime" },
        { "tools.halfTime", "HalfTime" },
        { "tools.stampSection", "StampSection" },
        { "tools.anchorFretUp", "AnchorFretUp" },
        { "tools.anchorFretDown", "AnchorFretDown" },
        { "tools.anchorWidthUp", "AnchorWidthUp" },
        { "tools.anchorWidthDown", "AnchorWidthDown" }
    };

    [Theory]
    [MemberData(nameof(AddedEditingVerbs))]
    public void Each_editing_verb_has_a_palette_entry_wired_to_its_own_keybind(string id, string keybind)
    {
        var catalog = Catalog();
        Assert.Contains($"\"{id}\"", catalog, StringComparison.Ordinal);
        // …and the shortcut column shows THAT verb's chord, not a neighbour's.
        // The trailing `)` is load-bearing: without it `k.ToggleVibrato` matches
        // `k.ToggleVibratoGroup` and half the note-modifier rows pass vacuously.
        Assert.Contains(ShortcutReference(keybind), catalog, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The verb that is MENU-only with no keybind at all, so a charter who
    ///     works from the palette could not reach it by any means. It was two
    ///     until 2026-09-04, when Edit ▸ Select By Query and its dialog were
    ///     deleted.
    /// </summary>
    [Theory]
    [InlineData("edit.checkpoints", "Checkpoints_Menu")]
    public void The_menu_only_tools_are_reachable_from_the_palette(string id, string titleKey)
    {
        var catalog = Catalog();
        Assert.Contains($"\"{id}\"", catalog, StringComparison.Ordinal);
        Assert.Contains($"\"{titleKey}\"", catalog, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The durable half: EVERY rebindable action is findable in the palette,
    ///     or is on <see cref="NotInThePalette" /> with a reason. Without this a
    ///     new technique toggle ships keybind-only again and nothing notices.
    /// </summary>
    [Fact]
    public void Every_rebindable_action_is_either_in_the_palette_or_deliberately_excluded()
    {
        var catalog = Catalog();
        var missing = KeybindProperties()
            .Where(p => !NotInThePalette.ContainsKey(p))
            .Where(p => !catalog.Contains(ShortcutReference(p), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            "These rebindable actions have no command-palette entry, so Ctrl+Shift+P cannot find them " +
            "and the usage statistics will report them as unused. Add an entry, or add them to " +
            $"NotInThePalette with a reason:\n  {string.Join("\n  ", missing)}");
    }

    /// <summary>
    ///     Guards the guard: an exclusion naming a property that no longer exists
    ///     is a stale excuse, and it would let a real gap hide behind it.
    /// </summary>
    [Fact]
    public void Every_exclusion_names_a_real_keybind_and_carries_a_reason()
    {
        var known = KeybindProperties().ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(known);

        foreach (var (property, reason) in NotInThePalette)
        {
            Assert.True(known.Contains(property),
                $"NotInThePalette lists '{property}', which is not a KeybindConfig property any more.");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"'{property}' is excluded with no reason.");
        }
    }

    /// <summary>
    ///     A palette id is the key the usage statistics count under, so a
    ///     duplicate silently merges two commands' totals.
    /// </summary>
    [Fact]
    public void Every_palette_id_is_unique()
    {
        var ids = Regex.Matches(Catalog(), @"From(?:Command|Action)\(""(?<id>[^""]+)""")
            .Select(m => m.Groups["id"].Value)
            .ToArray();

        Assert.NotEmpty(ids);
        var duplicates = ids.GroupBy(i => i, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        Assert.True(duplicates.Length == 0, $"duplicate palette ids: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    ///     Every title key must resolve in BOTH resx files. A missing key renders
    ///     as the raw key NAME in the palette row — visible, ugly, and exactly the
    ///     kind of thing that ships because nobody scrolled that far.
    /// </summary>
    [Fact]
    public void Every_palette_title_key_exists_in_both_resx_files()
    {
        var titles = Regex.Matches(Catalog(), @"From(?:Command|Action)\(""[^""]+"",\s*""(?<key>[^""]+)""")
            .Select(m => m.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(titles.Length > 100, $"only {titles.Length} palette titles found — did the parse break?");

        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var keys = ResxKeys(file);
            var absent = titles.Where(t => !keys.Contains(t)).Order(StringComparer.Ordinal).ToArray();
            Assert.True(absent.Length == 0, $"{file} is missing: {string.Join(", ", absent)}");
        }
    }

    /// <summary>
    ///     …and every CATEGORY key too. Categories come from `const string`
    ///     locals, so they are resolved from the source rather than the call.
    /// </summary>
    [Fact]
    public void Every_palette_category_key_exists_in_both_resx_files()
    {
        var categories = Regex.Matches(Catalog(), @"const string \w+ = ""(?<key>[^""]+)"";")
            .Select(m => m.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(categories.Length >= 9, $"only {categories.Length} categories found — did the parse break?");

        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var keys = ResxKeys(file);
            var absent = categories.Where(c => !keys.Contains(c)).Order(StringComparer.Ordinal).ToArray();
            Assert.True(absent.Length == 0, $"{file} is missing category: {string.Join(", ", absent)}");
        }
    }

    /// <summary>
    ///     `StampSection` was inline in <c>AppKeybindRegistrar</c>, so the key and
    ///     the palette would have run two copies of it. One implementation, on the
    ///     shell — the same shape as `InsertBeatAtPlayhead`.
    /// </summary>
    [Fact]
    public void StampSectionHasOneImplementationBehindBothTheKeyAndThePalette()
    {
        var registrar = ReadSource("DotCharter.UI", "Input", "AppKeybindRegistrar.cs");
        Assert.Contains("_shell.StampSectionCommand.Execute(null)", registrar, StringComparison.Ordinal);
        Assert.DoesNotContain("_eventEditing.StampLastMarker", registrar, StringComparison.Ordinal);

        // The editing verbs are the shell view model's MainWindowViewModel.Editing.cs
        // partial.
        var shell = ReadSource("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.Editing.cs");
        Assert.Contains("private void StampSection()", shell, StringComparison.Ordinal);
        Assert.Contains("_eventEditing.StampLastMarker", shell, StringComparison.Ordinal);
    }

    /// <summary>How a palette row cites its keybind, closing paren included so
    /// one property name cannot match another that merely starts with it.</summary>
    private static string ShortcutReference(string keybind) => $"() => k.{keybind})";

    private static IEnumerable<string> KeybindProperties() =>
        typeof(KeybindConfig)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(KeyChord))
            .Select(p => p.Name);

    private static string Catalog() =>
        ReadSource("DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs");

    private static HashSet<string> ResxKeys(string file) =>
        XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file)).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")?.Value ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

    private static string ReadSource(params string[] parts) =>
        UiSourceTree.ReadAllText(Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray()));
}
