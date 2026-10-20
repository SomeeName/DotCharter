using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     The redesigned Play Mode Get Ready / tuning-check screen. Binds to
///     <see cref="DotCharter.Presentation.Contracts.PlayMode.ITuningCheckScreen" /> and to
///     nothing else. Ctor only.
/// </summary>
public partial class TuningCheckScreenView : UserControl
{
    public TuningCheckScreenView() => InitializeComponent();
}
