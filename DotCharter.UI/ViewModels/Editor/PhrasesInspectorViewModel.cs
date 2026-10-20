using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>One phrase iteration in the browser. Carries per-row two-step
/// confirm state for the destructive propagate action.</summary>
public partial class PhraseIterationRow : ObservableObject
{
    public PhraseIterationRow(PhraseIteration iteration, int ordinal, double timeMs)
    {
        Iteration = iteration;
        Ordinal = ordinal;
        TimeMs = timeMs;
        TimeLabel = TimeSpan.FromMilliseconds(Math.Max(0, timeMs)).ToString(@"m\:ss\.f");
    }

    public PhraseIteration Iteration { get; }
    public int Ordinal { get; }
    public double TimeMs { get; }
    public string TimeLabel { get; }
    public string Label => $"#{Ordinal} · {TimeLabel}";

    /// <summary>The phrase has other iterations to copy this one into.</summary>
    public bool CanPropagate { get; init; }

    /// <summary>Armed by the first propagate click; the second executes.</summary>
    [ObservableProperty] private bool _isConfirming;
}

/// <summary>One phrase group: its name and every iteration, time-ordered.</summary>
public sealed record PhraseGroupRow(
    string Name, string CountLabel, IReadOnlyList<PhraseIterationRow> Iterations, bool CanPropagate);

/// <summary>
///     v0.29 phrase-iteration browser (the authoring surface over the shipped
///     propagation engine): lists every phrase's iterations, jumps the editor
///     to one (the deliberate-seek funnel), and propagates a CHOSEN
///     iteration's content to all others — the existing Tools-menu power tool
///     only ever takes the iteration under the playhead. Deliberately
///     STANDALONE (composed as a property on <see cref="InspectorViewModel"/>):
///     the inspector VM ceiling forbids restructuring, so this adds one
///     property + mechanical tab wiring there and nothing else. All
///     dependencies are injected seams, so the browser tests headless without
///     a chart editor.
/// </summary>
public partial class PhrasesInspectorViewModel : ViewModelBase
{
    private readonly Func<Arrangement?> _activeArrangement;
    private readonly Func<IList<BeatMarker>> _beats;
    private readonly Action<double> _seek;
    private readonly IPhraseIterationPropagationService? _propagation;
    private readonly ILocalizationService _localization;

    public PhrasesInspectorViewModel(Func<Arrangement?> activeArrangement, Func<IList<BeatMarker>> beats,
        Action<double> seek, IPhraseIterationPropagationService? propagation, ILocalizationService localization)
    {
        _localization = localization;
        _activeArrangement = activeArrangement;
        _beats = beats;
        _seek = seek;
        _propagation = propagation;
    }

    public ObservableCollection<PhraseGroupRow> Phrases { get; } = [];

    [ObservableProperty] private bool _hasPhrases;
    [ObservableProperty] private string _statusLine = "";

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
        var count = 0;
        foreach (var iteration in arrangement.PhraseIterations)
        {
            if (iteration.PhraseId == row.Iteration.PhraseId && !ReferenceEquals(iteration, row.Iteration))
                count++;
        }

        return count;
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
        foreach (var name in arrangement.Phrases) hash.Add(name);
        foreach (var iteration in arrangement.PhraseIterations)
        {
            hash.Add(iteration.PhraseId);
            hash.Add(iteration.Position.GetPositionMs(beats));
        }

        return hash.ToHashCode();
    }

    private void RebuildRows()
    {
        Disarm();
        _builtFrom = Fingerprint();
        Phrases.Clear();

        var arrangement = _activeArrangement();
        if (arrangement != null && arrangement.PhraseIterations.Count > 0)
        {
            var beats = _beats();
            var byPhrase = new Dictionary<int, List<PhraseIterationRow>>();
            foreach (var iteration in arrangement.PhraseIterations)
            {
                if (!byPhrase.TryGetValue(iteration.PhraseId, out var list))
                    byPhrase[iteration.PhraseId] = list = [];
                list.Add(new PhraseIterationRow(iteration, 0, iteration.Position.GetPositionMs(beats)));
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
                    ordered.Add(new PhraseIterationRow(rows[i].Iteration, i + 1, rows[i].TimeMs)
                    {
                        CanPropagate = rows.Count > 1
                    });
                }

                var name = phraseId >= 0 && phraseId < arrangement.Phrases.Count
                    ? arrangement.Phrases[phraseId]
                    : $"#{phraseId}";
                Phrases.Add(new PhraseGroupRow(
                    name,
                    loc.Plural("Inspector_Phrases_IterationsOne", "Inspector_Phrases_IterationsFmt",
                        ordered.Count),
                    ordered,
                    ordered.Count > 1));
            }
        }

        HasPhrases = Phrases.Count > 0;
    }
}
