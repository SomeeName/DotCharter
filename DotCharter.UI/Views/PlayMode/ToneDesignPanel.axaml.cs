using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode;

/// <summary>
///     The "Design a tone" panel — the prompt, the matcher and the section list —
///     hosted in the left column of the rig's TONES MODE. All behaviour lives on
///     <c>ToneDesignViewModel</c>; this is the parameterless-ctor host the XAML
///     needs.
///     <para>
///         It has no <c>IsStubbed</c> styled property: the panel does not share the rig's
///         fixed-height workspace with the module editor, so it never has to fall back to its
///         header whenever the editor wants the room. The two do not compete for
///         one host — the designer is a mode — so there is no stub state and
///         nothing to restore around it.
///     </para>
/// </summary>
public partial class ToneDesignPanel : UserControl
{
    /// <summary>Builds the panel.</summary>
    public ToneDesignPanel() => InitializeComponent();
}
