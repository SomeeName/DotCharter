using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     The mic placement over a REAL mic model — the native <c>micpos</c> unit ("Cab
///     Mic Position"): which microphone (<c>mic</c>: 57 / 121 / 421 / condenser /
///     tube condenser), where on the speaker (<c>position</c>, dust cap → cone edge),
///     how far from the grille (<c>distanceCm</c>), its angle off the speaker axis
///     (<c>angleDeg</c>), plus its room, second-mic blend and level. Every
///     property reads and writes the node's own parameter, so the knobs, presets,
///     automation and this view can never disagree. It replaces the cabinet's old
///     mic pad, which dressed the cab's low / high cut up as a mic.
/// </summary>
public sealed partial class RigMicPlacementViewModel : ObservableObject, IDisposable
{
    private readonly PedalParamViewModel _mic;
    private readonly PedalParamViewModel _position;
    private readonly PedalParamViewModel _distance;
    private readonly List<PedalParamViewModel> _watched;
    private readonly ILocalizationService _loc;
    private double _bearing = MicPlacementMath.DefaultBearingDegrees;

    private RigMicPlacementViewModel(PedalNodeViewModel node, PedalParamViewModel mic, PedalParamViewModel position,
        PedalParamViewModel distance)
    {
        Node = node;
        _loc = node.Localization;
        _mic = mic;
        _position = position;
        _distance = distance;
        Room = node.Params.FirstOrDefault(p => p.Id == "room");
        SecondMic = node.Params.FirstOrDefault(p => p.Id == "blend2");
        Level = node.Params.FirstOrDefault(p => p.Id == "level");
        Angle = node.Params.FirstOrDefault(p => p.Id == "angleDeg");
        // One card per microphone the node's own `mic` range offers (57 / 121 /
        // 421 / Condenser and the tube condenser).
        Mics = Enumerable.Range(0, (int)Math.Round(mic.Maximum - mic.Minimum) + 1)
            .Select(i => new RigMicChoice(this, i, _loc)).ToArray();
        _watched = new[] { mic, position, distance, Room, SecondMic }.OfType<PedalParamViewModel>().ToList();
        foreach (var p in _watched) p.PropertyChanged += OnParamChanged;
    }

    /// <summary>The placement for <paramref name="node" />, or null when it is not a
    /// mic model (it lacks the mic, position and distance params).</summary>
    public static RigMicPlacementViewModel? For(PedalNodeViewModel? node)
    {
        if (node is null) return null;
        var mic = node.Params.FirstOrDefault(p => p.Id == "mic");
        var position = node.Params.FirstOrDefault(p => p.Id == "position");
        var distance = node.Params.FirstOrDefault(p => p.Id == "distanceCm");
        return mic is null || position is null || distance is null ? null : new RigMicPlacementViewModel(node, mic, position, distance);
    }

    /// <summary>The mic unit this placement drives.</summary>
    public PedalNodeViewModel Node { get; }

    /// <summary>0 on the dust cap's centre … 1 at the cone's edge.</summary>
    public double Position
    {
        get => _position.Value;
        set => _position.Value = (float)Math.Clamp(value, _position.Minimum, _position.Maximum);
    }

    /// <summary>Centimetres from the grille cloth, 0 … 30.</summary>
    public double DistanceCm
    {
        get => _distance.Value;
        set => _distance.Value = (float)Math.Clamp(value, _distance.Minimum, _distance.Maximum);
    }

    /// <summary>The microphone: 0 dynamic 57, 1 ribbon 121, 2 dynamic 421, 3 condenser, 4 tube condenser.</summary>
    public int MicIndex
    {
        get => (int)Math.Round(_mic.Value - _mic.Minimum);
        set => _mic.Value = (float)Math.Clamp(_mic.Minimum + value, _mic.Minimum, _mic.Maximum);
    }

    /// <summary>The angle around the cone the mic is drawn at. Placement state only —
    /// the model has no bearing — kept here so it survives re-binding.</summary>
    public double BearingDegrees
    {
        get => _bearing;
        set => SetProperty(ref _bearing, value);
    }

    public PedalParamViewModel? Room { get; }

    /// <summary>The model's second mic: a ribbon at 10 cm, blended in.</summary>
    public PedalParamViewModel? SecondMic { get; }

    public PedalParamViewModel? Level { get; }

    /// <summary>The capsule's angle off the speaker axis, 0 … 60° — the mic's own
    /// published polar response (the model's <c>angleDeg</c>). Distinct from
    /// <see cref="BearingDegrees" />, which only says where around the cone the mic
    /// is drawn.</summary>
    public PedalParamViewModel? Angle { get; }

    /// <summary>The microphones, as picker cards.</summary>
    public IReadOnlyList<RigMicChoice> Mics { get; }

    public RigMicChoice ActiveMic => Mics[Math.Clamp(MicIndex, 0, Mics.Count - 1)];

    public int Zone => MicPlacementMath.Zone(Position);

    /// <summary>"Cap edge · bright".</summary>
    public string PositionReadout => _loc[ZoneKey(Zone)] + " · " + _loc[ToneKey(Zone)];

    /// <summary>"5.0 cm".</summary>
    public string DistanceText => DistanceCm.ToString("0.0", CultureInfo.CurrentCulture) + " cm";

    /// <summary>"Close · balanced".</summary>
    public string DistanceReadout => _loc[DistanceKey(MicPlacementMath.DistanceZone(DistanceCm))];

    internal static string ZoneKey(int zone) => zone switch
    {
        0 => "Rig_Editor_MicZone_Cap",
        1 => "Rig_Editor_MicZone_CapEdge",
        2 => "Rig_Editor_MicZone_MidCone",
        _ => "Rig_Editor_MicZone_ConeEdge"
    };

    internal static string ToneKey(int zone) => zone switch
    {
        0 => "Rig_Editor_MicTone_Brightest",
        1 => "Rig_Editor_MicTone_Bright",
        2 => "Rig_Editor_MicTone_Balanced",
        _ => "Rig_Editor_MicTone_Warmest"
    };

    internal static string DistanceKey(int zone) => zone switch
    {
        0 => "Rig_Mic_Dist_Grille",
        1 => "Rig_Mic_Dist_Close",
        _ => "Rig_Mic_Dist_Back"
    };

    /// <summary>Back to the model's defaults: a 57 at the cap edge, 5 cm off.</summary>
    [RelayCommand]
    private void ResetPlacement()
    {
        _position.Value = _position.Default;
        _distance.Value = _distance.Default;
        BearingDegrees = MicPlacementMath.DefaultBearingDegrees;
    }

    private void OnParamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PedalParamViewModel.Value) or null)) return;
        if (ReferenceEquals(sender, _position))
        {
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(Zone));
            OnPropertyChanged(nameof(PositionReadout));
        }
        else if (ReferenceEquals(sender, _distance))
        {
            OnPropertyChanged(nameof(DistanceCm));
            OnPropertyChanged(nameof(DistanceText));
            OnPropertyChanged(nameof(DistanceReadout));
        }
        else if (ReferenceEquals(sender, _mic))
        {
            OnPropertyChanged(nameof(MicIndex));
            OnPropertyChanged(nameof(ActiveMic));
            foreach (var choice in Mics) choice.Refresh();
        }
    }

    public void Dispose()
    {
        foreach (var p in _watched) p.PropertyChanged -= OnParamChanged;
    }
}

/// <summary>One microphone on the picker: its drawing, name and sound.</summary>
public sealed partial class RigMicChoice : ObservableObject
{
    private readonly RigMicPlacementViewModel _owner;
    private readonly ILocalizationService _loc;

    public RigMicChoice(RigMicPlacementViewModel owner, int index, ILocalizationService localization)
    {
        _owner = owner;
        _loc = localization;
        Index = index;
    }

    public int Index { get; }

    public string Name => _loc[Index switch
    {
        0 => "Rig_Mic_57",
        1 => "Rig_Mic_121",
        2 => "Rig_Mic_421",
        4 => "Rig_Mic_Tube",
        _ => "Rig_Mic_Condenser"
    }];

    public string Sound => _loc[Index switch
    {
        0 => "Rig_Mic_57_Sound",
        1 => "Rig_Mic_121_Sound",
        2 => "Rig_Mic_421_Sound",
        4 => "Rig_Mic_Tube_Sound",
        _ => "Rig_Mic_Condenser_Sound"
    }];

    public bool IsActive => _owner.MicIndex == Index;

    [RelayCommand]
    private void Select() => _owner.MicIndex = Index;

    internal void Refresh() => OnPropertyChanged(nameof(IsActive));
}
