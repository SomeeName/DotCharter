using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="ISetlistPanel" /> whose verbs really edit it: New makes and
///     picks an empty list, Add appends a song, the row's ✕ removes it, Delete drops the list.
/// </summary>
public sealed class FakeSetlistPanel : ObservableObject, ISetlistPanel
{
    private readonly ObservableCollection<FakeSetlistOption> _lists;
    private FakeSetlistOption? _selected;
    private IReadOnlyList<ISetlistEntryRow> _entries = [];
    private readonly string _notice;

    private FakeSetlistPanel(IEnumerable<FakeSetlistOption> lists, bool canAddSong, string notice = "")
    {
        _lists = new ObservableCollection<FakeSetlistOption>(lists);
        CanAddSong = canAddSong;
        _notice = notice;
        NewSetlistCommand = new RelayCommand(() =>
        {
            var list = new FakeSetlistOption(NextFreeName(), []);
            _lists.Add(list);
            OnPropertyChanged(nameof(Lists));
            Pick(list);
        });
        DeleteSetlistCommand = new RelayCommand(() =>
        {
            if (_selected is not { } list) return;
            var index = _lists.IndexOf(list);
            _lists.Remove(list);
            OnPropertyChanged(nameof(Lists));
            Pick(_lists.Count == 0 ? null : _lists[Math.Min(index, _lists.Count - 1)]);
        });
        AddSongToSetlistCommand = new RelayCommand(() =>
        {
            if (!CanAddSong) return;
            if (_selected == null) NewSetlistCommand.Execute(null);
            _selected!.Songs.Add(("Take My Bones Away", "Baroness · Lead"));
            Rebuild();
        });
        RemoveSetlistEntryCommand = new RelayCommand<ISetlistEntryRow>(row =>
        {
            if (row is not FakeSetlistEntryRow { } entry || _selected is not { } list) return;
            if (entry.Index < 0 || entry.Index >= list.Songs.Count) return;
            list.Songs.RemoveAt(entry.Index);
            Rebuild();
        });
        StartSetlistCommand = new RelayCommand(() => { });
        Pick(_lists.FirstOrDefault());
    }

    // ── the states the variants use ───────────────────────────────────────

    /// <summary>No setlist yet, and no song picked to add.</summary>
    public static FakeSetlistPanel None() => new([], canAddSong: false);

    /// <summary>Three setlists, the first picked: five songs, two of them slowed down.</summary>
    public static FakeSetlistPanel Picked() => new(
    [
        new FakeSetlistOption("Friday rehearsal", FakeSetlistSongs.Rehearsal()),
        new FakeSetlistOption("Warm-up", FakeSetlistSongs.Rehearsal().Take(2)),
        new FakeSetlistOption("Setlist 3", [])
    ], canAddSong: true);

    /// <summary>A picked setlist with no songs yet: the panel says how to add one.</summary>
    public static FakeSetlistPanel EmptyList() => new([new FakeSetlistOption("Setlist 1", [])], canAddSong: true);

    /// <summary>A 30-song setlist: past the rail's cap the panel's middle scrolls.</summary>
    public static FakeSetlistPanel Many() => new([new FakeSetlistOption("Whole album night", FakeSetlistSongs.Many())],
        canAddSong: true);

    /// <summary>A setlist that ended early because its last songs left the library.</summary>
    public static FakeSetlistPanel Skipped() => new(
        [new FakeSetlistOption("Friday rehearsal", FakeSetlistSongs.Rehearsal())], canAddSong: true,
        "Setlist ended. Not in your library: Moved Away: A Song That Left The Library, Hysteria (Live)");

    /// <summary>German and Japanese names, CDLC-length titles and a long notice.</summary>
    public static FakeSetlistPanel LongText() => new(
    [
        new FakeSetlistOption("Probe am Freitagabend mit der ganzen Band (zweites Set)", FakeSetlistSongs.LongText()),
        new FakeSetlistOption("金曜日のリハーサル・セットリスト（ウォームアップ付き）", FakeSetlistSongs.Rehearsal())
    ], canAddSong: true, "Deine Setlisten konnten gerade nicht gelesen werden. Änderungen werden erst gespeichert, wenn das wieder geht.");

    // ── ISetlistPanel ─────────────────────────────────────────────────────

    public IReadOnlyList<ISetlistOption> Lists => _lists;

    public ISetlistOption? SelectedList
    {
        get => _selected;
        set => Pick(value as FakeSetlistOption);
    }

    public string SelectedListName
    {
        get => _selected?.Name ?? "";
        set
        {
            if (_selected == null || value.Trim().Length == 0) return;
            _selected.Name = value.Trim();
            OnPropertyChanged();
        }
    }

    public string SelectedListSummary => _selected is { } list
        ? list.Songs.Count == 1 ? "1 song" : list.Songs.Count.ToString(CultureInfo.InvariantCulture) + " songs"
        : "";

    public IReadOnlyList<ISetlistEntryRow> Entries => _entries;

    public bool HasLists => _lists.Count > 0;

    public bool HasSelectedList => _selected != null;

    public bool HasEntries => _entries.Count > 0;

    public bool ShowsEmptyHint => HasSelectedList && !HasEntries;

    public bool CanAddSong { get; }

    public bool CanPlaySelected => _selected is { Songs.Count: > 0 };

    public string Notice => _notice;

    public ICommand NewSetlistCommand { get; }

    public ICommand DeleteSetlistCommand { get; }

    public ICommand AddSongToSetlistCommand { get; }

    public ICommand RemoveSetlistEntryCommand { get; }

    public ICommand StartSetlistCommand { get; }

    private void Pick(FakeSetlistOption? list)
    {
        _selected = list;
        OnPropertyChanged(nameof(SelectedList));
        OnPropertyChanged(nameof(SelectedListName));
        Rebuild();
    }

    private void Rebuild()
    {
        _entries = _selected is { } list
            ? list.Songs.Select((s, i) => (ISetlistEntryRow)new FakeSetlistEntryRow(i, s.Title, s.Detail)).ToList()
            : [];
        string[] changed =
        [
            nameof(Entries), nameof(HasLists), nameof(HasSelectedList), nameof(HasEntries), nameof(ShowsEmptyHint),
            nameof(SelectedListSummary), nameof(CanPlaySelected)
        ];
        foreach (var name in changed) OnPropertyChanged(name);
    }

    private string NextFreeName()
    {
        for (var n = _lists.Count + 1; ; n++)
        {
            var name = "Setlist " + n.ToString(CultureInfo.InvariantCulture);
            if (_lists.All(l => l.Name != name)) return name;
        }
    }
}

/// <summary>One setlist in the fake panel's picker, with its songs.</summary>
public sealed class FakeSetlistOption(string name, IEnumerable<(string Title, string Detail)> songs)
    : ObservableObject, ISetlistOption
{
    private string _name = name;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    internal List<(string Title, string Detail)> Songs { get; } = songs.ToList();
}

/// <summary>One song of the fake panel's picked setlist.</summary>
public sealed class FakeSetlistEntryRow(int index, string title, string detail) : ObservableObject, ISetlistEntryRow
{
    internal int Index { get; } = index;

    public string Number { get; } = (index + 1).ToString(CultureInfo.InvariantCulture);

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public bool HasDetail => Detail.Length > 0;

    public override string ToString() => Title;
}

/// <summary>
///     A services-free running setlist for Get Ready, Pause and Results. Next moves to the
///     following song; End stops the run, so every flag the screens read can change.
/// </summary>
public sealed class FakeSetlistRun : ObservableObject, IGetReadySetlist, IPausedSetlist, IResultsSetlist
{
    private readonly string _name;
    private readonly string[] _titles;
    private int _current;
    private bool _running;
    private string _skipped;

    private FakeSetlistRun(string name, string[] titles, int current, string skipped = "")
    {
        _name = name;
        _titles = titles;
        _current = current;
        _running = titles.Length > 0;
        _skipped = skipped;
        NextSetlistSongCommand = new RelayCommand(() =>
        {
            if (!HasNext) return;
            _current++;
            _skipped = "";
            Raise();
        });
        SkipSetlistSongCommand = NextSetlistSongCommand;
        EndSetlistCommand = new RelayCommand(() =>
        {
            _running = false;
            _skipped = "";
            Raise();
        });
    }

    /// <summary>No setlist is running: the screens show nothing of it.</summary>
    public static FakeSetlistRun Idle() => new("", [], 0);

    /// <summary>Song 2 of 5 of "Friday rehearsal".</summary>
    public static FakeSetlistRun MidRun() => new("Friday rehearsal", FakeSetlistSongs.RehearsalTitles(), 1);

    /// <summary>The last song's results: the setlist is done.</summary>
    public static FakeSetlistRun LastSong() => new("Friday rehearsal", FakeSetlistSongs.RehearsalTitles(), 4);

    /// <summary>Song 3 of 5, after one that left the library was skipped.</summary>
    public static FakeSetlistRun Skipped() => new("Friday rehearsal", FakeSetlistSongs.RehearsalTitles(), 2,
        "Skipped, not in your library: Moved Away: A Song That Left The Library");

    /// <summary>A long German setlist name, a CDLC-length next title and a long skip note.</summary>
    public static FakeSetlistRun LongText() => new("Probe am Freitagabend mit der ganzen Band (zweites Set)",
        [.. FakeSetlistSongs.LongText().Select(s => s.Title)], 0,
        "Übersprungen, nicht in deiner Bibliothek: " + FakeFlowText.LongSongTitle + ", 非常に長い曲名のテストケース");

    public bool IsRunning => _running;

    public string ProgressLabel => _running
        ? string.Create(CultureInfo.InvariantCulture, $"{_name} · song {_current + 1} of {_titles.Length}")
        : "";

    public string SkippedNote => _running ? _skipped : "";

    public bool HasSkippedNote => SkippedNote.Length > 0;

    public bool HasNext => _running && _current + 1 < _titles.Length;

    public string NextLabel => HasNext ? "Next: " + _titles[_current + 1] : "";

    public bool IsFinished => _running && !HasNext;

    public string FinishedLabel => IsFinished
        ? string.Create(CultureInfo.InvariantCulture, $"Setlist complete · {_titles.Length} / {_titles.Length} played")
        : "";

    public ICommand NextSetlistSongCommand { get; }

    public ICommand SkipSetlistSongCommand { get; }

    public ICommand EndSetlistCommand { get; }

    private void Raise() => OnPropertyChanged(string.Empty);
}

/// <summary>The songs the setlist fakes are made of.</summary>
internal static class FakeSetlistSongs
{
    public static IEnumerable<(string Title, string Detail)> Rehearsal() =>
    [
        ("Master of Puppets", "Metallica · Lead"),
        ("Hysteria", "Muse · Bass · 80 %"),
        ("Take My Bones Away", "Baroness · Rhythm"),
        ("Do I Wanna Know?", "Arctic Monkeys · Lead"),
        ("Pull Me Under", "Dream Theater · Lead · 70 %")
    ];

    public static string[] RehearsalTitles() => [.. Rehearsal().Select(s => s.Title)];

    public static IEnumerable<(string Title, string Detail)> Many() =>
        Enumerable.Range(1, 30).Select(i => (
            string.Create(CultureInfo.InvariantCulture, $"Track {i:00} — Live at the Roundhouse"),
            i % 3 == 0 ? "The Band · Rhythm · 85 %" : "The Band · Lead"));

    public static IEnumerable<(string Title, string Detail)> LongText() =>
    [
        ("非常に長い曲名のテストケース（デラックス・エディション、リマスター）", "長いアーティスト名のバンド・プロジェクト · リズム"),
        (FakeFlowText.LongSongTitle, "Die Ärzte und das Orchester der Stadt Wuppertal · Lead · 75 %"),
        ("Schrei nach Liebe (Live in der Waldbühne, erweiterte Fassung)", "Die Ärzte · Bass")
    ];
}
