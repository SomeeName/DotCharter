using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     The redesigned Play Mode count-in screen. Binds to
///     <see cref="DotCharter.Presentation.Contracts.PlayMode.ICountdownScreen" /> and to
///     nothing else — no view model, no services, no DI. Ctor only: everything
///     it does is a binding or an <c>ICommand</c> on the contract.
/// </summary>
public partial class CountdownScreenView : UserControl
{
    public CountdownScreenView() => InitializeComponent();
}
