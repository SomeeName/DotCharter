using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>View-only index offsets and status visibility; never transforms a DSP value.</summary>
public sealed class RigControlPresentationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RigAssignmentStatus status) return status.ToString() == (parameter as string);
        if (value is not int number) return AvaloniaProperty.UnsetValue;
        return (parameter as string) switch
        {
            "macroIndex" => number + 1,
            "midiChannel" => number < 0 ? LocalizationService.Instance["Rig_AssignmentNone"] : (number + 1).ToString(culture),
            "midiController" => number < 0 ? LocalizationService.Instance["Rig_AssignmentNone"] : number.ToString(culture),
            _ => AvaloniaProperty.UnsetValue
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (parameter as string) == "macroIndex" && value is int index && index is >= 0 and <= 4
            ? index - 1 : BindingOperations.DoNothing;
}
