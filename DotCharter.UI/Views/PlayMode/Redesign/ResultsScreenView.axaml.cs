using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     The redesigned Play Mode results screen. Binds to
///     <see cref="DotCharter.Presentation.Contracts.PlayMode.IResultsScreen" /> and to
///     nothing else. Ctor only — the shipped screen's score-reveal animation is
///     driven from code-behind and is deliberately not part of the redesign's
///     contract.
/// </summary>
public partial class ResultsScreenView : UserControl
{
    public ResultsScreenView() => InitializeComponent();
}
