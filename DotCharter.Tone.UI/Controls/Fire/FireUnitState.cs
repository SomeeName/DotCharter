using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The eight gear categories of the Fire design system. Each one owns a FIXED
/// colour ("GEAR CATEGORY IDENTITY · FIXED, DO NOT REASSIGN" — section 01 of
/// the owner's Implementation Spec); the brushes live in
/// <c>Styles/FireControls.axaml</c> and the mapping is
/// <see cref="FireUnitState.TokenKeyFor"/>.
/// </summary>
public enum FireUnitCategory
{
    /// <summary>Compressor / gate / limiter — <c>#8FA3BD</c>.</summary>
    Dynamics,

    /// <summary>Amp heads — <c>#F0A54A</c>.</summary>
    Amps,

    /// <summary>Cabinets and IR loaders — <c>#C98F5E</c>.</summary>
    Cabinets,

    /// <summary>Overdrive / distortion / fuzz — <c>#E05A5A</c>.</summary>
    Drive,

    /// <summary>Equalisers — <c>#4ADE80</c>.</summary>
    Eq,

    /// <summary>Chorus / phaser / flanger / detune — <c>#E879B9</c>.</summary>
    Modulation,

    /// <summary>Delay and reverb — <c>#3EC9C0</c>.</summary>
    DelayReverb,

    /// <summary>Splitters, mergers, gain, routing — <c>#A78BFA</c>.</summary>
    UtilityRouting
}

/// <summary>
/// The rig unit's identity and its bypass, as two INHERITED attached
/// properties on the unit's card.
///
/// <para><b>Why attached-and-inherited rather than a property per control.</b>
/// The spec's own list of "behaviour rules the agent keeps missing" opens with:
/// <i>"Bypassed units drop to 0.55 opacity AND grey their signal-strip chip AND
/// kill their LED — all three."</i> Four independent visual switches is exactly
/// the shape that ships with one of them forgotten. A card sets
/// <c>fire:FireUnitState.IsBypassed="{Binding IsBypassed}"</c> and
/// <c>fire:FireUnitState.Category="Drive"</c> ONCE on its root, and every
/// <see cref="FireKnob"/>, <see cref="FireLevelMeter"/> and
/// <see cref="FireProgress"/> underneath picks both up — there is no second
/// place to set and therefore no half state to apply. A control still accepts a
/// local value (a lone knob in a gear-list preview), because a local value beats
/// an inherited one.</para>
///
/// <para>The identity is a CATEGORY, never a colour: a knob that took a brush
/// could be handed the wrong hue, and the eight hues are fixed identities.</para>
/// </summary>
public static class FireUnitState
{
    /// <summary>The gear category whose fixed colour this subtree draws in.
    /// Inherited — set it once on the unit card.</summary>
    public static readonly AttachedProperty<FireUnitCategory> CategoryProperty =
        AvaloniaProperty.RegisterAttached<Control, FireUnitCategory>(
            "Category", typeof(FireUnitState), FireUnitCategory.UtilityRouting, inherits: true);

    /// <summary>
    ///     An explicit accent for a subtree that has its OWN colour in the
    ///     design — the pedal enclosure, whose paint and accent come per pedal
    ///     from the texture pack's finishes (<c>RigArtwork.AccentFor</c>) rather
    ///     than from the category. Null (the default) keeps the category's fixed
    ///     identity. Inherited like the category; bypass still wins.
    /// </summary>
    public static readonly AttachedProperty<Color?> AccentProperty =
        AvaloniaProperty.RegisterAttached<Control, Color?>(
            "Accent", typeof(FireUnitState), null, inherits: true);

    public static Color? GetAccent(Control control) => control.GetValue(AccentProperty);

    public static void SetAccent(Control control, Color? value) => control.SetValue(AccentProperty, value);

    /// <summary>Whether the unit is bypassed. Inherited — set it once on the
    /// unit card and the whole subtree greys together.</summary>
    /// <summary>The resource key of the colour a board wire draws in — set on a
    /// lane row (its lane colour) or the serial run; inherited by every
    /// <see cref="RigWire" /> under it. The trunk keeps the accent.</summary>
    public static readonly AttachedProperty<string> LaneTokenProperty =
        AvaloniaProperty.RegisterAttached<Control, string>(
            "LaneToken", typeof(FireUnitState), "FireAccent", inherits: true);

    public static string GetLaneToken(Control control) => control.GetValue(LaneTokenProperty);

    public static void SetLaneToken(Control control, string value) => control.SetValue(LaneTokenProperty, value);

    public static readonly AttachedProperty<bool> IsBypassedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsBypassed", typeof(FireUnitState), inherits: true);

    /// <summary>What a bypassed unit's card drops to
    /// (<c>opacity: off ? .55 : 1</c> in <c>Amp and FX Rig.dc.html</c>). The
    /// custom-drawn controls apply the same factor to everything they paint, so
    /// a knob inside a bypassed card reads as off even where the card's own
    /// opacity does not reach.</summary>
    public const double BypassedOpacity = 0.55;

    /// <summary>A disabled control is dimmer still — it is not "off by choice",
    /// it is unavailable.</summary>
    public const double DisabledOpacity = 0.38;

    /// <summary>The token a bypassed/disabled unit's accent collapses to. Its
    /// own colour is its IDENTITY, so a bypassed unit must not keep it — the
    /// LED going out is half of what "bypassed" looks like.</summary>
    public const string InactiveTokenKey = "FireTextDim";

    public static FireUnitCategory GetCategory(Control control) => control.GetValue(CategoryProperty);

    public static void SetCategory(Control control, FireUnitCategory value)
        => control.SetValue(CategoryProperty, value);

    public static bool GetIsBypassed(Control control) => control.GetValue(IsBypassedProperty);

    public static void SetIsBypassed(Control control, bool value)
        => control.SetValue(IsBypassedProperty, value);

    /// <summary>The resource key of a category's fixed colour. Total over the
    /// enum on purpose — a new category will not compile until it has a token.</summary>
    public static string TokenKeyFor(FireUnitCategory category) => category switch
    {
        FireUnitCategory.Dynamics => "FireUnitDynamics",
        FireUnitCategory.Amps => "FireUnitAmps",
        FireUnitCategory.Cabinets => "FireUnitCabinets",
        FireUnitCategory.Drive => "FireUnitDrive",
        FireUnitCategory.Eq => "FireUnitEq",
        FireUnitCategory.Modulation => "FireUnitModulation",
        FireUnitCategory.DelayReverb => "FireUnitDelay",
        FireUnitCategory.UtilityRouting => "FireUnitUtility",
        _ => "FireUnitUtility"
    };

    /// <summary>
    /// True when the control must paint itself as inactive — bypassed OR
    /// disabled. ONE predicate, so a control cannot honour one and miss the
    /// other.
    /// </summary>
    public static bool IsInactive(Control control)
        => GetIsBypassed(control) || !control.IsEffectivelyEnabled;

    /// <summary>The opacity everything this control paints is multiplied by.</summary>
    public static double OpacityFor(Control control)
        => !control.IsEffectivelyEnabled ? DisabledOpacity
            : GetIsBypassed(control) ? BypassedOpacity
            : 1.0;

    /// <summary>
    /// The colour this control draws its lit parts in: the category's fixed
    /// identity, or the inactive grey when bypassed/disabled. The ONE place
    /// either decision is made.
    /// </summary>
    public static Color AccentOf(Control control)
    {
        if (IsInactive(control)) return ColorFrom(control, InactiveTokenKey);
        if (GetAccent(control) is { } own) return own;
        return ColorFrom(control, TokenKeyFor(GetCategory(control)));
    }

    /// <summary>
    /// Resolves a theme brush to its colour. Returns <see cref="Colors.Gray"/>
    /// when the token is absent — the dictionary is the single source of every
    /// Fire colour, so a per-category literal here would BE the second source
    /// this layer exists to avoid. A missing token therefore renders visibly
    /// wrong rather than plausibly right, and
    /// <c>FireUnitIdentityTests</c> pins that every key exists with the
    /// spec's value.
    /// </summary>
    internal static Color ColorFrom(Control control, string key)
        => control.TryFindResource(key, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : Colors.Gray;

    /// <summary>Resolves a theme brush as a brush (gradients included).</summary>
    internal static IBrush? BrushFrom(Control control, string key)
        => control.TryFindResource(key, out var value) ? value as IBrush : null;

    /// <summary>The same colour at a different alpha — used for tracks, glows
    /// and washes, so a tint is never a second hex.</summary>
    internal static Color At(Color color, double alpha)
        => Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255.0), 0, 255), color.R, color.G, color.B);
}
