using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>What the Shapes panel reaches beside the chart editor: placing a shape, the grid, and the dialogs it opens.</summary>
public sealed record ShapesPanelParts(IChordShapePlacementService Placement, IEditorTransport Transport, IDialogs Dialogs);
