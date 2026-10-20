using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     The redesigned Play Mode in-game HUD. Binds to
///     <see cref="DotCharter.Presentation.Contracts.PlayMode.IPlayingScreen" /> and to
///     nothing else — the 3D highway is a native Vulkan surface and is a
///     placeholder <c>Border</c> in this view. Ctor only.
/// </summary>
public partial class PlayingScreenView : UserControl
{
    public PlayingScreenView() => InitializeComponent();
}
