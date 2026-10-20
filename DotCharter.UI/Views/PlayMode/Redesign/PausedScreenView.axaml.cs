using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     The redesigned Play Mode pause screen. Binds to
///     <see cref="DotCharter.Presentation.Contracts.PlayMode.IPausedScreen" /> and to nothing
///     else. Ctor only.
/// </summary>
public partial class PausedScreenView : UserControl
{
    public PausedScreenView() => InitializeComponent();
}
