using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     The redesigned Play Mode start screen (the front door). Binds to
///     <see cref="DotCharter.Presentation.Contracts.PlayMode.IStartScreen" /> and to nothing
///     else. Ctor only.
/// </summary>
public partial class StartScreenView : UserControl
{
    public StartScreenView() => InitializeComponent();
}
