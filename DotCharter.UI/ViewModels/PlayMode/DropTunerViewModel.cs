using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>Why the drop tuner can or cannot play the selected path.</summary>
public enum DropTunerVerdict
{
    /// <summary>The path is in standard (or says nothing about its tuning): nothing to shift, no row.</summary>
    NotNeeded,

    /// <summary>Every string sits the same distance below standard, within reach of the shifter.</summary>
    Shift,

    /// <summary>The strings sit different distances from standard; one input shift moves them all.</summary>
    NotUniform,

    /// <summary>The path is tuned above standard; the engine's shifter only tunes down.</summary>
    AboveStandard,

    /// <summary>Further below standard than the shifter reaches (<see cref="DropTunerViewModel.MaxDropSemitones" />).</summary>
    TooFar
}

/// <summary>
/// Get Ready's per-song drop tuner: play a path tuned uniformly BELOW standard with
///     the guitar left in standard, the engine shifting the player's input down to the song.
///     <para>
///         The opt-in is remembered PER SONG (<see cref="AppConfig.DropTunerSongs" />, keyed by
///         <see cref="PlaySongItem.SongIdentity" />). A path the engine cannot honour shows the
///         switch off and disabled with the reason printed, and the stored opt-in survives it.
///         <see cref="EffectiveSemitones" /> is the one number a run reads.
///     </para>
///     <para>
/// It is the other answer to the question "Play in my tuning" answers, and it wins
///         when both are on: <see cref="TuningMatchViewModel.SetDropTunerOn" /> shows that row
///         disabled with the reason while this one <see cref="IsOn" />.
///     </para>
/// </summary>
public sealed partial class DropTunerViewModel : ObservableObject
{
    /// <summary>How far down the run can shift the input: <c>TransportViewModel.InputPitchShiftSemis</c>
    /// clamps to this, so a deeper tuning would be played silently out of tune.</summary>
    public const int MaxDropSemitones = 12;

    private readonly IConfigurationService? _config;
    private readonly ILocalizationService _localization;
    private ArrangementOption? _option;
    private string? _songKey;
    private bool _loading;

    public DropTunerViewModel(IConfigurationService? config, ILocalizationService localization)
    {
        _config = config;
        _localization = localization;
        Recompute();
    }

    /// <summary>The stored per-song opt-in. It survives a path that cannot honour it, which then
    /// shows the switch off (<see cref="IsOn" />).</summary>
    [ObservableProperty] private bool _useDropTuner;

    /// <summary>The selected path can be played through the drop tuner.</summary>
    [ObservableProperty] private bool _canUseDropTuner;

    /// <summary>The row is shown: the path is not in standard, so the drop tuner is a question.</summary>
    [ObservableProperty] private bool _isApplicable;

    /// <summary>"Drop tuner (-2 semitones, experimental)" when the path can be shifted, else the bare title.</summary>
    [ObservableProperty] private string _toggleLabel = "";

    /// <summary>What the switch does, or why it is disabled. Empty when the row is hidden.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>The verdict for the selected path.</summary>
    public DropTunerVerdict Verdict { get; private set; }

    /// <summary>The path's uniform step against standard (negative = below); 0 when not uniform.</summary>
    public int Step { get; private set; }

    /// <summary>The switch's state: on only where it can be honoured.</summary>
    public bool IsOn
    {
        get => UseDropTuner && CanUseDropTuner;
        set
        {
            if (!CanUseDropTuner) return;
            UseDropTuner = value;
        }
    }

    /// <summary>The semitones a run shifts the input by (negative), or 0 when off.</summary>
    public int EffectiveSemitones => IsOn ? Step : 0;

    /// <summary>The tuning the guitar stays in while the drop tuner is on: standard, named for
    /// the path's string count ("E Standard").</summary>
    public string StandardTuningName { get; private set; } = "";

    /// <summary>Raised after anything a run or the screen reads changed.</summary>
    public event Action? Changed;

    /// <summary>A song was picked: read its stored opt-in. A song with no identity (nothing
    /// loaded) starts off and is never written.</summary>
    public void LoadSong(string? songKey)
    {
        _songKey = string.IsNullOrEmpty(songKey) ? null : songKey;
        _loading = true;
        try
        {
            UseDropTuner = _songKey != null &&
                           (_config?.LoadAppConfig().DropTunerSongs
                               .Contains(_songKey, StringComparer.OrdinalIgnoreCase) ?? false);
        }
        finally
        {
            _loading = false;
        }

        Recompute();
    }

    /// <summary>Re-answers the verdict for the path the screen selected.</summary>
    public void Refresh(ArrangementOption? option)
    {
        _option = option;
        Recompute();
    }

    partial void OnUseDropTunerChanged(bool value)
    {
        Persist(value);
        Recompute();
    }

    private void Persist(bool on)
    {
        if (_loading || _config == null || _songKey == null) return;
        var config = _config.LoadAppConfig();
        var stored = config.DropTunerSongs.FindIndex(k => string.Equals(k, _songKey, StringComparison.OrdinalIgnoreCase));
        // Off is the default: keep the config free of no-ops.
        if (on == stored >= 0) return;
        if (on) config.DropTunerSongs.Add(_songKey);
        else config.DropTunerSongs.RemoveAt(stored);
        _config.SaveAppConfig(config);
    }

    /// <summary>The engine's answer for a path's uniform step against standard (null = not uniform).
    /// Pure.</summary>
    public static DropTunerVerdict Judge(int? uniformStep) => uniformStep switch
    {
        null => DropTunerVerdict.NotUniform,
        0 => DropTunerVerdict.NotNeeded,
        > 0 => DropTunerVerdict.AboveStandard,
        < -MaxDropSemitones => DropTunerVerdict.TooFar,
        _ => DropTunerVerdict.Shift
    };

    private void Recompute()
    {
        if (_loading) return;
        var step = _option?.UniformTuningStep;
        Verdict = _option == null ? DropTunerVerdict.NotNeeded : Judge(step);
        Step = Verdict == DropTunerVerdict.Shift ? step!.Value : 0;
        IsApplicable = Verdict != DropTunerVerdict.NotNeeded;
        CanUseDropTuner = Verdict == DropTunerVerdict.Shift;
        StandardTuningName = _option == null ? "" : StandardNameFor(_option.Arrangement);
        // PluralWith: {0} is the SIGNED step as the player reads it ("-2"); the form is decided by
        // its magnitude, so -1 is singular.
        ToggleLabel = CanUseDropTuner
            ? _localization.PluralWith("PlayMode_DropTuner_LabelOne", "PlayMode_DropTuner_Label",
                Math.Abs(Step), Step.ToString("+0;-0", CultureInfo.InvariantCulture))
            : _localization["PlayMode_DropTuner_Title"];
        StatusText = IsApplicable ? Explain(_option!) : "";
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(EffectiveSemitones));
        Changed?.Invoke();
    }

    /// <summary>Standard tuning named for the path's string count; a 5- or 7-string path, which
    /// the name table does not cover, falls back to the 6-string name.</summary>
    private static string StandardNameFor(Arrangement arrangement)
    {
        var strings = ArrangementTunerLogic.ResolvedStringCount(arrangement);
        return TuningNameResolver.ResolveFromOffsets(strings, new int[strings])
               ?? TuningNameResolver.ResolveFromOffsets(6, new int[6])
               ?? "";
    }

    /// <summary>The consequence line: what the switch does, or the reason it cannot.</summary>
    private string Explain(ArrangementOption option) => Verdict switch
    {
        DropTunerVerdict.Shift => _localization.Format("PlayMode_DropTuner_Shift", StandardTuningName),
        DropTunerVerdict.AboveStandard => _localization.Format("PlayMode_DropTuner_AboveStandard",
            option.TuningDisplayLabel),
        DropTunerVerdict.TooFar => _localization.Format("PlayMode_DropTuner_TooFar",
            option.TuningDisplayLabel, MaxDropSemitones),
        _ => _localization.Format("PlayMode_DropTuner_NotUniform", option.TuningDisplayLabel)
    };
}
