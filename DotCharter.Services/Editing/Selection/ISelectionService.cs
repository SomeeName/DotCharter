using System.Collections.ObjectModel;
using System.ComponentModel;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using EventPoint = DotCharter.Core.Domain.Events.EventPoint;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;
using LyricEvent = DotCharter.Core.Domain.Vocals.LyricEvent;
using ToneChange = DotCharter.Core.Domain.Events.ToneChange;

namespace DotCharter.Services.Editing.Selection;

/// <summary>
/// The single, app-wide selection model shared by every chart view (the 2D
/// timeline, the 3D Vulkan highway and the sheet-music strip). Views do
/// not own selection state — they read these collections and subscribe to
/// <see cref="SelectionChanged"/>, so a selection made in one view highlights
/// in all of them. Registered as a DI singleton in the composition root.
/// </summary>
public interface ISelectionService : INotifyPropertyChanged
{
    /// <summary>
    /// Raised whenever the selected *content* set changes: any <c>Selected*</c>
    /// collection gained/lost items, or the scalar <see cref="SelectedLyric"/>
    /// changed. It is a single coarse "selection changed, re-highlight" signal
    /// so a view can subscribe once instead of wiring six collection handlers.
    /// Fires on whichever thread mutated the selection — selection edits happen
    /// on the UI thread, so subscribers can touch UI state directly. It fires
    /// only on real selection edits (user-driven), never per render frame, so
    /// it is safe for a subscriber to repaint in the handler. Hover/preview
    /// state is deliberately excluded (it changes at pointer/60 Hz rates and is
    /// polled by the renderers each frame instead).
    /// </summary>
    event EventHandler? SelectionChanged;

    /// <summary>
    /// Monotonic counter bumped on every <see cref="SelectionChanged"/> —
    /// the cheap per-frame "did the selection change since I last looked?"
    /// probe for the render snapshot builders (v0.30 leg b: they rebuild
    /// their selection lookup sets only when this moves, instead of
    /// re-hashing the whole selection 60–144 times a second).
    /// </summary>
    long SelectionVersion { get; }

    ObservableCollection<GuitarNote> SelectedNotes { get; }
    ObservableCollection<Chord> SelectedChords { get; }
    ObservableCollection<EventPoint> SelectedEventPoints { get; }
    ObservableCollection<ToneChange> SelectedTones { get; }
    ObservableCollection<HandShape> SelectedHandShapes { get; }
    ObservableCollection<FretAnchor> SelectedAnchors { get; }

    ObservableCollection<GuitarNote> PreviewNotes { get; }
    ObservableCollection<Chord> PreviewChords { get; }
    ObservableCollection<HandShape> PreviewHandShapes { get; }
    ObservableCollection<EventPoint> PreviewEventPoints { get; }
    ObservableCollection<FretAnchor> PreviewAnchors { get; }

    /// <summary>
    /// Arrangement that owns the current Preview*/Hover* state. Renderers for
    /// other arrangements suppress their preview overlays so a hover/drag
    /// preview started in one timeline doesn't ghost-render across all of
    /// them. Null = no preview / global / fall back to active arrangement.
    /// </summary>
    Arrangement? PreviewArrangement { get; set; }

    GuitarNote? SelectedNote { get; set; }
    Chord? SelectedChord { get; set; }

    GuitarNote? HoveredNote { get; set; }
    Chord? HoveredChord { get; set; }
    HandShape? HoveredHandShape { get; set; }
    EventPoint? HoveredEventPoint { get; set; }
    ToneChange? HoveredTone { get; set; }
    FretAnchor? HoveredAnchor { get; set; }
    LyricEvent? HoveredLyric { get; set; }
    LyricEvent? SelectedLyric { get; set; }

    double HoveredTimeMs { get; set; }
    int HoveredStringIndex { get; set; }

    void ClearSelections();
    void ClearPreviews();
    void ClearHovers();
    void ClearAll();
}
