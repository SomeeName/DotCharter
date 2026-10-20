using System.Globalization;
using Avalonia.Data.Converters;
using DotCharter.Tone.UI.Converters;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.Converters;

/// <summary>
///     Turns an EQ preset KEY into the sentence the picker shows.
///     <para>
///         The picker's items are keys, not labels, because the key is what the
///         section persists and what <c>EqPresets</c> is indexed by — so the list
///         and the model are one list. Exactly the same split
///         <see cref="FireTokenBrushConverter" /> makes for identity colours:
///         the view model says WHICH, the one table says what that looks like.
///     </para>
/// </summary>
public sealed class EqPresetLabelConverter : IValueConverter
{
    public static readonly EqPresetLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && key.Length > 0 ? EqModuleViewModel.PresetLabel(key, LocalizationService.Instance) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A preset label is display-only; the picker binds the KEY.");
}
