using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode.RigEditors;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>Selected unit editor shared by serial and parallel signal chains.
/// Hosts one view per editor family, built on first use and kept attached
/// (hidden) after that, so stepping through the chain swaps a DataContext
/// instead of inflating and styling a view.</summary>
public partial class RigModuleEditorView : UserControl
{
    private readonly Dictionary<RigEditorFamily, Control> _views = [];

    /// <summary>The node whose editor is showing, so a re-select of the SAME
    /// unit keeps its scroll offset and a new one does not inherit it.</summary>
    private int _shownNodeId = -1;

    public RigModuleEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ShowFamilyView();
    }

    /// <summary>The family view currently showing, if any (test seam).</summary>
    public Control? ActiveFamilyView =>
        DataContext is RigDockUnit unit && _views.TryGetValue(unit.Family, out var view) ? view : null;

    private void ShowFamilyView()
    {
        var unit = DataContext as RigDockUnit;
        foreach (var (family, view) in _views)
        {
            if (unit is not null && family == unit.Family) continue;
            view.IsVisible = false;
            view.DataContext = null;
        }
        if (unit is null) return;

        if (!_views.TryGetValue(unit.Family, out var active))
        {
            active = RigEditorTemplateSelector.BuildFor(unit.Family);
            _views[unit.Family] = active;
            RigEditorFamilyHost.Children.Add(active);
        }
        var isNewUnit = unit.Node.NodeId != _shownNodeId;
        _shownNodeId = unit.Node.NodeId;
        active.DataContext = unit;
        active.IsVisible = true;
        if (isNewUnit) ScrollNewUnitIntoView(active);
    }

    /// <summary>
    ///     A NEW unit starts at the TOP of its editor (W3: "the editor is
    ///     brought into view"); re-selecting the SAME unit keeps the offset,
    ///     because that is someone coming back to where they were.
    ///     <para>
    ///         Both scrollers are reset, and the RACK is the one that can
    ///         actually carry a stale offset today. Its three cards — the
    ///         performance macros, the control-assignment editor and the MIDI
    ///         input — are per-UNIT content in a column that is routinely taller
    ///         than the dock (measured at 854 px), and this host keeps every
    ///         view it has built ATTACHED, so without this the next unit opens
    ///         wherever the last one was left: the header names one module while
    ///         the column shows another's assignments.
    ///     </para>
    ///     <para>
    ///         The FACE's own <c>RigEditorScroll</c> is reset for the same reason
    ///         and is belt-and-braces: since lane E every face is folded into the
    ///         row by <c>RigScaleToFit</c>, so its extent fits its viewport and
    ///         its offset is already zero. It is here so that a face which ever
    ///         stops fitting — a new family, a raised
    ///         <c>FireRigFaceMaxScale</c> — does not reintroduce the fault one
    ///         control over.
    ///     </para>
    /// </summary>
    private void ScrollNewUnitIntoView(Control familyView)
    {
        foreach (var scroll in familyView.GetSelfAndVisualDescendants().OfType<ScrollViewer>()
                     .Where(s => s.Name is "RigEditorScroll"))
        {
            scroll.Offset = default;
        }

        foreach (var rack in this.GetSelfAndVisualDescendants().OfType<ScrollViewer>()
                     .Where(s => s.Name is "RigEditorRackScroll"))
        {
            rack.Offset = default;
        }
    }
}
