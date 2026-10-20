using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Navigation;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     The settings shell over fake tabs: the rail follows <see cref="SettingsRail" />,
///     the footer's verbs route to every tab (Reset to the selected one), the
///     unsaved count sums every change source, the session opens and closes
///     the page, and the search is one coalesced pass over what the tabs declare.
/// </summary>
public sealed class SettingsShellViewModelTests
{
    private sealed class FakeTab(string key, Type? screen = null) : ObservableObject, ISettingsTab, ISettingsChangeSource, IScreenLifecycle
    {
        private List<string> _unsaved = [];

        public List<string> Log { get; } = [];

        public string? LastQuery { get; private set; }

        public string Key => key;

        public Type? Screen => screen;

        public IReadOnlyList<SettingsSearchGroup> SearchGroups { get; set; } = [];

        public IReadOnlyList<string> UnsavedSettings => _unsaved;

        public void SetUnsaved(params string[] names)
        {
            _unsaved = [.. names];
            OnPropertyChanged(nameof(UnsavedSettings));
        }

        public void Rebaseline()
        {
            Log.Add("rebaseline");
            _unsaved = [];
            OnPropertyChanged(nameof(UnsavedSettings));
        }

        public void Load() => Log.Add("load");

        public void Save() => Log.Add("save");

        public void Revert() => Log.Add("revert");

        public void Reset() => Log.Add("reset");

        public void ApplySearch(string query) => LastQuery = query;

        public void OnShown() => Log.Add("shown");

        public void OnHidden() => Log.Add("hidden");
    }

    /// <summary>Records posts and runs them on demand, so a burst of keystrokes can be observed as ONE pass.</summary>
    private sealed class QueueDispatcher : IUiDispatcher
    {
        public Queue<Action> Posted { get; } = new();

        public bool CheckAccess() => true;

        public void Post(Action action) => Posted.Enqueue(action);

        public void PostNormal(Action action) => Posted.Enqueue(action);

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public void Drain()
        {
            while (Posted.Count > 0) Posted.Dequeue()();
        }
    }

    /// <summary>Answers every confirmation with <see cref="Answer" /> and records what it was asked.</summary>
    private sealed class ConfirmDialogs : IDialogs
    {
        public bool Answer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult> => throw new NotSupportedException();

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false)
        {
            Asked.Add(title + " | " + message);
            return Task.FromResult(isConfirmation && Answer);
        }

        public bool CloseTop() => false;
    }

    private sealed class Rig
    {
        public FakeTab General = new(SettingsTabKeys.General);
        public FakeTab Keybinds = new(SettingsTabKeys.Keybinds, typeof(IKeybindsSettingsScreen));
        public FakeTab Updates = new(SettingsTabKeys.Updates, typeof(IUpdatesSettingsScreen));
        public SettingsSession Session = new();
        public QueueDispatcher Ui = new();
        public FakeAppLogger Logger = new();
        public ConfirmDialogs Dialogs = new();

        public IEnumerable<FakeTab> Tabs => [Updates, General, Keybinds];

        public SettingsShellViewModel Shell() =>
            new(Tabs, Tabs, Session, FakeLocalization.English(), Ui, Dialogs, Logger);
    }

    private static ISettingsRailItem Item(SettingsShellViewModel shell, string key) =>
        shell.Sections.SelectMany(s => s.Items).Cast<SettingsRailItemViewModel>().Single(i => i.Tab.Key == key);

    [Fact]
    public void TheRail_FollowsTheDeclaredOrderAndSections_WhateverTheRegistrationOrder()
    {
        using var shell = new Rig().Shell();

        Assert.Equal(new[] { "Settings_Section_Workspace", "Settings_Section_Configuration" }
                .Select(k => FakeLocalization.English()[k]),
            shell.Sections.Select(s => s.Title));
        Assert.Equal([SettingsTabKeys.General, SettingsTabKeys.Keybinds, SettingsTabKeys.Updates],
            shell.Sections.SelectMany(s => s.Items).Cast<SettingsRailItemViewModel>().Select(i => i.Tab.Key));
        Assert.Equal([typeof(IKeybindsSettingsScreen), typeof(IUpdatesSettingsScreen)], shell.TabScreens);
        Assert.All(shell.Sections.SelectMany(s => s.Items), i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Title));
            Assert.False(string.IsNullOrWhiteSpace(i.Description));
            Assert.StartsWith("Icon", i.IconKey, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ATabWithNoRailRow_IsRefused()
    {
        var rig = new Rig();
        var stray = new FakeTab("mystery");
        Assert.Throws<InvalidOperationException>(() =>
            new SettingsShellViewModel([stray], [], rig.Session, FakeLocalization.English(), rig.Ui, rig.Dialogs));
    }

    [Fact]
    public void TheFirstTabIsSelected_AndSelectingAnother_FlipsTheRailAndTheScreen()
    {
        var rig = new Rig();
        using var shell = rig.Shell();

        Assert.True(Item(shell, SettingsTabKeys.General).IsSelected);
        Assert.Null(shell.CurrentScreen);
        Assert.Equal(["shown"], rig.General.Log);

        Item(shell, SettingsTabKeys.Keybinds).SelectCommand.Execute(null);

        Assert.False(Item(shell, SettingsTabKeys.General).IsSelected);
        Assert.True(Item(shell, SettingsTabKeys.Keybinds).IsSelected);
        Assert.Equal(typeof(IKeybindsSettingsScreen), shell.CurrentScreen);
        Assert.Equal(["shown", "hidden"], rig.General.Log);
        Assert.Equal(["shown"], rig.Keybinds.Log);
        Assert.Same(rig.Keybinds, shell.SelectedTab);
    }

    [Fact]
    public void ReselectingTheCurrentTab_AnnouncesItsSelection_SoTheToggleStaysChecked()
    {
        using var shell = new Rig().Shell();
        var item = Item(shell, SettingsTabKeys.General);
        var announced = 0;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ISettingsRailItem.IsSelected)) announced++;
        };

        item.SelectCommand.Execute(null);

        Assert.True(item.IsSelected);
        Assert.Equal(1, announced);
    }

    [Fact]
    public void OpeningTheSession_LoadsEveryTabOnTheNextFrame_AndTakesTheBaseline()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        rig.General.SetUnsaved("general.Language");

        rig.Session.IsOpen = true;
        Assert.DoesNotContain("load", rig.General.Log);

        rig.Ui.Drain();

        Assert.Equal(["load", "rebaseline"], rig.General.Log.Where(l => l is "load" or "rebaseline"));
        Assert.Contains("load", rig.Updates.Log);
        Assert.False(shell.HasUnsavedChanges);
    }

    /// <summary>
    ///     The shell builds the settings surface when its tab is first shown — AFTER it
    ///     set <c>IsOpen</c> — so a page built into an open session has to load on its
    ///     own, or the first visit shows whatever the tabs were constructed with.
    /// </summary>
    [Fact]
    public void APageBuiltWhileTheSessionIsOpen_LoadsOnTheNextFrame_Once()
    {
        var rig = new Rig();
        rig.Session.IsOpen = true;
        using var shell = rig.Shell();
        Assert.DoesNotContain("load", rig.General.Log);

        rig.Ui.Drain();

        Assert.Single(rig.General.Log, l => l == "load");
        Assert.Single(rig.Updates.Log, l => l == "load");
    }

    [Fact]
    public void TheUnsavedCount_SumsEverySource_AndNamesThem()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        Assert.Equal(FakeLocalization.English()["Settings_NothingUnsaved"], shell.UnsavedChangesText);

        rig.Keybinds.SetUnsaved("keybinds.PlayPause");
        Assert.True(shell.HasUnsavedChanges);
        Assert.Equal(FakeLocalization.English().Plural("Settings_UnsavedOne", "Settings_Unsaved", 1), shell.UnsavedChangesText);

        rig.Updates.SetUnsaved("updates.UpdateCheckOnStartup", "updates.GitHubClientId");
        Assert.Equal(FakeLocalization.English().Plural("Settings_UnsavedOne", "Settings_Unsaved", 3), shell.UnsavedChangesText);
        Assert.Contains(rig.Logger.Entries, e => e.Message.Contains("keybinds.PlayPause", StringComparison.Ordinal)
                                                  && e.Message.Contains("updates.GitHubClientId", StringComparison.Ordinal));

        rig.Keybinds.SetUnsaved();
        rig.Updates.SetUnsaved();
        Assert.False(shell.HasUnsavedChanges);
    }

    [Fact]
    public void Save_SavesEveryTab_RetakesTheBaseline_AndClosesThePage()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        rig.Session.IsOpen = true;
        rig.Ui.Drain();
        rig.Keybinds.SetUnsaved("keybinds.PlayPause");

        shell.SaveCommand.Execute(null);

        foreach (var tab in rig.Tabs) Assert.Contains("save", tab.Log);
        Assert.False(shell.HasUnsavedChanges);
        Assert.False(rig.Session.IsOpen);
        // The page closed itself: leaving must NOT revert what was just saved.
        rig.Ui.Drain();
        Assert.DoesNotContain("revert", rig.Keybinds.Log);
    }

    [Fact]
    public void Cancel_RevertsEveryTab_AndClosesThePage()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        rig.Session.IsOpen = true;
        rig.Ui.Drain();

        shell.CancelCommand.Execute(null);

        foreach (var tab in rig.Tabs) Assert.Contains("revert", tab.Log);
        Assert.False(rig.Session.IsOpen);
    }

    [Fact]
    public void LeavingTheTab_RevertsOnTheNextFrame_ButOnlyAfterAnOpen()
    {
        var rig = new Rig();
        using var shell = rig.Shell();

        rig.Session.IsOpen = false;
        rig.Ui.Drain();
        Assert.DoesNotContain("revert", rig.General.Log);

        rig.Session.IsOpen = true;
        rig.Ui.Drain();
        rig.Session.IsOpen = false;
        Assert.DoesNotContain("revert", rig.General.Log);
        rig.Ui.Drain();
        Assert.Contains("revert", rig.General.Log);
    }

    [Fact]
    public void Save_IsDisabled_WhileNothingIsUnsaved_AndDoesNothingIfForced()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        rig.Session.IsOpen = true;
        rig.Ui.Drain();
        Assert.False(shell.SaveCommand.CanExecute(null));

        shell.SaveCommand.Execute(null);
        Assert.DoesNotContain("save", rig.General.Log);
        Assert.True(rig.Session.IsOpen);

        rig.General.SetUnsaved("general.Language");
        Assert.True(shell.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetSection_AsksFirst_ThenResetsOnlyTheSelectedTab()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        Item(shell, SettingsTabKeys.Updates).SelectCommand.Execute(null);
        rig.Updates.SetUnsaved("updates.UpdateCheckOnStartup", "updates.GitHubClientId");
        rig.General.SetUnsaved("general.Language");

        await ((IAsyncRelayCommand)shell.ResetSectionCommand).ExecuteAsync(null);

        var asked = Assert.Single(rig.Dialogs.Asked);
        Assert.Contains(Item(shell, SettingsTabKeys.Updates).Title, asked, StringComparison.Ordinal);
        Assert.Contains("reset", rig.Updates.Log);
        Assert.DoesNotContain("reset", rig.General.Log);
        Assert.DoesNotContain("reset", rig.Keybinds.Log);
    }

    [Fact]
    public async Task ResetSection_Declined_ResetsNothing()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        Item(shell, SettingsTabKeys.Updates).SelectCommand.Execute(null);
        rig.Updates.SetUnsaved("updates.UpdateCheckOnStartup");
        rig.Dialogs.Answer = false;

        await ((IAsyncRelayCommand)shell.ResetSectionCommand).ExecuteAsync(null);

        Assert.Single(rig.Dialogs.Asked);
        Assert.DoesNotContain("reset", rig.Updates.Log);
    }

    [Fact]
    public void ResetSection_IsDisabled_WhileTheSelectedTabHasNothingUnsaved()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        Item(shell, SettingsTabKeys.Updates).SelectCommand.Execute(null);
        rig.General.SetUnsaved("general.Language");
        Assert.False(shell.ResetSectionCommand.CanExecute(null));

        Item(shell, SettingsTabKeys.General).SelectCommand.Execute(null);
        Assert.True(shell.ResetSectionCommand.CanExecute(null));
    }

    [Fact]
    public void AReloadRequest_ReReadsEveryTab_WhileThePageIsOpen()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        rig.Session.RequestReload();
        Assert.DoesNotContain("load", rig.General.Log);

        rig.Session.IsOpen = true;
        rig.Ui.Drain();
        rig.Session.RequestReload();

        Assert.Equal(2, rig.General.Log.Count(l => l == "load"));
    }

    [Fact]
    public void TypingABurst_RunsOnePass_OverEveryTab_AndListsTheHitsWithTheirTab()
    {
        var rig = new Rig();
        rig.Keybinds.SearchGroups =
        [
            new SettingsSearchGroup("Transport", null, [new SettingsSearchRow("keybinds.PlayPause", "Play / Pause")])
        ];
        rig.General.SearchGroups =
        [
            new SettingsSearchGroup("Playback", "Speed and looping", [new SettingsSearchRow("general.speed", "Default play speed")])
        ];
        using var shell = rig.Shell();

        shell.SearchQuery = "p";
        shell.SearchQuery = "pl";
        shell.SearchQuery = "play";
        Assert.Single(rig.Ui.Posted);
        Assert.Empty(shell.SearchResults);

        rig.Ui.Drain();

        Assert.True(shell.IsSearchOpen);
        Assert.True(shell.HasSearchResults);
        Assert.Equal(["Default play speed", "Play / Pause"], shell.SearchResults.Select(h => h.Label));
        Assert.Equal(FakeLocalization.English()[SettingsRail.Find(SettingsTabKeys.General)!.TitleKey], shell.SearchResults[0].TabName);
        Assert.Equal("Playback", shell.SearchResults[0].GroupName);
        Assert.True(shell.SearchResults[0].HasGroupName);
        Assert.Equal(FakeLocalization.English().Plural("Settings_SearchResultCountOne", "Settings_SearchResultCount", 2),
            shell.SearchResultCountText);
        Assert.All(rig.Tabs, t => Assert.Equal("play", t.LastQuery));
    }

    [Fact]
    public void TheDropdown_IsCappedAtFortyRows_WhileTheCountReportsTheTotal()
    {
        var rig = new Rig();
        rig.General.SearchGroups =
        [
            new SettingsSearchGroup("Everything", null,
                [.. Enumerable.Range(0, 50).Select(i => new SettingsSearchRow($"g.{i}", $"Setting {i}"))])
        ];
        using var shell = rig.Shell();

        shell.SearchQuery = "setting";
        rig.Ui.Drain();

        Assert.Equal(SettingsShellViewModel.MaxDropdownRows, shell.SearchResults.Count);
        Assert.Equal(FakeLocalization.English().Plural("Settings_SearchResultCountOne", "Settings_SearchResultCount", 50),
            shell.SearchResultCountText);
    }

    [Fact]
    public void TheKeyboard_WalksTheHits_WrapsAround_AndEnterJumpsToTheHitsTab()
    {
        var rig = new Rig();
        rig.Keybinds.SearchGroups = [new SettingsSearchGroup("Transport", null, [new SettingsSearchRow("k.1", "Play / Pause")])];
        rig.Updates.SearchGroups = [new SettingsSearchGroup("Updates", null, [new SettingsSearchRow("u.1", "Play nice with GitHub")])];
        using var shell = rig.Shell();
        shell.SearchQuery = "play";
        rig.Ui.Drain();

        shell.HighlightNextResultCommand.Execute(null);
        Assert.True(shell.SearchResults[0].IsHighlighted);
        shell.HighlightNextResultCommand.Execute(null);
        Assert.False(shell.SearchResults[0].IsHighlighted);
        Assert.True(shell.SearchResults[1].IsHighlighted);
        shell.HighlightNextResultCommand.Execute(null);
        Assert.True(shell.SearchResults[0].IsHighlighted);
        shell.HighlightPreviousResultCommand.Execute(null);
        Assert.True(shell.SearchResults[1].IsHighlighted);

        shell.ActivateResultCommand.Execute(null);

        Assert.Same(rig.Updates, shell.SelectedTab);
        Assert.False(shell.IsSearchOpen);
        Assert.Equal("play", shell.SearchQuery);
    }

    [Fact]
    public void EnterWithNothingHighlighted_TakesTheFirstHit_AndAClickTakesItsOwn()
    {
        var rig = new Rig();
        rig.Keybinds.SearchGroups = [new SettingsSearchGroup("Transport", null, [new SettingsSearchRow("k.1", "Play / Pause")])];
        rig.Updates.SearchGroups = [new SettingsSearchGroup("Updates", null, [new SettingsSearchRow("u.1", "Play nice")])];
        using var shell = rig.Shell();
        shell.SearchQuery = "play";
        rig.Ui.Drain();

        shell.ActivateResultCommand.Execute(null);
        Assert.Same(rig.Keybinds, shell.SelectedTab);

        shell.SearchResults[1].GoCommand.Execute(null);
        Assert.Same(rig.Updates, shell.SelectedTab);
    }

    [Fact]
    public void ClearingTheQuery_ClosesTheDropdown_AndRestoresEveryTab()
    {
        var rig = new Rig();
        using var shell = rig.Shell();
        shell.SearchQuery = "zzz";
        rig.Ui.Drain();
        Assert.True(shell.IsSearchOpen);
        Assert.False(shell.HasSearchResults);

        shell.ClearSearchCommand.Execute(null);
        rig.Ui.Drain();

        Assert.Equal("", shell.SearchQuery);
        Assert.False(shell.HasSearchQuery);
        Assert.False(shell.IsSearchOpen);
        Assert.All(rig.Tabs, t => Assert.Equal("", t.LastQuery));
    }

    [Fact]
    public void ItConformsToItsContract()
    {
        var rig = new Rig();
        rig.General.SearchGroups = [new SettingsSearchGroup("Playback", null, [new SettingsSearchRow("g.1", "Speed")])];
        using var shell = rig.Shell();
        shell.SearchQuery = "speed";
        rig.Ui.Drain();

        Assert.Empty(ContractConformanceTests.Violations(typeof(ISettingsShellScreen), shell));
    }

    [Fact]
    public void Dispose_StopsFollowingTheSessionAndTheSources()
    {
        var rig = new Rig();
        var shell = rig.Shell();
        shell.Dispose();

        rig.Session.IsOpen = true;
        rig.Ui.Drain();
        rig.Keybinds.SetUnsaved("keybinds.PlayPause");

        Assert.DoesNotContain("load", rig.General.Log);
        Assert.False(shell.HasUnsavedChanges);
    }
}
