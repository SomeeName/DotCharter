using Avalonia;
using Avalonia.Controls;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     Shape values a THEME sets, carried on the control so markup can bind
///     them.
///     <para>
///         <b>Why an attached property and not a converter parameter.</b> The
///         lit action's chamfer is produced from the control's live
///         <c>Bounds</c> by <c>Converters/FireChamferClipConverter</c>, which
///         takes the cut as its converter PARAMETER — and a converter parameter
///         is a plain CLR value, so <c>{DynamicResource FireCutSize}</c> cannot
///         reach it. The size was therefore the converter's own constant, and
///         the <c>FireCutSize</c> token in <c>FireTokens/Ladders.axaml</c> was
///         read by nothing: a theme could not change the silhouette that is the
///         design's signature.
///     </para>
///     <para>
///         <see cref="CutSizeProperty" /> closes that: the control theme sets it
///         from the token, and the clip binds bounds AND cut. Default is the
///         converter's own default, so a control that never sets it looks
///         exactly as before.
///     </para>
/// </summary>
public static class FireShape
{
    /// <summary>The 45° cut on the lit action, in DIPs (the token is <c>FireCutSize</c>).</summary>
    public static readonly AttachedProperty<double> CutSizeProperty =
        AvaloniaProperty.RegisterAttached<Control, double>(
            "CutSize", typeof(FireShape), Converters.FireChamferClipConverter.DefaultCut);

    public static double GetCutSize(Control control) => control.GetValue(CutSizeProperty);

    public static void SetCutSize(Control control, double value) => control.SetValue(CutSizeProperty, value);
}
