using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>One phrase iteration in the browser. Carries per-row two-step
/// confirm state for the destructive propagate action.</summary>
public partial class PhraseIterationRow : ObservableObject, IPhraseIterationRow
{
    /// <summary>One iteration, labelled "#2 · bar 5 · 0:20.0" (<see cref="MarkerOccurrenceLabel" />).</summary>
    public PhraseIterationRow(PhraseIteration iteration, int ordinal, double timeMs, string label)
    {
        Iteration = iteration;
        Ordinal = ordinal;
        TimeMs = timeMs;
        Label = label;
    }

    public PhraseIteration Iteration { get; }
    public int Ordinal { get; }
    public double TimeMs { get; }
    public string Label { get; }

    /// <summary>The phrase has other iterations to copy this one into.</summary>
    public bool CanPropagate { get; init; }

    /// <summary>Armed by the first propagate click; the second executes.</summary>
    [ObservableProperty] private bool _isConfirming;
}

/// <summary>One phrase group: its name, every iteration time-ordered, and its inline rename.</summary>
public sealed partial class PhraseGroupRow(
    string name, string countLabel, IReadOnlyList<PhraseIterationRow> iterations, bool canPropagate, string deleteTip)
    : ObservableObject, IPhraseGroupRow
{
    public string Name { get; } = name;
    public string CountLabel { get; } = countLabel;
    public IReadOnlyList<PhraseIterationRow> Iterations { get; } = iterations;
    IReadOnlyList<IPhraseIterationRow> IPhraseGroupRow.Iterations => Iterations;
    public bool CanPropagate { get; } = canPropagate;

    /// <summary>What Delete removes, in words: the button's tooltip and accessible name.</summary>
    public string DeleteTip { get; } = deleteTip;

    /// <summary>The rename box; Apply shows only once it differs from <see cref="Name" />.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameDirty))]
    private string _editName = name;

    public bool IsRenameDirty =>
        EditName.Trim().Length > 0 && !string.Equals(EditName.Trim(), Name, StringComparison.Ordinal);

    /// <summary>The group shows its iterations; the chevron folds them away.</summary>
    [ObservableProperty] private bool _isExpanded = true;
}

/// <summary>
///     The ONE Phrases panel: lists every phrase's iterations from both phrase stores
///     (<see cref="PhraseMarkers.Iterations" />, what the exporters read), renames and deletes a
///     phrase (<see cref="PhraseEdits" />, one undo node each), jumps the editor to an iteration
///     (the deliberate-seek funnel), and propagates a CHOSEN iteration's content to all others —
///     the Tools-menu power tool only ever takes the iteration under the playhead. Deliberately
///     STANDALONE (composed as a property on <see cref="InspectorViewModel"/>):
///     the inspector VM ceiling forbids restructuring, so this adds one
///     property + mechanical tab wiring there and nothing else. All
///     dependencies are injected seams, so the browser tests headless without
///     a chart editor.
/// </summary>
public partial class PhrasesInspectorViewModel : ViewModelBase, IPhrasesScreen
{
    private readonly Func<Arrangement?> _activeArrangement;
    private readonly Func<IList<BeatMarker>> _beats;
    private readonly Action<double> _seek;
    private readonly IPhraseIterationPropagationService? _propagation;
    private readonly ILocalizationService _localization;
    private readonly IHistoryService _history;

    public PhrasesInspectorViewModel(Func<Arrangement?> activeArrangement, Func<IList<BeatMarker>> beats,
        Action<double> seek, IPhraseIterationPropagationService? propagation, ILocalizationService localization,
        IHistoryService history)
    {
        _localization = localization;
        _activeArrangement = activeArrangement;
        _beats = beats;
        _seek = seek;
        _propagation = propagation;
        _history = history;
        // An undo, or a phrase edit on the lane, moves the rows; the fingerprint keeps it cheap.
        history.HistoryChanged += EnsureCurrent;
    }

    public ObservableCollection<PhraseGroupRow> Phrases { get; } = [];
    IReadOnlyList<IPhraseGroupRow> IPhrasesScreen.Phrases => Phrases;
    ICommand IPhrasesScreen.RefreshCommand => RefreshCommand;
    ICommand IPhrasesScreen.RenamePhraseCommand => RenamePhraseCommand;
    ICommand IPhrasesScreen.CancelRenameCommand => CancelRenameCommand;
    ICommand IPhrasesScreen.DeletePhraseCommand => DeletePhraseCommand;
    ICommand IPhrasesScreen.JumpToCommand => JumpToCommand;
    ICommand IPhrasesScreen.PropagateCommand => PropagateCommand;

    [ObservableProperty] private bool _hasPhrases;
    [ObservableProperty] private string _statusLine = "";

    /// <summary>What an export adds that the arrangement does not carry (COUNT, END); empty when nothing.</summary>
    [ObservableProperty] private string _exportNote = "";

    private PhraseIterationRow? _confirmingRow;

    // What the rows were last built from: activating the tab rebuilds only
    // when this moved. Rebuilding cleared and re-added every card — an un-virtualized
    // list of dozens of cards, each with its own iteration list — so every switch to
    // the tab paid a full re-template and layout even when nothing had changed.
    private int? _builtFrom;

    /// <summary>Rebuilds the browser from the active arrangement (called on
    /// tab activation and by the refresh button — never per tick).</summary>
    [RelayCommand]
    public void Refresh()
    {
        RebuildRows();
        StatusLine = "";
    }

    [RelayCommand]
    private void RenamePhrase(PhraseGroupRow? row)
    {
        var arrangement = _activeArrangement();
        if (row is not { IsRenameDirty: true } || arrangement == null) return;
        var newName = row.EditName.Trim();
        var action = PhraseEdits.PlanRename(arrangement, row.Name, newName);
        if (action == null || !_history.Do(action)) return;
        RebuildRows();
        StatusLine = _localization.Format("Inspector_Phrases_Renamed", row.Name, newName);
    }

    /// <summary>Esc in the rename box: back to the phrase's name.</summary>
    [RelayCommand]
    private void CancelRename(PhraseGroupRow? row) => row?.EditName = row.Name;

    [RelayCommand]
    private void DeletePhrase(PhraseGroupRow? row)
    {
        var arrangement = _activeArrangement();
        if (row == null || arrangement == null) return;
        var action = PhraseEdits.PlanDelete(arrangement, row.Name);
        if (action == null || !_history.Do(action)) return;
        RebuildRows();
        StatusLine = _localization.Plural("Inspector_Phrases_DeletedOne", "Inspector_Phrases_DeletedFmt",
            row.Iterations.Count, row.Name);
    }

    [RelayCommand]
    private void JumpTo(PhraseIterationRow? row)
    {
        if (row != null) _seek(row.TimeMs);
    }

    /// <summary>Two-step destructive propagate: the first click ARMS this row
    /// (button relabels, status names the rewrite count); the second executes
    /// through the history-integrated engine. Arming another row — or a
    /// refresh — disarms.</summary>
    [RelayCommand]
    private void Propagate(PhraseIterationRow? row)
    {
        if (row is not { CanPropagate: true } || _propagation == null) return;

        if (!ReferenceEquals(_confirmingRow, row))
        {
            Disarm();
            _confirmingRow = row;
            row.IsConfirming = true;
            var others = CountOtherIterations(row);
            StatusLine = _localization.Plural("Inspector_Phrases_PropagateConfirmOne", "Inspector_Phrases_PropagateConfirmFmt", others);
            return;
        }

        Disarm();
        var rewritten = _propagation.PropagateToAllIterations(row.Iteration);
        RebuildRows();
        StatusLine = _localization.Plural("Inspector_Phrases_PropagatedOne", "Inspector_Phrases_PropagatedFmt", rewritten);
    }

    private void Disarm()
    {
        _confirmingRow?.IsConfirming = false;
        _confirmingRow = null;
    }

    private int CountOtherIterations(PhraseIterationRow row)
    {
        var arrangement = _activeArrangement();
        if (arrangement == null) return 0;
        return PhraseMarkers.Iterations(arrangement).Iterations.Count(iteration =>
            iteration.PhraseId == row.Iteration.PhraseId && iteration.Position != row.Iteration.Position);
    }

    /// <summary>
    /// Tab activation: rebuild only when the arrangement's phrases moved
    ///     since the rows were built. The Refresh button still always rebuilds.
    /// </summary>
    public void EnsureCurrent()
    {
        if (_builtFrom == Fingerprint()) return;
        Refresh();
    }

    /// <summary>The arrangement, its phrase names and every iteration's phrase and
    /// time — everything a row shows. Cheap next to the rebuild it saves.</summary>
    private int Fingerprint()
    {
        var arrangement = _activeArrangement();
        if (arrangement == null) return 0;
        var beats = _beats();
        var hash = new HashCode();
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(arrangement));
        var (phrases, iterations) = PhraseMarkers.Iterations(arrangement);
        foreach (var name in phrases) hash.Add(name);
        foreach (var iteration in iterations)
        {
            hash.Add(iteration.PhraseId);
            hash.Add(iteration.Position.GetPositionMs(beats));
        }

        return hash.ToHashCode();
    }

    /// <summary>
    ///     A chart whose phrases are markers only gets COUNT at the start and END after the
    ///     last note on export when it has none (<see cref="PhraseMarkers.Iterations" />).
    /// </summary>
    private string ExportNoteFor(Arrangement arrangement, IReadOnlyList<string> phrases)
    {
        if (arrangement.Phrases.Count > 0 && arrangement.PhraseIterations.Count > 0) return "";
        var addsCount = !phrases.Contains("COUNT", StringComparer.OrdinalIgnoreCase);
        var addsEnd = !phrases.Contains("END", StringComparer.OrdinalIgnoreCase);
        return (addsCount, addsEnd) switch
        {
            (true, true) => _localization["Inspector_Phrases_ExportAddsCountEnd"],
            (true, false) => _localization["Inspector_Phrases_ExportAddsCount"],
            (false, true) => _localization["Inspector_Phrases_ExportAddsEnd"],
            _ => ""
        };
    }

    private void RebuildRows()
    {
        Disarm();
        _builtFrom = Fingerprint();
        Phrases.Clear();

        var arrangement = _activeArrangement();
        // Both stores: a chart placed in the editor or imported from EOF keeps its phrases
        // as markers only.
        var (phrases, iterations) = arrangement == null
            ? new PhraseMarkers.IterationSet([], [])
            : PhraseMarkers.Iterations(arrangement);
        if (iterations.Count > 0)
        {
            var beats = _beats();
            var byPhrase = new Dictionary<int, List<PhraseIterationRow>>();
            foreach (var iteration in iterations)
            {
                if (!byPhrase.TryGetValue(iteration.PhraseId, out var list))
                    byPhrase[iteration.PhraseId] = list = [];
                list.Add(new PhraseIterationRow(iteration, 0, iteration.Position.GetPositionMs(beats), ""));
            }

            var loc = _localization;
            foreach (var (phraseId, rows) in byPhrase.OrderBy(p =>
                         p.Value.Min(r => r.TimeMs)))
            {
                rows.Sort(static (a, b) => a.TimeMs.CompareTo(b.TimeMs));
                // Re-materialize with time-ordered ordinals (rows are cheap).
                var ordered = new List<PhraseIterationRow>(rows.Count);
                for (var i = 0; i < rows.Count; i++)
                {
                    var label = MarkerOccurrenceLabel.Format(_localization, i + 1, beats, rows[i].TimeMs);
                    ordered.Add(new PhraseIterationRow(rows[i].Iteration, i + 1, rows[i].TimeMs, label)
                    {
                        CanPropagate = rows.Count > 1
                    });
                }

                var name = phraseId >= 0 && phraseId < phrases.Count
                    ? phrases[phraseId]
                    : $"#{phraseId}";
                Phrases.Add(new PhraseGroupRow(
                    name,
                    loc.Plural("Inspector_Phrases_IterationsOne", "Inspector_Phrases_IterationsFmt",
                        ordered.Count),
                    ordered,
                    ordered.Count > 1,
                    loc.Plural("Inspector_Phrases_DeleteTipOne", "Inspector_Phrases_DeleteTipFmt", ordered.Count, name)));
            }
        }

        HasPhrases = Phrases.Count > 0;
        ExportNote = arrangement == null || !HasPhrases ? "" : ExportNoteFor(arrangement, phrases);
    }
}
