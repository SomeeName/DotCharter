using Avalonia;
using Avalonia.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Input.Tools;

public class ToolContext
{
    private FractionalPosition? _snappedPosition;

    public ToolContext(MainWindowViewModel vm, IChartViewport viewport, ITrackContextRegistry trackContexts)
    {
        ViewModel = vm;
        Viewport = viewport;
        TrackContexts = trackContexts;
        Properties = default!;
    }

    public MainWindowViewModel ViewModel { get; }
    public IChartViewport Viewport { get; }
    public ITrackContextRegistry TrackContexts { get; }

    /// <summary>
    /// The track this pointer event is operating on. Set by the coordinator
    /// before each pointer event is dispatched. Tools must read all
    /// arrangement-scoped data (notes, chords, hand shapes, anchors, events,
    /// selection, hovers, previews) through this context, never through
    /// the global <see cref="DotCharter.UI.ViewModels.Editor.ChartEditorViewModel"/> singletons. That way,
    /// when later phases give stacked panels their own contexts, tools
    /// already act on the correct arrangement automatically.
    /// </summary>
    public TrackEditorContext Track { get; set; } = default!;

    public Point Position { get; set; }
    public PointerPointProperties Properties { get; set; }
    public KeyModifiers Modifiers { get; set; }
    public double ExactTimeMs { get; set; }
    public int StringIndex { get; set; }

    public FractionalPosition SnappedPosition
    {
        get
        {
            _snappedPosition ??= ViewModel.ChartEditor.SnapToGrid(ExactTimeMs);
            return _snappedPosition.Value;
        }
    }

    public void ResetSnapCache() => _snappedPosition = null;
}
