using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     The redesigned Play Mode fail screen. Binds to
///     <see cref="DotCharter.Presentation.Contracts.PlayMode.IFailedScreen" /> and to nothing
///     else. Ctor only.
/// </summary>
public partial class FailedScreenView : UserControl
{
    public FailedScreenView() => InitializeComponent();
}
