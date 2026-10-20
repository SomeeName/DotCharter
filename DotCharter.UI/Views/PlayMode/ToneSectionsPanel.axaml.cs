using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode;

/// <summary>The "Match by section" list, hosted inside the tone-design panel's
/// match area. All behaviour lives on <c>ToneSectionsViewModel</c>; this is the
/// parameterless-ctor host the XAML needs.</summary>
public partial class ToneSectionsPanel : UserControl
{
    /// <summary>Builds the panel.</summary>
    public ToneSectionsPanel() => InitializeComponent();
}
