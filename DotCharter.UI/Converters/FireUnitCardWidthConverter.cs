using System.Globalization;
using Avalonia.Data.Converters;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.UI.Converters;

/// <summary>
///     A unit card's width, from how many knobs the unit actually has.
///
///     <para>
///         The rig board's cards are one HEIGHT and many widths — that is the
///         mockup's rule, and it is what makes the board read as a row of gear
///         rather than a ragged pile. A one-knob boost is a narrow box; a
///         nine-knob amp is a wide one. A single fixed width has to be wide
///         enough for the amp, which leaves the boost three-quarters empty, and
///         a purely auto width puts nine knobs on one 700 px line.
///     </para>
///
///     <para>
///         So the card is sized by COLUMNS: 2 up to four knobs, 3 up to six, 4 up
///         to eight, 5 beyond — the mockup's own 186 / 208 / 300 / 372 ladder
///         expressed as the rule behind it, since our knob cell is 72 px wide and
///         the mockup's is 68. Everything else about the card (the identity bar,
///         the head, the foot) is width-independent.
///     </para>
/// </summary>
public sealed class FireUnitCardWidthConverter : IValueConverter
{
    public static readonly FireUnitCardWidthConverter Instance = new();

    /// <summary>One <c>FireKnob</c> cell as the board draws it.</summary>
    public const double KnobCellWidth = 68.0;

    /// <summary>
    ///     Everything on the card that is NOT knob columns: the card's own
    ///     left+right inset (<c>FirePadCard</c>), its border, and the width the
    ///     knob area's scrollbar takes when a tall unit needs one. Getting this
    ///     wrong is invisible in the source and obvious in a render — one column
    ///     short and the last two knobs wrap onto a third row the card is not
    ///     tall enough for, so they are simply not there.
    /// </summary>
    public const double CardInset = 46.0;

    /// <summary>Columns for a knob count. Public + pure so the ladder is pinned
    /// in the default gate instead of living only inside a converter.
    /// <para>
    ///     TWO is the floor even for a one-knob unit: the head has to fit a
    ///     category eyebrow, the unit's name and the remove button, and a
    ///     single-column card cropped "Clean Boost" to "Clea…".
    /// </para></summary>
    public static int ColumnsFor(int knobCount) => knobCount switch
    {
        <= 4 => 2,
        <= 6 => 3,
        <= 8 => 4,
        _ => 5
    };

    public static double WidthFor(int knobCount)
        => ColumnsFor(knobCount) * KnobCellWidth + CardInset;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PedalNodeViewModel node
            ? HardwareWidthFor(node.Skin, node.KnobParams.Count)
            : WidthFor(value is int count ? count : 0);

    public static double HardwareWidthFor(PedalSkin skin, int knobCount)
        => Math.Max(WidthFor(knobCount), skin switch
        {
            PedalSkin.AmpHead or PedalSkin.NeuralAmp => 390,
            PedalSkin.Cabinet => 300,
            _ => 228
        });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("A card's width is a rendering of its knob count.");
}
