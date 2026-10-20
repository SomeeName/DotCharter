using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
/// The Chart quality report's Editor on Fire round trip, over
///     <see cref="EofLossSummary" />: a group per kind of loss, named in the user's language
///     (<see cref="EofLosses.ResourceKey" />), each place with its arrangement, bar.beat and time
///     and the value that names it. The first few places of each kind are listed and Show all
///     lists every one; the whole list opens as plain text in the text-document dialog, whose
///     Copy takes it.
/// <para>It is LAZY: the in-memory export costs about as much as the rest of the report
///     on a chart with a difficulty ladder, so nothing runs while the section is closed. Opened,
///     the chart is snapshotted on the UI thread and the export runs on <see cref="OffThread" />; a
///     result that lands after a recheck is a stale chart's and is dropped.</para>
/// </summary>
public sealed class EofRoundTripSectionViewModel : ObservableObject, IEofRoundTripSection
{
    private readonly ILocalizationService _loc;
    private readonly IDialogs _dialogs;
    private readonly IAppLogger _logger;
    private readonly Action<string, double?> _jump;
    private readonly Func<ChartProject?> _snapshot;
    private EofLossSummary _summary = EofLossSummary.Empty;
    private IReadOnlyList<BeatMarker> _beats = [];
    private bool _showAll;
    private bool _isExpanded;
    private int _generation;
    private int _shownGeneration = -1;

    /// <summary>
    ///     <paramref name="jump" /> seeks the editor: the place's subject (an arrangement's name,
    ///     or the beat map / vocals) and its time. <paramref name="snapshot" /> copies the chart to
    ///     check, on the UI thread; null when there is none.
    /// </summary>
    public EofRoundTripSectionViewModel(ILocalizationService localization, IDialogs dialogs, IAppLogger logger,
        Action<string, double?> jump, Func<ChartProject?> snapshot)
    {
        _loc = localization;
        _dialogs = dialogs;
        _logger = logger;
        _jump = jump;
        _snapshot = snapshot;
        OpenListCommand = new RelayCommand(OpenList, () => HasGroups);
    }

    /// <summary>Runs the export off the UI thread; a test hands in its own to see it is used.</summary>
    internal Func<Func<EofRoundTripCheck>, Task<EofRoundTripCheck>> OffThread { get; set; } = Task.Run;

    /// <summary>The run in flight, or the last one.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            if (value && _shownGeneration != _generation && !IsBusy) Start();
        }
    }

    public bool IsBusy { get; private set; }

    public string Summary { get; private set; } = "";

    public bool HasGroups => Groups.Count > 0;

    public IReadOnlyList<IEofLossGroup> Groups { get; private set; } = [];

    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (_showAll == value) return;
            _showAll = value;
            Project();
        }
    }

    public bool CanShowAll => _summary.Groups.Any(g => g.Hidden > 0);

    public ICommand OpenListCommand { get; }

    /// <summary>The chart was rechecked, so what is shown is another chart's round trip. Open, it runs again.</summary>
    public void Invalidate()
    {
        _generation++;
        IsBusy = false;
        Show(null, "", []);
        if (_isExpanded) Start();
    }

    /// <summary>Shows a round trip; <paramref name="error" /> is why there is none, and no summary at all shows nothing.</summary>
    internal void Show(EofLossSummary? summary, string error, IReadOnlyList<BeatMarker> beats)
    {
        _summary = summary ?? EofLossSummary.Empty;
        _beats = beats;
        Summary = error.Length > 0 ? _loc.Format("Dialog_QualityReport_EofFailed", error)
            : summary == null ? ""
            : _summary.IsLossless ? _loc["Dialog_QualityReport_EofLossless"]
            : _loc.Format("Dialog_QualityReport_EofSummary", _summary.Dropped, _summary.Approximated);
        Project();
    }

    private void Start()
    {
        var generation = _generation;
        var snapshot = _snapshot();
        if (snapshot == null)
        {
            _shownGeneration = generation;
            return;
        }

        IsBusy = true;
        Summary = _loc["Dialog_QualityReport_EofChecking"];
        Project();
        Pending = RunAsync(snapshot, generation);
    }

    private async Task RunAsync(ChartProject snapshot, int generation)
    {
        var elapsed = TimeSpan.Zero;
        EofRoundTripCheck check;
        try
        {
            check = await OffThread(() =>
            {
                var watch = Stopwatch.StartNew();
                var result = ChartQualityReporter.CheckEofRoundTrip(snapshot);
                elapsed = watch.Elapsed;
                return result;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            check = new EofRoundTripCheck(null, ex.Message);
        }

        var stale = generation != _generation;
        _logger.Info(AppLogCategory.Performance, string.Create(CultureInfo.InvariantCulture,
            $"EOF round trip checked off the UI thread in {elapsed.TotalMilliseconds:0} ms, {check.Summary?.Total ?? 0} places{(stale ? "; dropped, the chart was rechecked" : "")}."));
        if (stale) return;
        IsBusy = false;
        _shownGeneration = generation;
        Show(check.Summary, check.Error, [.. snapshot.Beats]);
    }

    /// <summary>The whole list as plain text, every place of every kind, in the user's language.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(_loc["Dialog_QualityReport_EofTitle"]).Append(" — ").AppendLine(Summary);
        foreach (var group in _summary.Groups)
        {
            text.AppendLine();
            text.Append(CountOf(group)).Append(": ").AppendLine(_loc[EofLosses.ResourceKey(group.Loss)]);
            foreach (var place in group.Places)
            {
                text.Append("    ").Append(Location(place));
                if (place.Detail.Length > 0) text.Append(" · ").Append(place.Detail);
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private void Project()
    {
        Groups = _summary.Groups.Select(IEofLossGroup (g) =>
        {
            var places = _showAll ? g.Places : g.Shown;
            var hidden = g.Count - places.Count;
            return new Group(_loc[EofLosses.ResourceKey(g.Loss)], CountOf(g), g.Kind == EofConversionKind.Dropped,
                [.. places.Select(Place)],
                hidden > 0 ? _loc.Format("Dialog_QualityReport_EofMore", hidden) : "");
        }).ToList();
        OnPropertyChanged(string.Empty);
        ((RelayCommand)OpenListCommand).NotifyCanExecuteChanged();
    }

    private string CountOf(EofLossGroup group) => _loc.Format(
        group.Kind == EofConversionKind.Dropped ? "Dialog_QualityReport_EofDropped" : "Dialog_QualityReport_EofApproximated",
        group.Count);

    private IEofLossPlace Place(EofLossPlace place) =>
        new Row(SubjectOf(place), ChartPositionLabel.Format(_beats, place.TimeMs), place.Detail, place.TimeMs.HasValue,
            new RelayCommand(() => _jump(place.Subject, place.TimeMs), () => place.TimeMs.HasValue));

    private string SubjectOf(EofLossPlace place) => place.Subject switch
    {
        EofLosses.BeatsSubject => _loc["Dialog_QualityReport_EofBeatMap"],
        EofLosses.VocalsSubject => _loc["Dialog_QualityReport_EofVocals"],
        _ => place.Subject
    };

    private string Location(EofLossPlace place)
    {
        var where = ChartPositionLabel.Format(_beats, place.TimeMs);
        return where.Length == 0
            ? SubjectOf(place)
            : string.Create(CultureInfo.InvariantCulture, $"{SubjectOf(place)}{ChartPositionLabel.Separator}{where}");
    }

    private void OpenList()
    {
        if (!HasGroups) return;
        _dialogs.ShowAsync(new TextDocumentRequest(_loc["Dialog_QualityReport_EofTitle"], Summary, ToText(), ""))
            .Observe(_logger, "EOF round trip list");
    }

    private sealed record Group(string Title, string Count, bool IsDropped, IReadOnlyList<IEofLossPlace> Items,
        string MoreNote) : IEofLossGroup
    {
        public bool HasMore => MoreNote.Length > 0;
    }

    private sealed record Row(string Subject, string Where, string Detail, bool HasTime, ICommand JumpCommand) : IEofLossPlace;
}
