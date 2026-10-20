using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The rig footer's DROP TUNE pedal: shifts the live guitar input DOWN by whole
///     semitones (0…−12) before the chain, so a song in Drop C plays on a guitar
///     left in D standard. Down only, because the engine's shifter is: it tunes
///     down with no latency floor, and a live shift UP cannot be done without a
///     delay the player hears (JuceSharp's dcdsp_droptune.h). The list offers
///     only drops: a "+" step would do nothing. The engine half already exists (the input pitch
///     shifter, per seat, replayed on a rebuild); this is the control.
///     <para>
///         It owns NO state. The host hands it the one place the value lives —
///         DotCharter the transport's <c>InputPitchShiftSemis</c> (so this and the
///         transport's own picker can never disagree), RigLab the engine plus its
///         config — and calls <see cref="Refresh" /> when that changes elsewhere.
///     </para>
/// </summary>
public sealed partial class RigDropTuneViewModel : ObservableObject
{
    public const int MaxSemitones = 12;

    private readonly Func<int> _read;
    private readonly Action<int> _write;

    public RigDropTuneViewModel(Func<int> read, Action<int> write)
    {
        _read = read;
        _write = write;
        Steps = RigDropTuneStep.All;
    }

    /// <summary>0 down to −12: off at the top, the deepest drop at the bottom.</summary>
    public IReadOnlyList<RigDropTuneStep> Steps { get; }

    /// <summary>The shift in force, clamped to 0…−12.</summary>
    public int Semitones
    {
        get => Math.Clamp(_read(), -MaxSemitones, 0);
        set
        {
            var clamped = Math.Clamp(value, -MaxSemitones, 0);
            if (clamped == Semitones) return;
            _write(clamped);
            Refresh();
        }
    }

    /// <summary>The picker's row for <see cref="Semitones" />.</summary>
    public RigDropTuneStep SelectedStep
    {
        get => Steps.First(s => s.Semitones == Semitones);
        set
        {
            if (value is not null) Semitones = value.Semitones;
        }
    }

    /// <summary>A shift is on (the footer marks the pedal as engaged).</summary>
    public bool IsEngaged => Semitones != 0;

    /// <summary>The value changed somewhere else (the transport's picker, a song
    /// that set its own drop tune): re-read it.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Semitones));
        OnPropertyChanged(nameof(SelectedStep));
        OnPropertyChanged(nameof(IsEngaged));
    }
}

/// <summary>One row of the drop-tune picker: "0", "−2", "+3".</summary>
public sealed record RigDropTuneStep(int Semitones)
{
    /// <summary>+12 down to −12 — the one list every drop-tune picker offers (the
    /// rig footer's pedal and DotCharter's Amp/FX panel), so the two read alike.</summary>
    public static IReadOnlyList<RigDropTuneStep> All { get; } =
        Enumerable.Range(-RigDropTuneViewModel.MaxSemitones, RigDropTuneViewModel.MaxSemitones + 1)
            .Reverse()
            .Select(s => new RigDropTuneStep(s))
            .ToList();

    /// <summary>The same list as plain semitone counts, for a picker bound
    /// straight to an <see cref="int" /> (DotCharter's Amp/FX panel binds the
    /// transport's <c>InputPitchShiftSemis</c>).</summary>
    public static IReadOnlyList<int> AllSemitones { get; } = All.Select(s => s.Semitones).ToList();

    public string Label => Semitones switch
    {
        0 => "0",
        < 0 => "−" + (-Semitones),
        _ => "+" + Semitones
    };

    public override string ToString() => Label;
}
