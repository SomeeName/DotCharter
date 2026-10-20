// RigDockUnit: whether the editor host draws the DETAILS column beside the stage.

using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class RigDockUnit
{
    /// <summary>
    ///     Whether <c>RigModuleEditorView</c> draws its details column (the capture
    ///     and its library, every exact value, lyrics, the meters, the unit's status
    ///     and the controllers &amp; MIDI) beside the big gear. The rig screen owns
    ///     the choice (<see cref="AmpFxRigViewModel.IsDetailsOpen" />, the gear bar's
    ///     DETAILS toggle) and pushes it here; an editor hosted on its own keeps the
    ///     default, which shows everything.
    /// </summary>
    [ObservableProperty] private bool _showsDetails = true;
}
