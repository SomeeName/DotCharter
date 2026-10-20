using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>
///     The quality report's Editor on Fire round trip, services-free; Show all really lists every
///     place, and opening it shows what it holds at once (the real one computes it off the UI thread).
/// </summary>
internal sealed class FakeEofRoundTripSection : IEofRoundTripSection
{
    private static readonly ICommand Inert = new RelayCommand(() => { });
    private readonly IReadOnlyList<FakeLossGroup> _all;
    private bool _showAll;
    private bool _isExpanded;

    private readonly Words _words;

    private FakeEofRoundTripSection(string summary, IReadOnlyList<FakeLossGroup> groups, Words? words = null,
        bool expanded = false, bool busy = false)
    {
        Summary = summary;
        _all = groups;
        _words = words ?? new Words("Dropped", "Approximated", "{0} more — Show all lists them");
        _isExpanded = expanded;
        IsBusy = busy;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public bool IsBusy { get; }

    public string Summary { get; }
    public bool HasGroups => _all.Count > 0;
    public IReadOnlyList<IEofLossGroup> Groups => [.. _all.Select(g => g.Showing(_showAll, _words))];
    public bool CanShowAll => _all.Any(g => g.Places.Count > Shown);
    public ICommand OpenListCommand => Inert;

    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (_showAll == value) return;
            _showAll = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    private const int Shown = 5;

    /// <summary>A song with a cent offset, an unused tone, bends and vibratos EOF rounds; closed, as a charter who never exported to EOF sees it.</summary>
    internal static FakeEofRoundTripSection Typical() => new("2 dropped · 9 approximated",
    [
        new("Cent offset left out", true, [new("Lead", "", "+12"), new("Rhythm", "", "-8")]),
        new("Bend rounded to a quarter step", false,
        [
            new("Lead", "M12.3 · 00:41.25", "1.3 → 1.25"), new("Lead", "M14.1 · 00:47.00", "0.6 → 0.5"),
            new("Lead", "M22.4 · 01:16.87", "1.1 → 1"), new("Lead", "M40.2 · 02:13.16", "2.1 → 2"),
            new("Rhythm", "M8.1 · 00:28.00", "0.3 → 0.25"), new("Rhythm", "M9.1 · 00:31.50", "0.3 → 0.25")
        ]),
        new("Vibrato strength not kept (EOF re-imports 80)", false,
            [new("Lead", "M30.1 · 01:41.00", "120"), new("Lead", "M31.1 · 01:44.50", "40")]),
        new("Bar line moved (EOF counts bars from the time signature)", false, [new("Beat map", "M17.3 · 00:58.50", "")])
    ]);

    /// <summary>Opened, the export still running.</summary>
    internal static FakeEofRoundTripSection Checking() =>
        new("Checking what an Editor on Fire export would keep…", [], expanded: true, busy: true);

    /// <summary>Nothing an EOF export would lose.</summary>
    internal static FakeEofRoundTripSection Lossless() => new("An Editor on Fire export keeps everything in this chart.", []);

    /// <summary>German, with long arrangement, tone and phrase names.</summary>
    internal static FakeEofRoundTripSection LongText() => new("3 entfallen · 7 angenähert",
    [
        new("Sound ohne Soundwechsel", true,
        [
            new("Leadgitarre (alternative Stimmung, Kapodaster im dritten Bund)", "", "Verzerrter Leadsound mit Hall und Delay für den Refrain"),
            new("リズムギター（ドロップD）", "", "クリーン・コーラス")
        ]),
        new("Phrase nie auf der Zeitleiste platziert", true, [new("Leadgitarre (alternative Stimmung)", "", "Zwischenspiel nach dem zweiten Refrain")]),
        new("Akkord mit unterschiedlichem Sustain je Saite", false,
            [.. Enumerable.Range(0, 7).Select(i => Timed("Leadgitarre (alternative Stimmung, Kapodaster im dritten Bund)", i, ""))])
    ], new Words("Entfällt", "Angenähert", "{0} weitere — „Alle zeigen“ listet sie"), expanded: true);

    /// <summary>A long chart: many kinds, dozens of places each.</summary>
    internal static FakeEofRoundTripSection Full() => new("12 dropped · 148 approximated",
    [
        new("Fret-hand position on the capo left out", true, Many("Rhythm", 12, "")),
        new("Bend rounded to a quarter step", false, Many("Lead", 60, "1.3 → 1.25")),
        new("Vibrato strength not kept (EOF re-imports 80)", false, Many("Lead", 40, "120")),
        new("Chord with different sustain lengths per string", false, Many("Rhythm", 30, "")),
        new("Bar line moved (EOF counts bars from the time signature)", false, Many("Beat map", 18, ""))
    ], expanded: true);

    private static FakeLossPlace Timed(string subject, int i, string detail) => new(subject,
        string.Create(CultureInfo.InvariantCulture, $"M{4 + i * 3}.{1 + i % 4} · {i / 6:00}:{i * 7 % 60:00}.{i * 13 % 100:00}"), detail);

    private static IReadOnlyList<FakeLossPlace> Many(string subject, int count, string detail) =>
        [.. Enumerable.Range(0, count).Select(i => Timed(subject, i, detail))];

    private sealed record Words(string Dropped, string Approximated, string More);

    private sealed record FakeLossPlace(string Subject, string Where, string Detail) : IEofLossPlace
    {
        public bool HasTime => Where.Length > 0;
        public ICommand JumpCommand => Inert;
    }

    private sealed record FakeLossGroup(string Title, bool IsDropped, IReadOnlyList<FakeLossPlace> Places)
    {
        public IEofLossGroup Showing(bool all, Words words)
        {
            var items = all ? Places : [.. Places.Take(Shown)];
            var hidden = Places.Count - items.Count;
            var count = string.Create(CultureInfo.InvariantCulture,
                $"{(IsDropped ? words.Dropped : words.Approximated)} ({Places.Count})");
            return new View(Title, count, IsDropped, items,
                hidden > 0 ? string.Format(CultureInfo.InvariantCulture, words.More, hidden) : "");
        }
    }

    private sealed record View(string Title, string Count, bool IsDropped, IReadOnlyList<IEofLossPlace> Items, string MoreNote)
        : IEofLossGroup
    {
        public bool HasMore => MoreNote.Length > 0;
    }
}
