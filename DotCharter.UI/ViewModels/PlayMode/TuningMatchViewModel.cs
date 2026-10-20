using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>One row of the "my tuning" picker: a GUITAR tuning the rig's tuner knows. A bass
/// shares a guitar's low four strings, so one list serves both paths.</summary>
public sealed record MyTuningChoice(TunerTuning Tuning)
{
    public string Name => Tuning.Name;

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Get Ready's "Play in my tuning" row: the player's own tuning and the opt-in that
///     plays a uniformly detuned path with the SONG shifted to it, instead of asking the player
///     to retune. Both are per profile (<see cref="PlayerProfile.MyTuningId" />,
///     <see cref="PlayerProfile.PlayInMyTuning" />).
///     <para>
///         Whether a path CAN be shifted is <see cref="TuningMatchTranspose" />'s verdict. A path
///         it refuses shows the switch OFF and disabled with the reason printed under it — never a
///         stored "on" that the run would silently ignore. <see cref="EffectiveSemitones" /> is the
///         one number the run reads: what the song is shifted by, the tuning check retargets by and
///         judging expects the frets that much higher by.
///     </para>
///     <para>
///         A collaborator rather than another <c>PlayModeViewModel</c> partial: it shares nothing
///         with the screen but the selected path, which the screen hands it.
///     </para>
/// </summary>
public sealed partial class TuningMatchViewModel : ObservableObject
{
    private readonly IPlayerProfileService _profiles;
    private readonly ILocalizationService _localization;
    private ArrangementOption? _option;
    private bool _loading;
    private bool _dropTunerOn;

    public TuningMatchViewModel(IPlayerProfileService profiles, ILocalizationService localization)
    {
        _profiles = profiles;
        _localization = localization;
        Choices = TunerTunings.All.Where(t => !t.IsBass).Select(t => new MyTuningChoice(t)).ToList();
        _selectedMyTuning = Choices[0];
        LoadFromProfile();
        Recompute();
    }

    /// <summary>The guitar tunings, E standard first (the default).</summary>
    public IReadOnlyList<MyTuningChoice> Choices { get; }

    /// <summary>The player's own tuning. Persisted per profile on every change.</summary>
    [ObservableProperty] private MyTuningChoice _selectedMyTuning;

    /// <summary>The stored opt-in. Persisted per profile; it survives a path that cannot honour
    /// it, which then shows the switch off (<see cref="IsOn" />).</summary>
    [ObservableProperty] private bool _playInMyTuning;

    /// <summary>The selected path can be played in the player's tuning by shifting the song, and
    /// the drop tuner is not already answering it.</summary>
    [ObservableProperty] private bool _canPlayInMyTuning;

    /// <summary>"Play in my tuning (+1 semitone)" when the path can be shifted, else the bare name.</summary>
    [ObservableProperty] private string _toggleLabel = "";

    /// <summary>What the switch does, or why it is disabled. Empty with no path.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>The verdict for the selected path.</summary>
    public TuningMatch Match { get; private set; }

    /// <summary>The switch's state: on only where it can be honoured.</summary>
    public bool IsOn
    {
        get => PlayInMyTuning && CanPlayInMyTuning;
        set
        {
            if (!CanPlayInMyTuning) return;
            PlayInMyTuning = value;
        }
    }

    /// <summary>The semitones a run shifts the song by: the verdict's step when opted in and the
    /// path allows it, else 0.</summary>
    public int EffectiveSemitones => IsOn ? Match.Semitones : 0;

    /// <summary>The player's tuning's name, for the tuning lockup while shifted.</summary>
    public string MyTuningName => SelectedMyTuning.Name;

    /// <summary>Raised after anything a run or the screen reads changed.</summary>
    public event Action? Changed;

    /// <summary>The drop tuner is on for this path. It is the other answer to the same
    /// question and it wins, so while it is on this switch reads off and disabled with that
    /// reason, and the stored opt-in survives.</summary>
    public void SetDropTunerOn(bool on)
    {
        if (_dropTunerOn == on) return;
        _dropTunerOn = on;
        Recompute();
    }

    /// <summary>Re-answers the verdict for the path the screen selected.</summary>
    public void Refresh(ArrangementOption? option)
    {
        _option = option;
        Recompute();
    }

    /// <summary>The active profile changed: read its tuning and opt-in.</summary>
    public void ReloadProfile()
    {
        LoadFromProfile();
        Recompute();
    }

    partial void OnSelectedMyTuningChanged(MyTuningChoice value)
    {
        Persist();
        Recompute();
    }

    partial void OnPlayInMyTuningChanged(bool value)
    {
        Persist();
        Recompute();
    }

    private void LoadFromProfile()
    {
        _loading = true;
        try
        {
            var profile = _profiles.ActiveProfile;
            SelectedMyTuning = Choices.FirstOrDefault(c => c.Tuning.Id == profile.MyTuningId) ?? Choices[0];
            PlayInMyTuning = profile.PlayInMyTuning;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Persist()
    {
        if (_loading) return;
        _profiles.SetTuningMatch(_profiles.ActiveProfile.Id, SelectedMyTuning.Tuning.Id, PlayInMyTuning);
    }

    private void Recompute()
    {
        if (_loading) return;
        var arrangement = _option?.Arrangement;
        Match = arrangement == null
            ? new TuningMatch(TuningMatchVerdict.UnknownTuning, 0)
            : TuningMatchTranspose.Resolve(arrangement, SelectedMyTuning.Tuning.Steps);
        CanPlayInMyTuning = Match.CanShift && !_dropTunerOn;
        ToggleLabel = Match.CanShift
            ? _localization.PluralWith("PlayMode_TuningMatch_ToggleOne", "PlayMode_TuningMatch_Toggle",
                Math.Abs(Match.Semitones), Match.Semitones.ToString("+0;-0", CultureInfo.InvariantCulture))
            : _localization["PlayMode_TuningMatch_Title"];
        StatusText = _option == null ? ""
            : _dropTunerOn ? _localization["PlayMode_TuningMatch_DropTunerWins"]
            : Explain(_option);
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(EffectiveSemitones));
        OnPropertyChanged(nameof(MyTuningName));
        Changed?.Invoke();
    }

    /// <summary>The consequence line: what the switch does, or the reason it cannot.</summary>
    private string Explain(ArrangementOption option) => Match.Verdict switch
    {
        TuningMatchVerdict.Shift => _localization.Format("PlayMode_TuningMatch_Shift", MyTuningName),
        TuningMatchVerdict.AlreadyMatches => _localization.Format("PlayMode_TuningMatch_Same", MyTuningName),
        TuningMatchVerdict.NotUniform => _localization.Format("PlayMode_TuningMatch_NotUniform",
            option.TuningDisplayLabel, MyTuningName),
        TuningMatchVerdict.TooFar => _localization.Format("PlayMode_TuningMatch_TooFar",
            option.TuningDisplayLabel, TuningMatchTranspose.MaxShiftSemitones),
        TuningMatchVerdict.UnsupportedStringCount => _localization.Format("PlayMode_TuningMatch_StringCount",
            ArrangementTunerLogic.ResolvedStringCount(option.Arrangement)),
        _ => _localization["PlayMode_TuningMatch_Unknown"]
    };
}
