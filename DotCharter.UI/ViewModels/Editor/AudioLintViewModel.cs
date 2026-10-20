using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Linting;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>One audio-lint finding row: localized description + click-to-jump.</summary>
public sealed record AudioLintFindingRow(double TimeMs, string TimeLabel, string Description);

/// <summary>
///     v0.29 audio-verified lint surface (an explicit-run block on the FEHLER
///     tab): asks <see cref="IAudioChartLintService" /> to verify the active
///     arrangement against the loaded song audio and lists the mismatches
///     ("charted C5 at 1:23.4 — audio says B4") as jump rows. STANDALONE
///     (inspector VM ceiling): composed as a property, seams injected.
///     Findings persist until the next run — they are NOT part of
///     RecomputeIssues (which is synchronous and rebuilds constantly); an
///     explicit heavyweight analysis stays explicit.
///     <para>
///         "Until the next run" EXCLUDES A CHART SWITCH. Every finding is a
///         TIMESTAMP into the song that was analysed, so
///         the moment the chart underneath changes the rows name positions in a
///         song that is not loaded any more and <see cref="JumpTo" /> would seek the NEW
///         song to the OLD one's times. Two halves prevent it: <see cref="Reset" />,
///         which the inspector calls on a project or arrangement change, and the
///         staleness check in <see cref="RunAsync" /> — a run is tens of seconds
///         of decode + CQT and the charter is free to switch chart while it
///         works, so a result that arrives for an arrangement nobody is looking
///         at is discarded rather than listed against its successor.
///     </para>
/// </summary>
public partial class AudioLintViewModel : ViewModelBase
{
    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private readonly Func<Arrangement?> _activeArrangement;
    private readonly Func<IList<BeatMarker>> _beats;
    private readonly Func<string?> _audioPath;
    private readonly Action<double> _seek;
    private readonly IAudioChartLintService? _service;
    private readonly ILocalizationService _localization;

    public AudioLintViewModel(Func<Arrangement?> activeArrangement, Func<IList<BeatMarker>> beats,
        Func<string?> audioPath, Action<double> seek, IAudioChartLintService? service,
        ILocalizationService localization)
    {
        _localization = localization;
        _activeArrangement = activeArrangement;
        _beats = beats;
        _audioPath = audioPath;
        _seek = seek;
        _service = service;
    }

    public ObservableCollection<AudioLintFindingRow> AudioFindings { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _summaryLine = "";

    public bool CanRun => _service != null;

    /// <summary>Cancels the run in flight; null while nothing is running.</summary>
    private CancellationTokenSource? _runCts;

    /// <summary>Bumped by <see cref="Reset" />. A run whose generation is stale
    /// when it returns has been superseded, whatever it found.</summary>
    private int _generation;

    /// <summary>
    ///     Forget the last run — its rows, its summary and the fact that it ran
    ///     — and cancel one in flight. Called by the inspector when the project
    ///     or the active arrangement changes: the findings are times into the
    ///     song that WAS loaded, so keeping them is worse than showing nothing.
    /// </summary>
    public void Reset()
    {
        unchecked { _generation++; }
        _runCts?.Cancel();
        ClearResult();
    }

    private void ClearResult()
    {
        AudioFindings.Clear();
        SummaryLine = "";
        HasResult = false;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (_service == null || IsRunning) return;
        var arrangement = _activeArrangement();
        var loc = _localization;
        if (arrangement == null)
        {
            SummaryLine = loc["Inspector_AudioLint_NoArrangement"];
            HasResult = true;
            return;
        }

        int generation;
        unchecked { generation = ++_generation; }
        using var cts = new CancellationTokenSource();
        _runCts = cts;

        IsRunning = true;
        SummaryLine = loc["Inspector_AudioLint_Running"];
        AudioFindings.Clear();
        var superseded = false;
        try
        {
            var outcome = await _service.LintAsync(arrangement, _beats(), _audioPath(), cts.Token);

            // The chart can change while a whole-song decode runs. A result
            // belongs to the arrangement it was ASKED about; against any other
            // one its times point into a song that is not loaded.
            if (generation != _generation || !ReferenceEquals(_activeArrangement(), arrangement))
            {
                superseded = true;
                ClearResult();
                return;
            }

            if (!outcome.Ran)
            {
                SummaryLine = outcome.Error == "no-audio"
                    ? loc["Inspector_AudioLint_NoAudio"]
                    : loc["Inspector_AudioLint_DecodeFailed"];
            }
            else
            {
                foreach (var finding in outcome.Findings)
                {
                    var time = TimeSpan.FromMilliseconds(Math.Max(0, finding.TimeMs)).ToString(@"m\:ss\.f");
                    var description = finding.ObservedMidi is { } observed
                        ? loc.Format("Inspector_AudioLint_FindingFmt",
                            NoteName(finding.ChartedMidi), time, NoteName(observed))
                        : loc.Format("Inspector_AudioLint_FindingSilentFmt",
                            NoteName(finding.ChartedMidi), time);
                    AudioFindings.Add(new AudioLintFindingRow(finding.TimeMs, time, description));
                }

                SummaryLine = loc.Format("Inspector_AudioLint_SummaryFmt",
                    outcome.SoundsVerified, outcome.SoundsChecked, outcome.Findings.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // Reset cancelled it — the panel has already been cleared, and a
            // cancelled run has nothing to report.
            superseded = true;
            ClearResult();
        }
        catch (Exception ex)
        {
            SummaryLine = loc.Format("Inspector_AudioLint_Failed", ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_runCts, cts)) _runCts = null;
            IsRunning = false;
            if (!superseded) HasResult = true;
        }
    }

    [RelayCommand]
    private void JumpTo(AudioLintFindingRow? row)
    {
        if (row != null) _seek(row.TimeMs);
    }

    /// <summary>"C5"-style display name (MIDI 60 = C4 convention).</summary>
    internal static string NoteName(int midi)
        => $"{NoteNames[((midi % 12) + 12) % 12]}{midi / 12 - 1}";
}
