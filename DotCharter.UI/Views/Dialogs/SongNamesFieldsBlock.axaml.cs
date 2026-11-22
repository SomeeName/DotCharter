using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Views.Dialogs;

/// <summary>
///     The song-name fields New project and Song metadata share: the non-Latin offer plus the
///     collapsed "Advanced" expander holding the Japanese, sort and DLC fields.
/// </summary>
public partial class SongNamesFieldsBlock : UserControl
{
    public static readonly StyledProperty<double> FieldWidthProperty =
        AvaloniaProperty.Register<SongNamesFieldsBlock, double>(nameof(FieldWidth), 240);

    public SongNamesFieldsBlock() => InitializeComponent();

    /// <summary>The width of the control column, matching the host dialog's own rows.</summary>
    public double FieldWidth
    {
        get => GetValue(FieldWidthProperty);
        set => SetValue(FieldWidthProperty, value);
    }
}
