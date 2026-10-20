using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig's gear LIBRARY: the player's starred units (per install, over
///     <c>AppConfig.RigFavoriteGear</c>), the Browse/Favourites tabs that filter
///     the rail by them, and the add-target lane that decides where a plain
///     gear-panel click lands when no insert gap is armed.
///     <para>
///         The favourites half asserts against the PERSISTED config and the rail's
///         rebuilt cards; the lane half asserts against the CHAIN (which slot and
///         which branch tag the engine was given), never against the picker's own
///         selection — the wrong-guitar lesson, one axis over.
///     </para>
/// </summary>
public sealed class RigGearLibraryTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("reverb", "Reverb", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("amp", "Amp", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)]),
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)])
    ];

    private static PedalboardViewModel NewRig(out FakeDspChainService dsp,
        out FakeConfigurationService config)
    {
        dsp = new FakeDspChainService(Catalog());
        config = new FakeConfigurationService();
        return TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English(), config);
    }

    private static PedalNodeViewModel Add(PedalboardViewModel rig, string id) =>
        rig.AddGear(rig.AvailableModules.First(m => m.Id == id))!;

    private static RigGearItem Row(AmpFxRigViewModel screen, string moduleTypeId) =>
        screen.GearGroups.SelectMany(g => g.Items).First(i => i.ModuleTypeId == moduleTypeId);

    // ── the pure filter ──────────────────────────────────────────────────────

    [Fact]
    public void TheFavoritesGate_IsOffUntilTheTabIsOn_AndIsNullSafe()
    {
        IReadOnlySet<string> starred = new HashSet<string>(StringComparer.Ordinal) { "delay" };

        // Off: the gate matches everything, starred or not.
        Assert.True(GearRailFilter.MatchesFavorites(false, starred, "overdrive"));
        Assert.True(GearRailFilter.MatchesFavorites(false, null, "overdrive"));

        // On: only the starred units pass.
        Assert.True(GearRailFilter.MatchesFavorites(true, starred, "delay"));
        Assert.False(GearRailFilter.MatchesFavorites(true, starred, "overdrive"));

        // An empty / absent set on the Favourites tab passes nothing, and no input
        // is allowed to throw — a fresh install is exactly this state.
        Assert.False(GearRailFilter.MatchesFavorites(true, null, "delay"));
        Assert.False(GearRailFilter.MatchesFavorites(true, new HashSet<string>(), "delay"));
        Assert.False(GearRailFilter.IsFavorite(starred, null));
        Assert.False(GearRailFilter.IsFavorite(starred, string.Empty));
        Assert.False(GearRailFilter.IsFavorite(null, "delay"));

        // Ordinal: a catalogue id is an identifier, not prose.
        Assert.False(GearRailFilter.IsFavorite(starred, "Delay"));
    }

    // ── the rail ─────────────────────────────────────────────────────────────

    [Fact]
    public void StarringAUnit_IsPersisted_AndComesBackOnTheNextRail()
    {
        var config = new FakeConfigurationService();
        var rail = new GearRailViewModel(Catalog(), FakeLocalization.English(), _ => { }, config: config);

        Assert.False(rail.HasFavorites);
        rail.ToggleFavoriteCommand.Execute("delay");

        Assert.True(rail.IsFavorite("delay"));
        Assert.True(rail.HasFavorites);
        Assert.Equal(["delay"], config.AppConfig.RigFavoriteGear);

        // A second rail over the same install starts with the star already set.
        var reopened = new GearRailViewModel(Catalog(), FakeLocalization.English(), _ => { }, config: config);
        Assert.True(reopened.IsFavorite("delay"));

        // …and un-starring writes the removal, not just the flag.
        rail.ToggleFavoriteCommand.Execute("delay");
        Assert.False(rail.IsFavorite("delay"));
        Assert.Empty(config.AppConfig.RigFavoriteGear);
    }

    [Fact]
    public void TheFavoritesTab_RebuildsTheCardsToTheStarredUnitsOnly()
    {
        var config = new FakeConfigurationService();
        var rail = new GearRailViewModel(Catalog(), FakeLocalization.English(), _ => { }, config: config);
        var all = rail.Cards.Count;
        Assert.True(all > 2);

        rail.ToggleFavoriteCommand.Execute("delay");
        rail.ToggleFavoriteCommand.Execute("amp");
        rail.ShowFavoritesOnly = true;

        Assert.Equal(["amp", "delay"], rail.Cards.Select(c => c.ModuleTypeId).OrderBy(id => id).ToArray());

        // The search still applies WITHIN the favourites.
        rail.SearchText = "delay";
        Assert.Equal(["delay"], rail.Cards.Select(c => c.ModuleTypeId).ToArray());

        // Un-starring the visible unit takes it out of the list at once.
        rail.SearchText = string.Empty;
        rail.ToggleFavoriteCommand.Execute("delay");
        Assert.Equal(["amp"], rail.Cards.Select(c => c.ModuleTypeId).ToArray());

        rail.ShowFavoritesOnly = false;
        Assert.Equal(all, rail.Cards.Count);
    }

    [Fact]
    public void ARailWithNoConfig_StillStars_ButRemembersNothing()
    {
        // A preset-only host (and every test that hands no config in) must not
        // lose the rail over a missing service.
        var rail = new GearRailViewModel(Catalog(), FakeLocalization.English(), _ => { });
        rail.ToggleFavoriteCommand.Execute("delay");
        Assert.True(rail.IsFavorite("delay"));
    }

    // ── the screen's tabs + rows ─────────────────────────────────────────────

    [Fact]
    public void TheGearRow_CarriesTheStar_AndUpdatesInPlace()
    {
        var rig = NewRig(out _, out var config);
        var screen = new AmpFxRigViewModel(rig);

        var delay = Row(screen, "delay");
        Assert.False(delay.IsFavorite);
        Assert.NotNull(delay.ToggleFavoriteCommand);

        delay.ToggleFavoriteCommand!.Execute(delay.ModuleTypeId);

        // Same row INSTANCE: starring a unit may not re-inflate the list it is
        // drawn in (the IsInRig rule).
        Assert.Same(delay, Row(screen, "delay"));
        Assert.True(delay.IsFavorite);
        Assert.True(screen.HasFavoriteGear);
        Assert.Equal(["delay"], config.AppConfig.RigFavoriteGear);
    }

    [Fact]
    public void TheLibraryTabs_DriveTheRailsFilter()
    {
        var rig = NewRig(out _, out _);
        var screen = new AmpFxRigViewModel(rig);
        Row(screen, "delay").ToggleFavoriteCommand!.Execute("delay");

        Assert.True(screen.IsGearLibraryBrowse);
        Assert.True(screen.GearLibraryTabs[0].IsActive);
        Assert.False(screen.GearLibraryTabs[1].IsActive);

        screen.ShowFavoritesCommand.Execute(null);

        Assert.Equal(RigGearLibraryTabKind.Favorites, screen.GearLibraryTab);
        Assert.True(screen.IsGearLibraryFavorites);
        Assert.True(rig.GearRail.ShowFavoritesOnly);
        Assert.True(screen.GearLibraryTabs[1].IsActive);
        Assert.False(screen.GearLibraryTabs[0].IsActive);
        Assert.Equal(["delay"],
            screen.GearGroups.SelectMany(g => g.Items).Select(i => i.ModuleTypeId).ToArray());

        screen.ShowBrowseCommand.Execute(null);

        Assert.False(rig.GearRail.ShowFavoritesOnly);
        Assert.True(screen.GearGroups.SelectMany(g => g.Items).Count() > 1);

        // The tab rows carry their own command — the strip is an ItemsControl.
        Assert.Same(screen.ShowFavoritesCommand, screen.GearLibraryTabs[1].Command);
        screen.GearLibraryTabs[1].Command.Execute(null);
        Assert.True(screen.IsGearLibraryFavorites);
    }

    // ── the add-target lane ──────────────────────────────────────────────────

    [Fact]
    public void OnlyTheLanesTheBoardRuns_AreOffered()
    {
        var rig = NewRig(out _, out _);
        var screen = new AmpFxRigViewModel(rig);
        Add(rig, "overdrive");

        // Serial: one option (the main chain), so there is no choice to offer.
        Assert.Equal([null], screen.AddTargetLanes.Select(o => o.Lane).ToArray());
        Assert.False(screen.HasAddTargetChoice);

        Add(rig, "splitter"); // auto-pairs the merger → a live two-lane region

        Assert.Equal([null, 1, 2], screen.AddTargetLanes.Select(o => o.Lane).ToArray());
        Assert.True(screen.HasAddTargetChoice);

        // Widening the region adds the third lane; narrowing takes it away again.
        rig.RoutingSplit3Command.Execute(null);
        Assert.Equal([null, 1, 2, 3], screen.AddTargetLanes.Select(o => o.Lane).ToArray());

        screen.AddTargetLanes.Single(o => o.Lane == 3).Command.Execute(null);
        Assert.Equal(3, screen.AddTargetLane);
        Assert.True(screen.AddTargetLanes.Single(o => o.Lane == 3).IsActive);

        rig.RoutingSplit2Command.Execute(null);
        Assert.Equal([null, 1, 2], screen.AddTargetLanes.Select(o => o.Lane).ToArray());
        // A target that stopped existing falls back to the main chain rather than
        // silently addressing lane 1.
        Assert.Null(screen.AddTargetLane);

        rig.RoutingSerialCommand.Execute(null);
        Assert.Equal([null], screen.AddTargetLanes.Select(o => o.Lane).ToArray());
        Assert.False(screen.HasAddTargetChoice);
    }

    [Fact]
    public void AddingWithALaneTargeted_SeatsTheUnitInsideThatLane()
    {
        var rig = NewRig(out var dsp, out _);
        var screen = new AmpFxRigViewModel(rig);
        Add(rig, "overdrive");
        Add(rig, "splitter");

        screen.AddTargetLane = 2;
        screen.AddGearCommand.Execute(Row(screen, "delay"));

        // Inside the region (before the merger) and tagged for lane B — the CHAIN
        // says so, which is the only thing the engine reads.
        Assert.Equal(["overdrive", "splitter", "delay", "merger"],
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        Assert.Equal(2, dsp.GetNodes()[2].Branch);

        var split = screen.BoardSegments.OfType<RigSplitSegment>().Single();
        Assert.Equal(["delay"], split.Lanes[1].Items.Select(n => n.ModuleTypeId).ToArray());
        Assert.Empty(split.Lanes[0].Items);
    }

    [Fact]
    public void AnArmedInsertGap_WinsOverTheTargetLane()
    {
        var rig = NewRig(out var dsp, out _);
        var screen = new AmpFxRigViewModel(rig);
        var od = Add(rig, "overdrive");
        Add(rig, "splitter");

        screen.AddTargetLane = 2;
        screen.InsertBeforeCommand.Execute(od); // the gap the player just clicked
        screen.AddGearCommand.Execute(Row(screen, "delay"));

        // Front of the chain, i.e. the gap — not lane B.
        Assert.Equal(["delay", "overdrive", "splitter", "merger"],
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        Assert.Equal(0, dsp.GetNodes()[0].Branch);
    }

    [Fact]
    public void WithNoLaneTargeted_AnAddStillAppends()
    {
        var rig = NewRig(out var dsp, out _);
        var screen = new AmpFxRigViewModel(rig);
        Add(rig, "overdrive");

        Assert.Null(screen.AddTargetLane);
        screen.AddGearCommand.Execute(Row(screen, "amp"));

        Assert.Equal(["overdrive", "amp"], dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
    }
}
