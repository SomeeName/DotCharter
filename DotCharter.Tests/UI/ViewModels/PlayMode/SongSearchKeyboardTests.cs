using Avalonia.Input;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The ✕ inside Play Mode's song-search field ran
///     <c>ClearSongFiltersCommand</c> — it emptied the query AND reset all NINE
///     filter controls (arrangement, played, tuning, capo, length, difficulty,
///     ownership, has-update). Its tooltip and its automation name both said
///     "clear search", so the label was right and the wiring was wrong, which is
///     the version of this bug nobody reports as a bug: the list simply came back
///     wider than the user left it and the tuning they had picked was gone.
///     <para>
///         "Clear filters" is still a control of its own on the browse bar, and
///         the no-match panel's escape hatch is a third — both keep
///         <c>ClearSongFiltersCommand</c>, and the second half of this file pins
///         that they do.
///     </para>
///     <para>
///         The keyboard flow that shipped with it (Esc empties the box, Ctrl+F
///         focuses it, Down hands the list the keyboard) lives in the two views'
///         code-behinds over the pure rules in <see cref="SearchBoxKeys" />. The
///         rules are asserted directly; the WIRING is a source lint, because
///         driving real key events needs the headless app session — which is
///         exactly what the default gate does not boot.
///     </para>
/// </summary>
public sealed class SongSearchKeyboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public SongSearchKeyboardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private PlayModeViewModel NewVm()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), localization: FakeLocalization.English());
    }

    /// <summary>Every filter control moved off its default — a selection into
    /// every facet plus the two specials — so a stray reset shows up as a value
    /// going back to empty/0/false rather than as "nothing happened".</summary>
    private static void NarrowEverything(PlayModeViewModel vm)
    {
        // Straight into the selection sets: the library is empty here so the
        // facets have no OPTIONS, but the criteria read `Selected` by
        // reference, which is exactly what a stray reset would clear.
        foreach (var facet in vm.SongSelect.SongFacets) facet.Selected.Add("x");
        vm.SongSelect.RecordFilter = 2;
    }

    private static void AssertStillNarrowed(PlayModeViewModel vm)
    {
        Assert.All(vm.SongSelect.SongFacets, facet => Assert.True(facet.IsActive));
        Assert.Equal(2, vm.SongSelect.RecordFilter);
    }

    // ── the command itself ──────────────────────────────────────────────────

    [Fact]
    public void ClearSearchText_EmptiesTheQuery_AndLeavesEveryOtherFilterAlone()
    {
        var vm = NewVm();
        vm.SongSelect.SearchText = "metallica";
        NarrowEverything(vm);

        vm.SongSelect.ClearSearchTextCommand.Execute(null);

        Assert.Equal("", vm.SongSelect.SearchText);
        AssertStillNarrowed(vm);
    }

    /// <summary>Clearing the text is the same gesture as deleting it by hand, so it
    /// goes through the SAME coalesced pass a keystroke does — one pass, posted,
    /// not an extra immediate filter of the whole library on top of it.</summary>
    [Fact]
    public void ClearSearchText_RequestsTheCoalescedPass_ItDoesNotRunASecondOne()
    {
        var vm = NewVm();
        vm.SongSelect.SearchText = "metallica";
        // A discrete filter gesture drains whatever the typing above queued, so
        // the counters below start from a known, non-pending state.
        NarrowEverything(vm);
        Assert.False(vm.SongSelect.FilterPass.IsPending);

        var passes = vm.SongSelect.FilterPass.PassCount;
        vm.SongSelect.ClearSearchTextCommand.Execute(null);

        Assert.True(vm.SongSelect.FilterPass.IsPending);
        Assert.Equal(passes, vm.SongSelect.FilterPass.PassCount);
    }

    /// <summary>…and the escape hatch it was mistaken for still resets everything.
    /// This is the half that must NOT change: the browse bar's "Clear filters" and
    /// the no-match panel both run it.</summary>
    [Fact]
    public void ClearSongFilters_StillResetsTheSearchAndEveryFilter()
    {
        var vm = NewVm();
        vm.SongSelect.SearchText = "metallica";
        NarrowEverything(vm);

        vm.SongSelect.ClearSongFiltersCommand.Execute(null);

        Assert.Equal("", vm.SongSelect.SearchText);
        Assert.All(vm.SongSelect.SongFacets, facet => Assert.False(facet.IsActive));
        Assert.Equal(0, vm.SongSelect.RecordFilter);
    }

    /// <summary>A chip's ✕ resets EXACTLY the value it stands for and leaves the
    /// rest of the row alone — the same promise the search ✕ broke above, one
    /// filter at a time. A facet chip deselects its ONE VALUE (the rest of that
    /// facet's selection stays); a missing <c>case</c> in the removal switch is
    /// silent — the command runs, the list does not change, and the chip comes
    /// straight back on the next pass.</summary>
    [Fact]
    public void RemoveSongFilter_ResetsExactlyTheOneValueItsChipStandsFor()
    {
        var vm = NewVm();
        vm.SongSelect.SearchText = "metallica";
        NarrowEverything(vm);
        vm.SongSelect.GenreFacet.Selected.Add("y"); // a second value on one facet

        vm.SongSelect.RemoveSongFilterCommand.Execute(new BrowserFilterChip("record", "", "chip"));
        Assert.Equal(0, vm.SongSelect.RecordFilter);
        Assert.True(vm.SongSelect.OriginFacet.IsActive);
        Assert.True(vm.SongSelect.GenreFacet.IsActive);

        vm.SongSelect.RemoveSongFilterCommand.Execute(new BrowserFilterChip("origin", "x", "chip"));
        Assert.False(vm.SongSelect.OriginFacet.IsActive);
        Assert.True(vm.SongSelect.GenreFacet.IsActive);

        // The facet chip removes ITS value only — the sibling selection stays.
        vm.SongSelect.RemoveSongFilterCommand.Execute(new BrowserFilterChip("genre", "x", "chip"));
        Assert.True(vm.SongSelect.GenreFacet.IsActive);
        Assert.Equal(["y"], vm.SongSelect.GenreFacet.Selected);

        vm.SongSelect.RemoveSongFilterCommand.Execute(new BrowserFilterChip("genre", "y", "chip"));
        Assert.False(vm.SongSelect.GenreFacet.IsActive);

        // …and nothing else moved.
        Assert.Equal("metallica", vm.SongSelect.SearchText);
        Assert.True(vm.SongSelect.PathFacet.IsActive);
        Assert.True(vm.SongSelect.PlayedFacet.IsActive);
        Assert.True(vm.SongSelect.DifficultyFacet.IsActive);
    }

    /// <summary>The rows the two commands leave behind differ, which is the whole
    /// user-visible point: clearing the text keeps the narrowing, clearing the
    /// filters gives the library back.</summary>
    [Fact]
    public void TheTwoClearAffordances_LeaveDifferentLists()
    {
        var lead = Song("Lead Song", routeMask: 1);
        var bass = Song("Bass Song", routeMask: 4);

        var textOnly = NewVm();
        textOnly.SongSelect.SetLibraryForTest([lead, bass]);
        textOnly.SongSelect.PathFacet.Options.Single(o => o.Value == "Lead").IsChecked = true; // Lead only
        textOnly.SongSelect.SearchText = "nothing matches this";
        textOnly.SongSelect.FilterPass.RunImmediately();
        Assert.Empty(textOnly.SongSelect.SongList);

        textOnly.SongSelect.ClearSearchTextCommand.Execute(null);
        textOnly.SongSelect.FilterPass.RunImmediately();
        Assert.Equal(["Lead Song"], textOnly.SongSelect.SongList.Select(s => s.Title));

        var everything = NewVm();
        everything.SongSelect.SetLibraryForTest([lead, bass]);
        everything.SongSelect.PathFacet.Options.Single(o => o.Value == "Lead").IsChecked = true;
        everything.SongSelect.SearchText = "nothing matches this";
        everything.SongSelect.ClearSongFiltersCommand.Execute(null);
        Assert.Equal(2, everything.SongSelect.SongList.Count);
    }

    private static PlaySongItem Song(string title, int routeMask)
    {
        var entry = new LibrarySongEntry
        {
            Title = title,
            SongKey = title,
            Arrangements = [new LibraryArrangementEntry { RouteMask = routeMask, SngPath = "a.sng" }]
        };
        var item = PlaySongItem.FromPsarc(new PsarcSongListItem(entry, FakeLocalization.English()));
        item.ApplyStats(null);
        return item;
    }

    // ── the shared search-box key rules ─────────────────────────────────────

    [Fact]
    public void Escape_ClaimsTheKeyOnlyWhileTheBoxHasText()
    {
        // With text: the box empties itself and the screen stays.
        Assert.Equal(SearchBoxKeyAction.ClearText,
            SearchBoxKeys.Resolve(Key.Escape, KeyModifiers.None, hasText: true));

        // Empty: the box claims nothing, so the shell's ONE Escape chain gets it
        // and navigates back. "Esc, Esc" therefore always leaves the screen.
        Assert.Equal(SearchBoxKeyAction.None,
            SearchBoxKeys.Resolve(Key.Escape, KeyModifiers.None, hasText: false));
    }

    [Fact]
    public void Down_HandsTheListTheKeyboard_AndModifiedPressesStayNativeText()
    {
        Assert.Equal(SearchBoxKeyAction.FocusList,
            SearchBoxKeys.Resolve(Key.Down, KeyModifiers.None, hasText: true));

        // Shift+Down is a selection and Ctrl+Down is a caret move — a search box
        // is a text box first.
        Assert.Equal(SearchBoxKeyAction.None,
            SearchBoxKeys.Resolve(Key.Down, KeyModifiers.Shift, hasText: true));
        Assert.Equal(SearchBoxKeyAction.None,
            SearchBoxKeys.Resolve(Key.Escape, KeyModifiers.Control, hasText: true));
        Assert.Equal(SearchBoxKeyAction.None,
            SearchBoxKeys.Resolve(Key.Up, KeyModifiers.None, hasText: true));
    }

    [Theory]
    [InlineData(-1, 12, 0)]  // nothing selected yet → the first row
    [InlineData(4, 12, 4)]   // keep what the user already had
    [InlineData(99, 12, 0)]  // a stale index from the previous result set
    [InlineData(-1, 0, -1)]  // empty list → stay in the box
    [InlineData(3, 0, -1)]
    public void TheHandoverPicksARowOnlyWhenThereIsOne(int selected, int count, int expected)
        => Assert.Equal(expected, SearchBoxKeys.SelectionForFocusHandover(selected, count));

    [Fact]
    public void CtrlF_IsTheFocusChord_AndNothingElseIs()
    {
        Assert.True(SearchBoxKeys.IsFocusSearchChord(Key.F, KeyModifiers.Control));
        Assert.False(SearchBoxKeys.IsFocusSearchChord(Key.F, KeyModifiers.None));
        Assert.False(SearchBoxKeys.IsFocusSearchChord(Key.F, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.False(SearchBoxKeys.IsFocusSearchChord(Key.G, KeyModifiers.Control));
    }

    // ── the wiring (source lint) ────────────────────────────────────────────

    [Fact]
    public void TheClearAffordanceInsideTheSearchField_ClearsTheTextOnly()
    {
        var markup = PlayModeMarkup.Text();
        var button = ElementAround(markup, "PlayMode_SongSelect_ClearSearch", "<Button", "</Button>");

        Assert.Contains("ClearSearchTextCommand", button, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearSongFiltersCommand", button, StringComparison.Ordinal);
        // An icon-only button reaches a screen reader as `Button ""` without this,
        // and a ToolTip is announced on HOVER — the one gesture that user does not
        // have. Carry both (UI/AccessibleNameTests).
        Assert.Contains("AutomationProperties.Name", button, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip", button, StringComparison.Ordinal);
    }

    /// <summary>The two escape hatches that really do reset everything are still
    /// wired to the command the ✕ gave up: the browse bar's "Clear filters" and
    /// the no-match empty state.</summary>
    [Fact]
    public void TheTwoRealClearFiltersAffordances_KeepTheirCommand()
    {
        var markup = PlayModeMarkup.Text();
        var uses = markup.Split("ClearSongFiltersCommand").Length - 1;
        Assert.True(uses >= 2,
            $"Expected the browse-bar button and the no-match panel to keep ClearSongFiltersCommand; found {uses}.");
    }

    /// <summary>The label may not promise a filter reset any more — it is the half
    /// of this bug that was already telling the truth about what SHOULD happen.</summary>
    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    public void TheClearSearchLabel_DoesNotPromiseAFilterReset(string file)
    {
        var resx = ReadSource("DotCharter.Localization", file);
        var entry = ElementAround(resx, "\"PlayMode_SongSelect_ClearSearch\"", "<data", "</data>");

        Assert.DoesNotContain("filter", entry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Filter", entry, StringComparison.Ordinal); // German for the same claim
    }

    /// <summary>
    ///     Both song lists wire the same three keys, off the same shared rules.
    ///     A source lint because the alternative is the headless app session, which
    ///     the default gate does not boot — and because the failure this guards is
    ///     a handler that was never registered, which no amount of view-model
    ///     testing can see.
    /// </summary>
    [Theory]
    // Song Select's keys are ROUTED by PlayModeView (its tunnel handler covers the
    // whole Play Mode surface, so Ctrl+F works from anywhere on the screen) and
    // RESOLVED by SongSelectView, next to the box (#294): its code is both files.
    [InlineData("DotCharter.UI/Views/PlayMode/Screens/SongSelectView", "SongSelectSearchBox", "DotCharter.UI/Views/PlayMode/PlayModeView")]
    [InlineData("DotCharter.UI/Views/Library/LibraryView", "LibrarySearchBox", null)]
    public void BothSearchBoxes_WireEscapeDownAndCtrlF(string viewPath, string boxName, string? routerPath)
    {
        var parts = viewPath.Split('/');
        var markup = ReadSource([.. parts[..^1], parts[^1] + ".axaml"]);
        var code = ReadSource([.. parts[..^1], parts[^1] + ".axaml.cs"]);
        if (routerPath?.Split('/') is { } router)
            code += ReadSource([.. router[..^1], router[^1] + ".axaml.cs"]);

        // The box is named (so the code-behind can reach it) and marked, which is
        // what makes the window tunnel stand down for Escape.
        Assert.Contains($"x:Name=\"{boxName}\"", markup, StringComparison.Ordinal);
        Assert.Contains("input:SearchBoxKeys.ClearOnEscape=\"True\"", markup, StringComparison.Ordinal);

        // A TUNNEL KeyDown handler, or a focused TextBox keeps Ctrl+F for itself.
        Assert.Contains("AddHandler(KeyDownEvent", code, StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Tunnel", code, StringComparison.Ordinal);

        // …and all three keys go through the shared rules rather than being
        // re-decided per view.
        Assert.Contains("SearchBoxKeys.IsFocusSearchChord", code, StringComparison.Ordinal);
        Assert.Contains("SearchBoxKeys.Resolve", code, StringComparison.Ordinal);
        Assert.Contains("SearchBoxKeyAction.ClearText", code, StringComparison.Ordinal);
        Assert.Contains("SearchBoxKeyAction.FocusList", code, StringComparison.Ordinal);
        Assert.Contains("SearchBoxKeys.SelectionForFocusHandover", code, StringComparison.Ordinal);
        Assert.Contains($"{boxName}.Focus()", code, StringComparison.Ordinal);
    }

    /// <summary>The shell owns the one Escape chain, and it is claimed at the
    /// WINDOW tunnel — which runs before anything a view can add (root → leaf).
    /// So the view can only get Escape if the shell hands it over, and this is the
    /// hand-over.</summary>
    [Fact]
    public void TheWindowTunnel_StandsDownForASearchBoxThatHasText()
    {
        var gate = ReadSource("DotCharter.UI", "Views", "GlobalChordGate.cs");
        var window = ReadSource("DotCharter.UI", "Views", "MainWindow.Input.cs");

        Assert.Contains("SearchBoxClaimsEscape", gate, StringComparison.Ordinal);
        Assert.Contains("!c.SearchBoxClaimsEscape", gate, StringComparison.Ordinal);
        Assert.Contains("SearchBoxKeys.ClaimsEscape(focused)", window, StringComparison.Ordinal);
    }

    /// <summary>The element (Button / data entry) that CONTAINS a marker, sliced out
    /// so an assertion about it cannot be satisfied by a different one elsewhere in
    /// the file.</summary>
    private static string ElementAround(string text, string marker, string open, string close)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"Marker '{marker}' not found.");

        var start = text.LastIndexOf(open, at, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No '{open}' before '{marker}'.");

        var end = text.IndexOf(close, at, StringComparison.Ordinal);
        Assert.True(end > start, $"No '{close}' after '{marker}'.");

        return text[start..(end + close.Length)];
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
