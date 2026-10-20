using Avalonia.Controls;

namespace DotCharter.UI.Views.Prompts;

/// <summary>
///     The message dialog card. Binds to
///     <see cref="DotCharter.Presentation.Contracts.Dialogs.IMessagePromptScreen" />
///     and nothing else; <see cref="Shell.DialogHost" /> hosts it.
/// </summary>
public partial class MessagePromptView : UserControl
{
    public MessagePromptView() => InitializeComponent();
}
