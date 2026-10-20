using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The chart editor's empty-state launch card: the hint, the three verbs
///     that leave the empty state (New Project / Open Project / Attach audio)
///     and the Open Recent list.
///     <para>
///         Lifted out of <c>MainWindow.axaml</c> so the card can be constructed
///         and laid out on its own — the shell window cannot be
///         <c>Show()</c>n in the headless session, so a row-containment
///         assertion had nowhere to live while this markup was inline. Its
///         visibility is still the shell's decision
///         (<c>MainWindowViewModel.IsEditorLaunchCardVisible</c>); this view
///         owns no state and inherits its DataContext.
///     </para>
/// </summary>
public partial class EditorLaunchCardView : UserControl
{
    /// <summary>Creates the launch card.</summary>
    public EditorLaunchCardView() => InitializeComponent();
}
