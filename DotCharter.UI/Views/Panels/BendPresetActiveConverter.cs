using System.Globalization;
using Avalonia.Data.Converters;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Panels;

// Binding converter used by the bend editor's preset buttons. Returns true
// when the view model's ActivePreset matches the preset name passed as the
// ConverterParameter (e.g. "Bend", "PrebendRelease"), which the Avalonia
// binding then applies as a style class on the button.
public sealed class BendPresetActiveConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not BendEditorViewModel.BendPreset preset) return false;
        if (parameter is not string name) return false;
        return Enum.TryParse<BendEditorViewModel.BendPreset>(name, out var want) && preset == want;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
