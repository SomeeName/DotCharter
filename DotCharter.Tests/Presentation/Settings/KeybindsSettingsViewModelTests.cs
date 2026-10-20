using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Input;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Keybinds as a screen: every chord of <see cref="KeybindConfig" />
///     has a row, a rebind counts as one unsaved change, Save persists and
///     rebuilds the dispatch, and — the bug the old tab shipped — a REPLACED
///     table is announced to every row, because Revert and a backup import
///     assign a fresh <see cref="KeybindConfig" /> rather than mutate the old one.
/// </summary>
public sealed class KeybindsSettingsViewModelTests
{
    private sealed class RecordingDispatch : IKeybindDispatch
    {
        public int Refreshes { get; private set; }

        public void RefreshBinds() => Refreshes++;

        public string? DescribeBinding(KeyChord chord) => null;
    }

    private static (KeybindsSettingsViewModel Vm, ProjectState State, FakeConfigurationService Cfg, RecordingDispatch Dispatch) Build()
    {
        var cfg = new FakeConfigurationService();
        var state = new ProjectState { Keybinds = cfg.Keybinds };
        var dispatch = new RecordingDispatch();
        var vm = new KeybindsSettingsViewModel(cfg, state, dispatch, FakeLocalization.English(), new FakeMidiTriggerInput());
        return (vm, state, cfg, dispatch);
    }

    private static IKeybindRow Row(KeybindsSettingsViewModel vm, string property) =>
        vm.Groups.SelectMany(g => g.Rows).Cast<KeybindRowViewModel>().Single(r => r.Property == property);

    [Fact]
    public void EveryChordOfTheConfig_HasExactlyOneRow()
    {
        var (vm, _, _, _) = Build();
        var rows = vm.Groups.SelectMany(g => g.Rows).Cast<KeybindRowViewModel>().Select(r => r.Property).ToList();
        var chords = typeof(KeybindConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(KeyChord) && p.CanRead && p.CanWrite)
            .Select(p => p.Name).ToList();

        Assert.Equal(chords.Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));
        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());
        Assert.True(rows.Count > 100, $"only {rows.Count} rows — the catalogue went blind");
    }

    [Fact]
    public void ItIsTheKeybindsTab_AndAScreen()
    {
        var (vm, _, _, _) = Build();
        Assert.Equal(SettingsTabKeys.Keybinds, vm.Key);
        Assert.Equal(typeof(IKeybindsSettingsScreen), vm.Screen);
        Assert.Empty(ContractConformanceTests.Violations(typeof(IKeybindsSettingsScreen), vm));
    }

    [Fact]
    public void ARebind_CountsAsOneUnsavedChange_AndFlippingItBackReadsZero()
    {
        var (vm, _, _, _) = Build();
        var row = Row(vm, nameof(KeybindConfig.PlayPause));
        var original = row.Chord;

        row.Chord = new KeyChord(Key.F8);
        Assert.Equal(["keybinds.PlayPause"], vm.UnsavedSettings);

        row.Chord = original;
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void Save_PersistsTheLiveTable_RebuildsTheDispatch_AndReadsZero()
    {
        var (vm, state, cfg, dispatch) = Build();
        Row(vm, nameof(KeybindConfig.PlayPause)).Chord = new KeyChord(Key.F8);

        vm.Save();

        Assert.Same(state.Keybinds, cfg.Keybinds);
        Assert.Equal(new KeyChord(Key.F8), cfg.Keybinds.PlayPause);
        Assert.Equal(1, dispatch.Refreshes);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void Revert_ReplacesTheTableFromDisk_AndAnnouncesEveryRow()
    {
        var (vm, state, cfg, dispatch) = Build();
        var row = Row(vm, nameof(KeybindConfig.PlayPause));
        var announced = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IKeybindRow.Chord)) announced++;
        };

        // What the user reverts away FROM, and what is on disk.
        row.Chord = new KeyChord(Key.F5);
        cfg.Keybinds = new KeybindConfig { PlayPause = new KeyChord(Key.Space) };

        vm.Revert();

        Assert.True(announced > 0, "Revert replaced ProjectState.Keybinds without announcing it — every row is still bound to the discarded table.");
        Assert.Same(cfg.Keybinds, state.Keybinds);
        Assert.Equal(new KeyChord(Key.Space), row.Chord);
        Assert.Equal(1, dispatch.Refreshes);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void AReplacementFromElsewhere_IsAnnouncedToo_AndEditsReachTheNewTable()
    {
        var (vm, state, _, _) = Build();
        var row = Row(vm, nameof(KeybindConfig.PlayPause));
        var announced = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IKeybindRow.Chord)) announced++;
        };

        var replacement = new KeybindConfig { PlayPause = new KeyChord(Key.F9) };
        state.Keybinds = replacement;

        Assert.Equal(1, announced);
        Assert.Equal(new KeyChord(Key.F9), row.Chord);

        row.Chord = new KeyChord(Key.F10);
        Assert.Equal(new KeyChord(Key.F10), replacement.PlayPause);
    }

    [Fact]
    public void EditingAChord_AnnouncesOnlyThatRow()
    {
        var (vm, _, _, _) = Build();
        var edited = Row(vm, nameof(KeybindConfig.PlayPause));
        var other = Row(vm, nameof(KeybindConfig.Undo));
        var otherAnnounced = 0;
        other.PropertyChanged += (_, _) => otherAnnounced++;

        edited.Chord = new KeyChord(Key.F7);

        Assert.Equal(0, otherAnnounced);
    }

    [Fact]
    public void ResetToDefaults_PutsEveryChordBack_AndRefreshesTheDispatch_ButDoesNotSave()
    {
        var (vm, state, cfg, dispatch) = Build();
        var row = Row(vm, nameof(KeybindConfig.PlayPause));
        row.Chord = new KeyChord(Key.F8);
        var saved = cfg.Keybinds;

        vm.ResetToDefaultsCommand.Execute(null);

        Assert.Equal(new KeyChord(Key.Space), row.Chord);
        Assert.Equal(1, dispatch.Refreshes);
        Assert.Same(saved, cfg.Keybinds);
        Assert.Same(saved, state.Keybinds);
    }

    [Fact]
    public void Load_RetakesTheBaseline_SoAnEditMadeBeforeItReadsSaved()
    {
        var (vm, _, _, _) = Build();
        Row(vm, nameof(KeybindConfig.PlayPause)).Chord = new KeyChord(Key.F8);
        Assert.Single(vm.UnsavedSettings);

        vm.Load();

        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void TheSearchGroups_AreTheIntroCardTheMidiModeAndEveryGroup_WithLocalizedLabels()
    {
        var (vm, _, _, _) = Build();
        var groups = vm.SearchGroups;

        Assert.Equal(KeybindRowCatalog.Groups.Count + 2, groups.Count);
        Assert.Empty(groups[0].Rows);
        Assert.Equal(FakeLocalization.English()["Settings_KeybindsTab"], groups[0].Title);
        Assert.Empty(groups[1].Rows);
        Assert.Equal(FakeLocalization.English()["Keybind_Mode_Midi"], groups[1].Title);
        // One row per catalogue spec — derived, so a new keybind does not turn this red.
        Assert.Equal(KeybindRowCatalog.Rows.Count(), groups.Skip(2).Sum(g => g.Rows.Count));
        Assert.All(groups.Skip(2).SelectMany(g => g.Rows), r =>
        {
            Assert.StartsWith("keybinds.", r.Key, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(r.Label));
            Assert.DoesNotContain("Keybind_", r.Label, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ApplySearch_HidesTheRowsThatDoNotMatch_AndTheGroupsLeftEmpty()
    {
        var (vm, _, _, _) = Build();
        var playPause = Row(vm, nameof(KeybindConfig.PlayPause));
        var undo = Row(vm, nameof(KeybindConfig.Undo));
        var transport = vm.Groups.Single(g => g.Rows.Contains(playPause));
        var editing = vm.Groups.Single(g => g.Rows.Contains(undo));

        vm.ApplySearch(playPause.Label);
        Assert.True(playPause.IsVisible);
        Assert.False(undo.IsVisible);
        Assert.True(transport.IsVisible);
        Assert.False(editing.IsVisible);

        // A group-title match shows the whole group.
        vm.ApplySearch(editing.Title);
        Assert.True(undo.IsVisible);
        Assert.True(editing.IsVisible);
        Assert.All(editing.Rows, r => Assert.True(r.IsVisible));

        vm.ApplySearch("");
        Assert.All(vm.Groups, g => Assert.True(g.IsVisible));
        Assert.All(vm.Groups.SelectMany(g => g.Rows), r => Assert.True(r.IsVisible));
    }

    [Fact]
    public void Dispose_StopsFollowingTheState()
    {
        var (vm, state, _, _) = Build();
        var row = Row(vm, nameof(KeybindConfig.PlayPause));
        var announced = 0;
        row.PropertyChanged += (_, _) => announced++;

        vm.Dispose();
        state.Keybinds = new KeybindConfig();

        Assert.Equal(0, announced);
    }

    /// <summary>Play Mode's gameplay-settings overlay carries the identical
    /// shape (<c>GameplayKeybinds =&gt; _projectState.Keybinds</c> on
    /// <c>PlayModeSettingsViewModel</c>, bound by ten capture boxes in
    /// `GameSettingsKeybindsView`). It re-announces at the moment the
    /// section opens, which is the only time the overlay reads it.</summary>
    [Fact]
    public void ThePlayModeTwin_ReAnnouncesItsTableWhenTheSectionOpens()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "ViewModels", "PlayMode", "PlayModeSettingsViewModel.cs")));
        var body = MethodBody(source, "private void WatchKeybinds(bool watch)");

        Assert.Matches(new Regex(@"OnPropertyChanged\(nameof\(GameplayKeybinds\)\)"), body);
    }

    private static string MethodBody(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"`{signature}` no longer exists — repoint this test.");
        var open = source.IndexOf('{', at + signature.Length);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after `{signature}`.");
    }
}
