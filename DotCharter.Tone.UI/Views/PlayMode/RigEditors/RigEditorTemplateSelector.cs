using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode.RigEditors;

/// <summary>
///     Builds the editor VIEW for a <see cref="RigDockUnit" /> from its
///     <see cref="RigDockUnit.Family" />. One place, one switch: a family that
///     has no view of its own yet falls back to the generic pedal editor, so
///     every unit always opens SOMETHING that exposes all of its parameters.
///     <see cref="RigModuleEditorView" /> keeps one instance per family alive
///     and only swaps the DataContext, so this is called once per family per
///     host, not once per click.
/// </summary>
public sealed class RigEditorTemplateSelector : IDataTemplate
{
    public bool Match(object? data) => data is RigDockUnit;

    public Control Build(object? data) => BuildFor(((RigDockUnit)data!).Family);

    /// <summary>The view TYPE each family opens — the test seam.</summary>
    public static Control BuildFor(RigEditorFamily family) => family switch
    {
        RigEditorFamily.AmpFace => new RigAmpFaceView(),
        RigEditorFamily.Cabinet => new RigCabinetEditorView(),
        RigEditorFamily.MicPlacement => new RigMicEditorView(),
        RigEditorFamily.Routing => new RigRoutingConsoleView(),
        RigEditorFamily.GraphicEq => new RigGraphicEqView(),
        RigEditorFamily.ParametricEq => new RigParametricEqView(),
        RigEditorFamily.Expression => new RigExpressionView(),
        RigEditorFamily.Vocal => new RigVocalView(),
        RigEditorFamily.Dynamics => new RigDynamicsView(),
        _ => new RigPedalEditorView()
    };
}
