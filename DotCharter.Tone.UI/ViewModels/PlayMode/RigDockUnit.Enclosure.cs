// RigDockUnit: the PEDAL ENCLOSURE projection - the generic editor family's controls, grid and finish.

using Avalonia;
using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class RigDockUnit
{
    /// <summary>True for the generic family: the unit is drawn as one complete
    /// stompbox enclosure sized to its own control count (design §6.2).</summary>
    public bool IsPedalFamily => Family == RigEditorFamily.Pedal;

    /// <summary>True when the unit has a generated faceplate
    /// (<see cref="RigFaceplates" />): the editor draws it, with the live controls
    /// on its nuts, instead of the drawn enclosure.</summary>
    public bool HasFaceplate => RigFaceplates.For(Node.ModuleTypeId) is not null;

    /// <summary>The pedal family's face: the generated faceplate.</summary>
    public bool ShowsFaceplate => IsPedalFamily && HasFaceplate;

    /// <summary>True when the stage shows a faceplate that does not carry every
    /// parameter: the dock opens its parameter list, because the big face alone
    /// cannot reach them. A family with its own editor (the mic placement, the
    /// cabinet) draws every param itself and never needs it.</summary>
    public bool FaceMissesParams =>
        ShowsFaceplate && RigFaceplates.For(Node.ModuleTypeId) is { } face
                       && Node.Params.Any(p => face.ControlFor(p.Id) is null);

    /// <summary>The drawn enclosure, for a pedal with no faceplate.</summary>
    public bool ShowsDrawnEnclosure => IsPedalFamily && !HasFaceplate;

    /// <summary>The model rail and the extra pickers belong to the faces; on a
    /// pedal every picker already sits in the enclosure grid, so showing them
    /// again below it would print the same control twice.</summary>
    public bool ShowsModelRail => HasModelRail && !IsPedalFamily;

    public bool ShowsExtraSelectors => HasExtraSelectors && !IsPedalFamily;

    private IReadOnlyList<object>? _enclosureControls;

    /// <summary>
    ///     Every control the enclosure shows, in CATALOGUE order: a
    ///     <see cref="PedalParamViewModel" /> for a continuous parameter, the
    ///     node's own <see cref="PedalModelSelectorViewModel" /> for a labelled
    ///     one. Nothing is invented and nothing is dropped — the test proves
    ///     the list is exactly the node's params, each once.
    /// </summary>
    public IReadOnlyList<object> EnclosureControls => _enclosureControls ??= BuildEnclosureControls();

    private IReadOnlyList<object> BuildEnclosureControls()
    {
        var list = new List<object>(Node.Params.Count);
        foreach (var param in Node.Params)
        {
            if (!param.HasValueLabels)
            {
                list.Add(param);
                continue;
            }

            var selector = Node.AmpModelSelector is { } amp && amp.ParamId == param.Id
                ? amp
                : Node.ModelSelectors.FirstOrDefault(s => s.ParamId == param.Id);
            if (selector is not null) list.Add(selector);
        }

        return list;
    }

    /// <summary>The knob grid mirrors the artwork: 2 columns up to four
    /// controls, 3 for five or six, 4 for seven or eight, 5 beyond.</summary>
    public int EnclosureColumns => RigArtwork.EnclosureColumns(EnclosureControls.Count);

    /// <summary>The enclosure's natural width at scale 1 (design: columns × 142
    /// plus 68 of padding); the editor scales it DOWN to fit, never re-lays it out.</summary>
    public double EnclosureNaturalWidth => EnclosureColumns * 142 + 68;

    /// <summary>
    ///     The columns the knob grid actually lays out. A ONE-control pedal
    ///     (Clean Boost's lone Gain) keeps the two-column enclosure the artwork
    ///     rule gives it - a narrower box would not hold its eyebrow and name -
    ///     but lays its knob in ONE column, centred, at the design's big single
    ///     dial (<c>.instrument-single</c>), instead of in the left cell of an
    ///     empty pair ("clean boost looks kinda weird").
    /// </summary>
    public int EnclosureGridColumns => EnclosureControls.Count == 1 ? 1 : EnclosureColumns;

    /// <summary>The dial diameter for the enclosure's knobs: 0 leaves
    /// <c>FireKnob</c>'s hardware default; a lone knob gets <see cref="SingleDial" />.</summary>
    public double EnclosureDialSize => EnclosureControls.Count == 1 ? SingleDial : 0;

    /// <summary>The single-knob dial: 1.5x the 64 px hardware dial, the
    /// design's 130 : 86 ratio for <c>.instrument-single</c>.</summary>
    public const double SingleDial = 96;

    /// <summary>A two-column enclosure has no room for the brand mark beside a
    /// long name; it drops rather than overlap (the Freeze pedal fault).</summary>
    public bool ShowsEnclosureBrand => EnclosureColumns >= 3;

    /// <summary>The enclosure's name line: the module name without a trailing
    /// parenthetical, which the dock bar already prints in full.</summary>
    public string EnclosureTitle
    {
        get
        {
            var name = Node.DisplayName;
            var open = name.LastIndexOf(" (", StringComparison.Ordinal);
            return open > 0 && name.EndsWith(')') ? name[..open] : name;
        }
    }

    public Color CoatColor => RigArtwork.CoatFor(Node.ModuleTypeId);
    public Color EnclosureAccent => RigArtwork.AccentFor(Node.ModuleTypeId);

    /// <summary>The paint: a light pulled to the top-left corner over a 155°
    /// gradient from a lifted coat to a shadowed one — reads as powder-coated
    /// metal rather than flat plastic.</summary>
    public IBrush CoatBrush => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Mix(CoatColor, Colors.White, 0.20), 0),
            new GradientStop(Mix(CoatColor, Colors.White, 0.06), 0.35),
            new GradientStop(Mix(CoatColor, Color.FromRgb(0x07, 0x08, 0x0d), 0.26), 1)
        }
    };

    /// <summary>The 2 px enclosure edge: the coat lifted towards white.</summary>
    public IBrush CoatEdgeBrush => new SolidColorBrush(Mix(CoatColor, Color.FromRgb(0xee, 0xee, 0xee), 0.35));

    /// <summary>The dark ledge under the enclosure (the design's 7 px drop).</summary>
    public IBrush CoatShadowBrush => new SolidColorBrush(Mix(CoatColor, Colors.Black, 0.65));

    public IBrush EnclosureAccentBrush => new SolidColorBrush(EnclosureAccent);

    /// <summary>The engaged LED: the accent lifted towards white, lit only while
    /// the unit is not bypassed.</summary>
    public IBrush EnclosureLedBrush => new SolidColorBrush(Mix(EnclosureAccent, Colors.White, 0.45));

    /// <summary>ENGAGED / BYPASS under the footswitch. The label has a reserved
    /// width in the markup so the two words cannot move the switch.</summary>
    public string FootswitchLabel => Node.Bypassed
        ? _loc["Rig_Bypass"]
        : _loc["Rig_Editor_Engaged"];

    public bool IsEngaged => !Node.Bypassed;

    /// <summary>The dock bar's ENABLED switch: the unit's bypass, inverted, so
    /// the header and the footswitch are one flag read two ways.</summary>
    public bool IsUnitEnabled
    {
        get => !Node.Bypassed;
        set
        {
            if (Node.Bypassed == !value) return;
            Node.Bypassed = !value;
        }
    }

    /// <summary>The action menu's Bypass / Enable row.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleEnabled() => IsUnitEnabled = !IsUnitEnabled;

    /// <summary>"Enable this unit" / "Bypass this unit" for the action menu.</summary>
    public string ToggleEnabledLabel => _loc[Node.Bypassed ? "Rig_Editor_EnableUnit" : "Rig_BypassUnit"];

    /// <summary>Called by the constructor's node subscription when the unit's
    /// bypass flips, so the LED and the label follow the footswitch.</summary>
    private void NotifyBypassChanged()
    {
        OnPropertyChanged(nameof(FootswitchLabel));
        OnPropertyChanged(nameof(IsEngaged));
        OnPropertyChanged(nameof(IsUnitEnabled));
        OnPropertyChanged(nameof(ToggleEnabledLabel));
    }

    private static Color Mix(Color a, Color b, double amount)
    {
        static byte Lerp(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(Lerp(a.R, b.R, amount), Lerp(a.G, b.G, amount), Lerp(a.B, b.B, amount));
    }
}
