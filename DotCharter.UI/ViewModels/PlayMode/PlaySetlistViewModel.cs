using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Setlists;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
/// Play Mode SETLISTS: a named, ordered list of songs, each with its
///     path and speed, played back to back. Solo only — the LAN setlists and voting
///     are not this. Exposed as <c>PlayModeViewModel.Setlists</c>
///     and driven through <see cref="IPlaySetlistHost" />, so none of it adds to the
///     Play Mode partials.
///     <list type="bullet">
///         <item>Song Select: the picker, "Add to setlist" (the selected song, the
///         rail's path, the current practice speed) and "Play setlist".</item>
///         <item>Results: "Next: &lt;song&gt;" while a setlist runs and has a next
///         song; on the last song, the setlist-complete line.</item>
///         <item>Pause: "Skip song" and "End setlist".</item>
///         <item>A song that will not open is skipped and NAMED on the next Get
///         Ready (or on Song Select when nothing after it opens).</item>
///         <item>Leaving to Song Select any other way ends the setlist.</item>
///     </list>
///     <para>
///         It is the app's side of the Presentation setlist contracts: Song Select's
///         <see cref="ISetlistPanel" /> and the flow screens' <see cref="IGetReadySetlist" />,
///         <see cref="IPausedSetlist" /> and <see cref="IResultsSetlist" />, which the views
///         bind and the gallery's fakes stand in for.
///     </para>
///     <para>
///         Persistence is per player profile (<see cref="IPlaySetlistStore" />), read
///         and written OFF the UI thread; saves are chained so they land in order.
///         Without a store (a Play Mode built outside DI) the lists live in memory.
///     </para>
/// </summary>
public sealed partial class PlaySetlistViewModel : ObservableObject,
    ISetlistPanel, IGetReadySetlist, IPausedSetlist, IResultsSetlist
{
    private readonly IPlaySetlistStore? _store;
    private readonly IPlayerProfileService? _profiles;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;

    private IPlaySetlistHost? _host;
    private SetlistRunner? _runner;
    private Guid _loadedProfile;
    private Task? _loading;
    private Task _saving = Task.CompletedTask;
    private int _speedBeforeRun = 100;

    /// <summary>Set while a setlist verb moves Play Mode itself, so the Song Select
    /// it passes through is not read as the player walking away.</summary>
    private bool _transitioning;

    /// <summary>Set while a newly picked list's name is copied into the editor, so
    /// the copy is not saved back as a rename.</summary>
    private bool _syncingName;

    public PlaySetlistViewModel(ILocalizationService localization, IPlaySetlistStore? store = null,
        IPlayerProfileService? profiles = null, IAppLogger? logger = null)
    {
        _localization = localization;
        _store = store;
        _profiles = profiles;
        _logger = logger;
        _profiles?.ActiveProfileChanged += OnActiveProfileChanged;
        NewSetlistCommand = new RelayCommand(NewSetlist);
        DeleteSetlistCommand = new RelayCommand(DeleteSetlist);
        AddSongToSetlistCommand = new RelayCommand(AddSongToSetlist);
        RemoveSetlistEntryCommand = new RelayCommand<ISetlistEntryRow?>(RemoveSetlistEntry);
        StartSetlistCommand = new AsyncRelayCommand(StartSetlist);
        NextSetlistSongCommand = new AsyncRelayCommand(NextSetlistSong);
        SkipSetlistSongCommand = new AsyncRelayCommand(SkipSetlistSong);
        EndSetlistCommand = new RelayCommand(EndSetlist);
    }

    // The verbs are plain properties, so each is one object the contracts and the tests share.

    public IRelayCommand NewSetlistCommand { get; }

    public IRelayCommand DeleteSetlistCommand { get; }

    public IRelayCommand AddSongToSetlistCommand { get; }

    public IRelayCommand<ISetlistEntryRow?> RemoveSetlistEntryCommand { get; }

    public IAsyncRelayCommand StartSetlistCommand { get; }

    public IAsyncRelayCommand NextSetlistSongCommand { get; }

    public IAsyncRelayCommand SkipSetlistSongCommand { get; }

    public IRelayCommand EndSetlistCommand { get; }

    /// <summary>A profile switch brings that player's setlists.</summary>
    private void OnActiveProfileChanged(PlayerProfile profile) => _ = LoadAsync();

    /// <summary>Every setlist of the active profile, in the order they were made.</summary>
    public ObservableCollection<PlaySetlistOption> Lists { get; } = [];

    /// <summary>The picked setlist's songs, as display rows.</summary>
    [ObservableProperty] private IReadOnlyList<ISetlistEntryRow> _entries = [];

    [ObservableProperty] private PlaySetlistOption? _selectedList;

    /// <summary>The picked setlist's name, editable in place.</summary>
    [ObservableProperty] private string _selectedListName = "";

    /// <summary>A line for Song Select: a skipped song when nothing after it
    /// opened, or a store that could not be read.</summary>
    [ObservableProperty] private string _notice = "";

    /// <summary>Get Ready's note naming the songs skipped on the way to this one.</summary>
    [ObservableProperty] private string _skippedNote = "";

    [ObservableProperty] private bool _isRunning;

    public bool HasSkippedNote => SkippedNote.Length > 0;

    public bool HasLists => Lists.Count > 0;

    public bool HasSelectedList => SelectedList != null;

    public bool HasEntries => Entries.Count > 0;

    /// <summary>A list is picked and has no songs: the panel says how to add one.</summary>
    public bool ShowsEmptyHint => HasSelectedList && !HasEntries;

    /// <summary>Song Select has a playable song to add.</summary>
    public bool CanAddSong => _host?.SelectedSong is { CanPlay: true };

    public string SelectedListSummary => SelectedList is { } list
        ? _localization.Plural("PlayMode_Setlist_SongsOne", "PlayMode_Setlist_SongsFmt", list.Setlist.Entries.Count)
        : "";

    public bool CanPlaySelected => !IsRunning && SelectedList is { Setlist.Entries.Count: > 0 };

    /// <summary>"Rock night · song 2 of 5" — Get Ready, Pause and Results.</summary>
    public string ProgressLabel => _runner is { IsRunning: true, CurrentIndex: >= 0 } run
        ? _localization.Format("PlayMode_Setlist_Progress", run.Name, run.CurrentIndex + 1, run.Count)
        : "";

    /// <summary>A setlist is running and a song follows the current one.</summary>
    public bool HasNext => IsRunning && _runner is { HasNext: true };

    public string NextLabel => _runner?.PeekNext() is { } next
        ? _localization.Format("PlayMode_Setlist_Next", next.Title)
        : "";

    /// <summary>The last song's results are up: the setlist is done.</summary>
    public bool IsFinished => IsRunning && _runner is { HasNext: false } && _host?.CurrentScreen == PlayModeScreen.Results;

    public string FinishedLabel => _runner is { } run
        ? _localization.Format("PlayMode_Setlist_Complete", run.CompletedCount, run.Count)
        : "";

    /// <summary>Called once by Play Mode with itself.</summary>
    public void AttachHost(IPlaySetlistHost host)
    {
        _host = host;
        host.PropertyChanged += OnHostPropertyChanged;
    }

    /// <summary>Reads the active profile's setlists off the UI thread. Without a
    /// store there is nothing to read.</summary>
    public Task LoadAsync() => _loading = LoadCoreAsync();

    /// <summary>Completes when every save queued so far has landed (tests).</summary>
    public Task WhenSaved => _saving;

    private async Task LoadCoreAsync()
    {
        if (_store == null || _profiles == null) return;
        var profile = _profiles.ActiveProfile.Id;
        var store = _store;
        var read = await Task.Run(() => store.Load(profile));
        _loadedProfile = profile;
        Lists.Clear();
        foreach (var list in read.Setlists) Lists.Add(new PlaySetlistOption(list));
        SelectedList = Lists.FirstOrDefault();
        Notice = read.Outcome switch
        {
            PlaySetlistReadOutcome.Unreadable => _localization["PlayMode_Setlist_Unreadable"],
            PlaySetlistReadOutcome.Corrupt => _localization["PlayMode_Setlist_Corrupt"],
            _ => ""
        };
        RaiseListState();
    }

    private void EnsureLoaded()
    {
        if (_loading == null) _ = LoadAsync();
    }

    // ── editing ────────────────────────────────────────────────────────────

    private void NewSetlist()
    {
        var list = new PlaySetlistOption(new PlaySetlist { Name = UniqueDefaultName() });
        Lists.Add(list);
        SelectedList = list;
        Save();
    }

    private void DeleteSetlist()
    {
        if (SelectedList is not { } list) return;
        var index = Lists.IndexOf(list);
        Lists.Remove(list);
        SelectedList = Lists.Count == 0 ? null : Lists[Math.Min(index, Lists.Count - 1)];
        Save();
    }

    /// <summary>Adds Song Select's song on the rail's path at the current practice
    /// speed. With no setlist yet, one is made for it.</summary>
    private void AddSongToSetlist()
    {
        if (_host?.SelectedSong is not { CanPlay: true } song) return;
        if (SelectedList == null) NewSetlist();
        SelectedList!.Setlist.Entries.Add(new PlaySetlistEntry
        {
            SongKey = song.SongIdentity,
            Arrangement = _host.SelectedArrangement?.Label ?? "",
            SpeedPercent = _host.PracticeSpeedPercent,
            Title = song.Title,
            Artist = song.Artist
        });
        RebuildEntries();
        Save();
    }

    private void RemoveSetlistEntry(ISetlistEntryRow? entry)
    {
        if (entry is not PlaySetlistEntryRow row || SelectedList?.Setlist is not { } list) return;
        if (row.Index < 0 || row.Index >= list.Entries.Count) return;
        list.Entries.RemoveAt(row.Index);
        RebuildEntries();
        Save();
    }

    partial void OnSelectedListChanged(PlaySetlistOption? value)
    {
        SyncName(value?.Name ?? "");
        RebuildEntries();
        RaiseListState();
    }

    /// <summary>A rename (committed when the field loses focus). A blank name is
    /// put back rather than saved; a new one reaches the picker through the
    /// option's own change notification.</summary>
    partial void OnSelectedListNameChanged(string value)
    {
        if (_syncingName || SelectedList is not { } list) return;
        var name = value.Trim();
        if (name.Length == 0 || name == list.Name)
        {
            if (value != list.Name) SyncName(list.Name);
            return;
        }

        list.Rename(name);
        if (value != name) SyncName(name);
        Save();
    }

    private void SyncName(string name)
    {
        _syncingName = true;
        try
        {
            SelectedListName = name;
        }
        finally
        {
            _syncingName = false;
        }
    }

    partial void OnIsRunningChanged(bool value) => RaiseRunState();

    partial void OnSkippedNoteChanged(string value) => OnPropertyChanged(nameof(HasSkippedNote));

    // ── running ────────────────────────────────────────────────────────────

    private async Task StartSetlist()
    {
        if (_host == null || !CanPlaySelected) return;
        _runner = new SetlistRunner(SelectedList!.Setlist);
        _speedBeforeRun = _host.PracticeSpeedPercent;
        Notice = "";
        IsRunning = true;
        await OpenNextAsync();
    }

    /// <summary>Results → the next song's Get Ready.</summary>
    private async Task NextSetlistSong()
    {
        if (_host == null || !HasNext) return;
        Leave(_host.BackToSongSelectCommand);
        await OpenNextAsync();
    }

    /// <summary>Pause → the next song's Get Ready; the run is abandoned.</summary>
    private async Task SkipSetlistSong()
    {
        if (_host == null || !IsRunning) return;
        Leave(_host.AbortToSongSelectCommand);
        await OpenNextAsync();
    }

    /// <summary>Pause or Results → Song Select, and the setlist is over.</summary>
    private void EndSetlist()
    {
        if (_host == null || !IsRunning) return;
        var exit = _host.CurrentScreen == PlayModeScreen.Results
            ? _host.BackToSongSelectCommand
            : _host.AbortToSongSelectCommand;
        Finish();
        Leave(exit);
    }

    private void Leave(IRelayCommand exit)
    {
        _transitioning = true;
        try
        {
            exit.Execute(null);
        }
        finally
        {
            _transitioning = false;
        }
    }

    /// <summary>Opens the runner's next song, skipping every one that will not
    /// open. Ends the setlist when none is left.</summary>
    private async Task OpenNextAsync()
    {
        if (_host is not { } host || _runner is not { } runner) return;
        _transitioning = true;
        try
        {
            while (runner.MoveNext() is { } entry)
            {
                if (await TryOpenAsync(host, entry))
                {
                    host.PracticeSpeedPercent = entry.EffectiveSpeedPercent;
                    SkippedNote = SkipText("PlayMode_Setlist_Skipped", runner.TakePendingSkips());
                    RaiseRunState();
                    return;
                }

                runner.MarkMissing();
            }

            var skipped = runner.TakePendingSkips();
            Finish();
            Notice = SkipText("PlayMode_Setlist_EndedSkipped", skipped);
        }
        finally
        {
            _transitioning = false;
        }
    }

    private async Task<bool> TryOpenAsync(IPlaySetlistHost host, PlaySetlistEntry entry)
    {
        try
        {
            return await host.OpenSongAsync(entry.SongKey, entry.Arrangement);
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Application, "Setlist: a song could not be opened; skipping it.", ex);
            return false;
        }
    }

    private void Finish()
    {
        if (!IsRunning) return;
        _host?.PracticeSpeedPercent = _speedBeforeRun;
        SkippedNote = "";
        IsRunning = false;
        _runner = null;
        RaiseRunState();
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPlaySetlistHost.SelectedSong)) OnPropertyChanged(nameof(CanAddSong));
        if (e.PropertyName != nameof(IPlaySetlistHost.CurrentScreen) || _host == null) return;
        switch (_host.CurrentScreen)
        {
            case PlayModeScreen.SongSelect:
                EnsureLoaded();
                if (IsRunning && !_transitioning) Finish();
                break;
            case PlayModeScreen.Results:
                _runner?.MarkCompleted();
                break;
        }

        RaiseRunState();
    }

    // ── the contracts' shapes ──────────────────────────────────────────────

    IReadOnlyList<ISetlistOption> ISetlistPanel.Lists => Lists;

    ISetlistOption? ISetlistPanel.SelectedList
    {
        get => SelectedList;
        set => SelectedList = value as PlaySetlistOption;
    }

    ICommand ISetlistPanel.NewSetlistCommand => NewSetlistCommand;

    ICommand ISetlistPanel.DeleteSetlistCommand => DeleteSetlistCommand;

    ICommand ISetlistPanel.AddSongToSetlistCommand => AddSongToSetlistCommand;

    ICommand ISetlistPanel.RemoveSetlistEntryCommand => RemoveSetlistEntryCommand;

    ICommand ISetlistPanel.StartSetlistCommand => StartSetlistCommand;

    ICommand IPausedSetlist.SkipSetlistSongCommand => SkipSetlistSongCommand;

    ICommand IPausedSetlist.EndSetlistCommand => EndSetlistCommand;

    ICommand IResultsSetlist.NextSetlistSongCommand => NextSetlistSongCommand;

    ICommand IResultsSetlist.EndSetlistCommand => EndSetlistCommand;

    // ── helpers ────────────────────────────────────────────────────────────

    private string SkipText(string key, IReadOnlyList<PlaySetlistEntry> skipped) =>
        skipped.Count == 0 ? "" : _localization.Format(key, string.Join(", ", skipped.Select(s => s.Title)));

    private string UniqueDefaultName()
    {
        for (var n = Lists.Count + 1; ; n++)
        {
            var name = _localization.Format("PlayMode_Setlist_DefaultName", n);
            if (Lists.All(l => !string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase))) return name;
        }
    }

    private void RebuildEntries()
    {
        Entries = SelectedList?.Setlist is { } list
            ? [.. list.Entries.Select((entry, i) => (ISetlistEntryRow)PlaySetlistEntryRow.From(i, entry))]
            : [];
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(ShowsEmptyHint));
        OnPropertyChanged(nameof(SelectedListSummary));
        OnPropertyChanged(nameof(CanPlaySelected));
    }

    /// <summary>Queues a save of a COPY of the lists behind every earlier save.
    /// Nothing is saved for a profile the lists were not read from.</summary>
    private void Save()
    {
        RaiseListState();
        if (_store is not { } store || _loading == null) return;
        var profile = _loadedProfile;
        var snapshot = Lists.Select(l => l.Setlist.Clone()).ToList();
        _saving = _saving.ContinueWith(_ =>
        {
            if (!store.Save(profile, snapshot))
                _logger?.Warning(AppLogCategory.Application, "Setlists: the save was refused or failed.");
        }, TaskScheduler.Default);
    }

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(Lists));
        OnPropertyChanged(nameof(HasLists));
        OnPropertyChanged(nameof(HasSelectedList));
        OnPropertyChanged(nameof(ShowsEmptyHint));
        OnPropertyChanged(nameof(SelectedListSummary));
        OnPropertyChanged(nameof(CanPlaySelected));
    }

    private void RaiseRunState()
    {
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(FinishedLabel));
        OnPropertyChanged(nameof(CanPlaySelected));
    }
}
