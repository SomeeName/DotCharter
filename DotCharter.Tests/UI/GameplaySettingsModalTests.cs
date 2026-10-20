using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     Structure guards for the Play Mode settings modal (§6.5), the surface
///     rebuilt from <c>Settings.dc.html</c>.
///
///     <para>Everything here is asserted against the real XAML parsed as XML and
///     the real resx parsed as XML — no Avalonia platform, no headless session.
///     Same approach as <see cref="FireClassReachabilityTests" />, and for the
///     same reason: booting the app session for a visual-tree walk is the
///     teardown trap the default gate excludes. What NEEDS a live tree (does
///     every pane MEASURE) is in
///     <c>Perf/PlayModeScreensUiPerfSmokeTests</c>.</para>
///
///     <para>The load-bearing one is
///     <see cref="EveryRowInTheModal_IsInTheCatalogue_AndViceVersa" />. The
///     modal's rows are declared inline in XAML and its search runs over
///     <see cref="GameplaySettingsCatalog" />; that is two sources for one list,
///     and the only thing that makes it safe is a lint proving they are the same
///     list. Without it, "search matches the real catalogue" degrades to "search
///     matches whatever the catalogue happened to say last year".</para>
/// </summary>
public sealed class GameplaySettingsModalTests
{
    private const string OverlayName = "GameplaySettingsOverlay";

    /// <summary><c>{localization:Loc Some_Key}</c> — the only form the modal
    /// uses for a literal string.</summary>
    private static readonly Regex LocKey = new(
        @"^\{localization:Loc\s+(?<key>[A-Za-z0-9_]+)\}$", RegexOptions.Compiled);

    // ── The acceptance criterion ─────────────────────────────────────────────

    /// <summary>
    ///     §6.5's stated acceptance criterion: EVERY row explains itself in one
    ///     line. Asserted on the markup rather than the catalogue so it cannot be
    ///     satisfied by a catalogue entry with no row behind it — every control
    ///     that changes a setting must sit in a row that also carries a
    ///     <c>FireRowDesc</c>.
    /// </summary>
    [Fact]
    public void EveryControlInTheModal_SitsInARowThatExplainsItself()
    {
        string[] settingControls =
            ["ToggleSwitch", "ComboBox", "Slider", "KeybindCaptureBox", "FireLevelMeter"];

        var offenders = new List<string>();
        var checkedControls = 0;

        foreach (var control in Overlay().Descendants()
                     .Where(e => settingControls.Contains(e.Name.LocalName)))
        {
            // The nav rail and the footer are chrome, not settings; the results
            // list is a projection of rows that were already checked.
            if (IsUnder(control, "GameSettingsNav") || IsUnder(control, "GameSettingsResultsPane")) continue;

            checkedControls++;
            var row = control.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Grid");
            if (row != null && row.Descendants().Any(IsRowDescription)) continue;

            offenders.Add($"<{control.Name.LocalName}> has no FireRowDesc in its row — " +
                          "every configuration-changing control states its consequence in one sentence (§3.8)");
        }

        // A vacuous pass is the failure mode this whole file exists to avoid: if
        // the walk found no controls it would report success with nothing
        // checked. The modal has six sections of them.
        Assert.True(checkedControls >= 40,
            $"only {checkedControls} setting controls found under {OverlayName} — the walk is not seeing the modal");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    /// <summary>
    ///     The catalogue half of the same rule, plus the sections' own
    ///     descriptions: every key it names resolves to a NON-EMPTY string in
    ///     BOTH resx files. An entry pointing at a key that exists in English
    ///     only reads as an explanation in review and is blank for a German
    ///     user.
    /// </summary>
    [Fact]
    public void EveryCatalogueKey_ResolvesInBothLanguages()
    {
        var english = ResxValues("Resources.resx");
        var german = ResxValues("Resources.de-DE.resx");

        var missing = new List<string>();
        foreach (var key in GameplaySettingsCatalog.AllKeys().Distinct())
        {
            if (!english.TryGetValue(key, out var en) || string.IsNullOrWhiteSpace(en))
                missing.Add($"Resources.resx is missing (or blanks) '{key}'");
            if (!german.TryGetValue(key, out var de) || string.IsNullOrWhiteSpace(de))
                missing.Add($"Resources.de-DE.resx is missing (or blanks) '{key}'");
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));

        // …and no row may reuse its own title as its explanation, which is the
        // laziest way to satisfy the letter of the rule and say nothing.
        var selfDescribing = GameplaySettingsCatalog.All
            .Where(e => e.TitleKey == e.DescriptionKey)
            .Select(e => e.TitleKey)
            .ToList();
        Assert.True(selfDescribing.Count == 0,
            "these rows point their description at their own title: " + string.Join(", ", selfDescribing));
    }

    // ── The two sources are one list ─────────────────────────────────────────

    /// <summary>
    ///     Every <c>FireRowTitle</c> in the modal's content panes is in
    ///     <see cref="GameplaySettingsCatalog" /> with the SAME description key,
    ///     and every catalogue entry is in the markup. Both directions matter:
    ///     one way catches a row you cannot search for, the other a search
    ///     result that leads to a control that no longer exists.
    /// </summary>
    [Fact]
    public void EveryRowInTheModal_IsInTheCatalogue_AndViceVersa()
    {
        var markup = MarkupRows();
        Assert.True(markup.Count >= 40,
            $"only {markup.Count} localized rows found under {OverlayName} — the walk is not seeing the modal");

        var catalogue = GameplaySettingsCatalog.All
            .ToDictionary(e => e.TitleKey, e => e.DescriptionKey, StringComparer.Ordinal);

        var notInCatalogue = markup.Keys.Where(k => !catalogue.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(notInCatalogue.Count == 0,
            "rows in PlayModeView.axaml that GameplaySettingsCatalog does not know about — " +
            "they cannot be found by search:\n" + string.Join("\n", notInCatalogue));

        var notInMarkup = catalogue.Keys.Where(k => !markup.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(notInMarkup.Count == 0,
            "catalogue entries with no row in PlayModeView.axaml — searching for one lands the user " +
            "on a section that does not contain it:\n" + string.Join("\n", notInMarkup));

        var mismatched = markup
            .Where(kv => catalogue[kv.Key] != kv.Value)
            .Select(kv => $"{kv.Key}: markup says '{kv.Value}', catalogue says '{catalogue[kv.Key]}'")
            .ToList();
        Assert.True(mismatched.Count == 0, string.Join("\n", mismatched));
    }

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>Search reads the REAL strings, matches on the title, the
    /// explanation AND the section name, and finds nothing for a term that is in
    /// none of them.</summary>
    [Fact]
    public void Search_MatchesTheRealCatalogue()
    {
        var english = ResxValues("Resources.resx");
        string Resolve(string key) => english.GetValueOrDefault(key, string.Empty);

        // A title word.
        var byTitle = GameplaySettingsCatalog.Search("Master volume", Resolve);
        Assert.Contains(byTitle, h => h.Entry.TitleKey == "PlayMode_GameSettings_MasterVolume");
        Assert.Equal(GameplaySettingsSectionId.Audio, byTitle[0].Entry.Section);

        // A word that appears ONLY in an explanation — the thing a label-only
        // search cannot find, and the reason descriptions are matched too.
        var byDescription = GameplaySettingsCatalog.Search("clipping", Resolve);
        Assert.Contains(byDescription, h => h.Entry.TitleKey == "PlayMode_GS_InputLevel");

        // A section name.
        var bySection = GameplaySettingsCatalog.Search("Keybinds", Resolve);
        Assert.NotEmpty(bySection);
        Assert.All(bySection, h => Assert.Equal(GameplaySettingsSectionId.Keybinds, h.Entry.Section));

        // Case-insensitive.
        Assert.NotEmpty(GameplaySettingsCatalog.Search("BUFFER", Resolve));

        // Every hit carries the strings the results list binds, so a row can
        // never render blank.
        foreach (var hit in GameplaySettingsCatalog.Search("e", Resolve))
        {
            Assert.False(string.IsNullOrWhiteSpace(hit.Title));
            Assert.False(string.IsNullOrWhiteSpace(hit.Description));
            Assert.False(string.IsNullOrWhiteSpace(hit.SectionName));
        }
    }

    [Fact]
    public void Search_ForATermInNoRow_FindsNothing()
    {
        var english = ResxValues("Resources.resx");
        string Resolve(string key) => english.GetValueOrDefault(key, string.Empty);

        Assert.Empty(GameplaySettingsCatalog.Search("qzxjvw", Resolve));

        // "No query" is the un-searched screen, NOT "everything" — the pane
        // gating reads `IsSearching` off exactly this.
        Assert.Empty(GameplaySettingsCatalog.Search("", Resolve));
        Assert.Empty(GameplaySettingsCatalog.Search("   ", Resolve));
        Assert.Empty(GameplaySettingsCatalog.Search(null, Resolve));
    }

    // ── The scrim ────────────────────────────────────────────────────────────

    /// <summary>
    ///     The modal dims the screen through the SHARED scrim class and never
    ///     hand-rolls its own backdrop.
    ///     <para>
    ///         This test used to assert the scrim was OPAQUE, and passed
    ///         VACUOUSLY after <c>aa5e587b</c> reversed that rule: it checked the
    ///         markup for an inline translucent <c>Background</c>, and the modal
    ///         has none because the fill comes from the CLASS — whose token has
    ///         been translucent ever since. Its failure message said
    ///         "Border.FireOverlayScrim, whose fill is opaque by construction",
    ///         which was simply untrue.
    ///     </para>
    ///     <para>
    ///         The alpha band is <see cref="FireTokenValueTests" />'s job, in the
    ///         one place the token is declared. What is worth asserting HERE is
    ///         what is local to this modal: it wears the shared class, so a change
    ///         to the app's scrim reaches it, and it does not paint a competing
    ///         backdrop of its own.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheModal_DimsThroughTheSharedScrimClass()
    {
        var overlay = Overlay();

        Assert.Contains(overlay.Descendants(),
            e => e.Name.LocalName == "Border"
                 && (e.Attribute("Classes")?.Value.Split(' ') ?? []).Contains("FireOverlayScrim"));

        // No hand-rolled backdrop beside it: a second scrim brush painted inline
        // would double the dimming and stop tracking the shared token.
        var ownBackdrop = overlay.DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "Background")
            .Select(a => a.Value)
            .Where(v => v.Contains("Scrim", StringComparison.OrdinalIgnoreCase) ||
                        v.Contains("FireOverlayFill", StringComparison.Ordinal))
            .ToList();
        Assert.True(ownBackdrop.Count == 0,
            "the settings modal paints its own backdrop (" + string.Join(", ", ownBackdrop) +
            ") beside Border.FireOverlayScrim — that double-dims it and stops it tracking the shared token.");
    }

    // ── Shape ────────────────────────────────────────────────────────────────

    /// <summary>The three regions of the mockup, at its measurements: a 230 px
    /// nav, the content pane, a footer — inside a 1020 × 660 card that does not
    /// reflow.</summary>
    [Fact]
    public void TheModal_IsAThreeRegionCardAtTheMockupsSize()
    {
        var card = Overlay().Descendants().Single(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "GameplaySettingsOverlayCard"));

        Assert.Equal("1020", card.Attribute("Width")?.Value);
        Assert.Equal("660", card.Attribute("Height")?.Value);
        Assert.Contains("FireScreen", card.Attribute("Classes")?.Value ?? "");

        var columns = card.Elements().First(e => e.Name.LocalName == "Grid");
        Assert.Equal("230,*", columns.Attribute("ColumnDefinitions")?.Value);

        // The rail and the footer do not scroll; only the content does.
        Assert.Contains(card.Descendants(), e => e.Name.LocalName == "Border"
                                                 && (e.Attribute("Classes")?.Value ?? "").Contains("FireRail"));
        Assert.Contains(card.Descendants(), e => e.Name.LocalName == "Border"
                                                 && (e.Attribute("Classes")?.Value ?? "").Contains("FireFooter"));
        Assert.DoesNotContain(
            card.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"),
            sv => sv.Ancestors().Any(a => (a.Attribute("Classes")?.Value ?? "").Contains("FireRail")
                                          || (a.Attribute("Classes")?.Value ?? "").Contains("FireFooter")));
    }

    /// <summary>Every nav item carries a description — that is the mockup's
    /// stated novelty for this screen — and the rail is a single-select
    /// RadioButton group, so clicking the active row cannot deselect it.</summary>
    [Fact]
    public void EveryNavRow_CarriesADescription_AndIsSingleSelect()
    {
        var nav = Overlay().Descendants().Single(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "GameSettingsNav"));

        var rows = nav.Elements().ToList();
        Assert.Equal(GameplaySettingsCatalog.Sections.Count, rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            Assert.Equal("RadioButton", row.Name.LocalName);
            Assert.Contains("FireNavRow", row.Attribute("Classes")?.Value ?? "");
            Assert.Equal("GameSettingsSections", row.Attribute("GroupName")?.Value);
            // A string parameter, never a literal int: a RelayCommand<int> fed a
            // literal CommandParameter throws while the view lays out.
            Assert.Equal(i.ToString(), row.Attribute("CommandParameter")?.Value);
            Assert.Contains("ShowGameplaySettingsSectionCommand", row.Attribute("Command")?.Value ?? "");

            var section = GameplaySettingsCatalog.Sections[i];
            var texts = row.Descendants()
                .Where(e => e.Name.LocalName == "TextBlock")
                .Select(e => LocKeyOf(e.Attribute("Text")?.Value))
                .Where(k => k != null)
                .ToList();
            Assert.Contains(GameplaySettingsCatalog.SectionTitleKey(section), texts);
            Assert.Contains(GameplaySettingsCatalog.SectionDescriptionKey(section), texts);
        }
    }

    /// <summary>A slider's numeric readout column is FIXED so the figures line
    /// up down a tab — §6.5 rule 5, and an <c>Auto</c> column fails it while
    /// every per-element bounds check still passes.</summary>
    [Fact]
    public void EverySliderRow_UsesTheSameFixedReadoutColumn()
    {
        var sliderRows = Overlay().Descendants()
            .Where(e => e.Name.LocalName == "Grid")
            .Where(g => g.Elements().Any(c => c.Name.LocalName is "Slider"
                                              or "FireLevelMeter"))
            .ToList();

        Assert.True(sliderRows.Count >= 7,
            $"only {sliderRows.Count} slider/meter rows found — the walk is not seeing the modal");

        Assert.All(sliderRows, row => Assert.Equal("200,18,*,18,64",
            row.Attribute("ColumnDefinitions")?.Value));

        // …and every one of them puts a mono value cell in that column.
        Assert.All(sliderRows, row => Assert.Contains(row.Elements(),
            e => e.Name.LocalName == "TextBlock"
                 && (e.Attribute("Classes")?.Value ?? "").Contains("FireValueCell")
                 && e.Attribute("Grid.Column")?.Value == "4"));
    }

    /// <summary>The control column is fixed too, so every non-slider row shares
    /// one right edge — the same rule the editor's Settings page keeps with its
    /// 300 px column.</summary>
    [Fact]
    public void EveryControlRow_SharesOneRightEdge()
    {
        var rows = Overlay().Descendants()
            .Where(IsRowGrid)
            .Where(g => !g.Elements().Any(c => c.Name.LocalName is "Slider" or "FireLevelMeter"))
            .Where(g => !IsUnder(g, "GameSettingsNav") && !IsUnder(g, "GameSettingsResultsPane"))
            .ToList();

        Assert.True(rows.Count >= 30, $"only {rows.Count} control rows found — the walk is not seeing the modal");
        Assert.All(rows, row => Assert.Equal("*,20,168", row.Attribute("ColumnDefinitions")?.Value));
    }

    // ── Prewarm ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The modal is ONE gated root with seven gated bodies inside it, so
    ///     <c>HiddenSubtreePrewarmer.HiddenChildrenOf</c> reaches the root only —
    ///     each pane would otherwise pay its whole first layout on the user's
    ///     first section switch. The list is looked up by <c>x:Name</c>, so a
    ///     rename in the markup is a silent no-op; this cross-checks both
    ///     directions.
    /// </summary>
    [Fact]
    public void EveryGatedPane_IsExposedForPrewarm()
    {
        var declared = DotCharter.UI.Views.PlayMode.PlayModeView.GameplaySettingsPaneNames;
        var inMarkup = Overlay().Descendants()
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value)
            .Where(n => n != null && n.EndsWith("Pane", StringComparison.Ordinal))
            .Select(n => n!)
            .ToList();

        Assert.Equal(inMarkup.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            declared.OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.Equal(GameplaySettingsCatalog.Sections.Count + 1, declared.Length);
    }

    // ── Pure maps ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void CountInMap_RoundTrips(int index, int beats)
    {
        Assert.Equal(beats, PlayModeSettingsViewModel.GameplayCountInIndexToBeats(index));
        Assert.Equal(index, PlayModeSettingsViewModel.GameplayCountInBeatsToIndex(beats));
    }

    /// <summary>
    ///     The editor's Settings page and this modal write ONE
    ///     <c>AppConfig.GameplayCountInBeats</c>. While each owned an inline
    ///     switch, this one had four rows and that one three — so a 2-beat
    ///     count-in set here was rewritten to OFF the next time Settings was
    ///     saved. Source lint, because the failure is a silent data change.
    /// </summary>
    [Fact]
    public void BothCountInSurfaces_UseTheOneSharedMap()
    {
        var settingsVm = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Presentation", "Screens", "Settings", "PlaySettingsViewModel.cs"));

        Assert.Contains("GameplayCountInRows.BeatsToIndex(config.GameplayCountInBeats)", settingsVm);
        Assert.Contains("GameplayCountInRows.IndexToBeats(GameplayCountInIndex)", settingsVm);
        Assert.DoesNotContain("GameplayCountInBeats switch", settingsVm);

        // The modal reaches the same map, not a copy of it.
        var modal = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode", "PlayModeSettingsViewModel.cs")));
        Assert.Contains("GameplayCountInRows.IndexToBeats(index)", modal);
        Assert.Contains("GameplayCountInRows.BeatsToIndex(beats)", modal);

        // …and the editor's picker offers the same four rows the map produces.
        var tab = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "PlaySettingsView.axaml")));
        foreach (var key in new[]
                 {
                     "Settings_GameplayCountIn_Off", "Settings_GameplayCountIn_2",
                     "Settings_GameplayCountIn_4", "Settings_GameplayCountIn_8"
                 })
        {
            Assert.Contains(key, tab);
        }
    }

    /// <summary>A zero capture peak is SILENCE, not 0 dB. Getting that backwards
    /// lights every bar on the meter red for a dead input.</summary>
    [Fact]
    public void PeakToDb_MapsSilenceAndFullScale()
    {
        Assert.True(double.IsNegativeInfinity(PlayModeSettingsViewModel.PeakToDb(0f)));
        Assert.Equal(0.0, PlayModeSettingsViewModel.PeakToDb(1f), 6);
        Assert.Equal(-6.0206, PlayModeSettingsViewModel.PeakToDb(0.5f), 3);
        Assert.True(PlayModeSettingsViewModel.PeakToDb(0.0001f) < PlayModeSettingsViewModel.InputMeterFloorDb);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Title key → description key, for every localized row in the
    /// modal's CONTENT panes (the nav rail and the bound results template are
    /// not rows).</summary>
    private static Dictionary<string, string> MarkupRows()
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var title in Overlay().Descendants()
                     .Where(e => e.Name.LocalName == "TextBlock")
                     .Where(e => (e.Attribute("Classes")?.Value.Split(' ') ?? []).Contains("FireRowTitle")))
        {
            if (IsUnder(title, "GameSettingsNav") || IsUnder(title, "GameSettingsResultsPane")) continue;
            if (LocKeyOf(title.Attribute("Text")?.Value) is not { } titleKey) continue;

            var description = title.Parent?.Elements()
                .FirstOrDefault(IsRowDescription);
            Assert.True(description != null,
                $"row '{titleKey}' has no FireRowDesc beside its title");

            var descriptionKey = LocKeyOf(description!.Attribute("Text")?.Value);
            Assert.True(descriptionKey != null,
                $"row '{titleKey}' has a description that is not a localized string");

            rows[titleKey] = descriptionKey!;
        }

        return rows;
    }

    /// <summary>A ROW grid: one whose own child stack carries the title/
    /// description pair. Deliberately not "any Grid with a description
    /// somewhere under it" — that also matches every container the rows sit
    /// in, and a container has no control column to line up.</summary>
    private static bool IsRowGrid(XElement e) =>
        e.Name.LocalName == "Grid"
        && e.Elements().Any(c => c.Name.LocalName == "StackPanel" && c.Elements().Any(IsRowDescription));

    private static bool IsRowDescription(XElement e) =>
        e.Name.LocalName == "TextBlock"
        && (e.Attribute("Classes")?.Value.Split(' ') ?? []).Contains("FireRowDesc");

    private static bool IsUnder(XElement element, string namedAncestor) =>
        element.Ancestors().Any(a =>
            a.Attributes().Any(attr => attr.Name.LocalName == "Name" && attr.Value == namedAncestor));

    private static string? LocKeyOf(string? attributeValue)
    {
        if (attributeValue == null) return null;
        var match = LocKey.Match(attributeValue);
        return match.Success ? match.Groups["key"].Value : null;
    }

    private static XElement Overlay()
    {
        var root = PlayModeMarkup.Document().Descendants().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == OverlayName));
        Assert.True(root != null,
            $"'{OverlayName}' is not in the Play Mode markup — a renamed root silently skips every guard here");
        return root!;
    }

    private static Dictionary<string, string> ResxValues(string fileName)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName);
        return XDocument.Load(path).Root!.Elements("data")
            .Where(d => d.Attribute("name") != null)
            .ToDictionary(d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
    }
}
