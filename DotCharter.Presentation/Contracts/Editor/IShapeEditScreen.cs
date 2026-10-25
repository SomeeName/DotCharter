using System.Windows.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Editor;

/// <summary>
///     The shape editor: one grip — its frets, fingers and name — edited with its SCOPE chosen
///     first. A shape is a reusable grip that chords and hand shapes point at, so every edit
///     says up front what it reaches: this chord, the selection, or every use. The uses the
///     chosen scope reaches are lit (here in the song strip, in the app on the timeline as
///     well) before anything is applied, and Apply says in words what it will do.
///     <para>
///         Chords and their hand shapes stay in sync: a hand shape over the reached chords
///         follows the new grip unless <see cref="KeepHandShapesInSync" /> is turned off.
///     </para>
///     <para>
///         <see cref="IDialogScreen{TResult}.Result" /> is true when the edit was applied, as
///         one undo step.
///     </para>
/// </summary>
public interface IShapeEditScreen : IDialogScreen<bool>
{
    /// <summary>The shape being edited, as it is now: "Am · x 0 2 2 1 0".</summary>
    string ShapeTitle { get; }

    /// <summary>Where the editor was opened from: "The chord at bar 12, beat 3".</summary>
    string OpenedFromText { get; }

    /// <summary>The scopes, narrowest first; exactly one is selected.</summary>
    IReadOnlyList<IShapeEditScope> Scopes { get; }

    /// <summary>The song as a strip, one cell per bar: the bars that use the shape, and which of them the scope reaches.</summary>
    IReadOnlyList<IShapeUseBar> UseBars { get; }

    /// <summary>What the lit cells mean: "14 chords in bars 5–48 change · 6 keep Am".</summary>
    string PreviewText { get; }

    /// <summary>A display copy of the grip before the edit.</summary>
    ChordTemplate BeforeDiagram { get; }

    Chord BeforeChord { get; }

    /// <summary>A display copy of the grip as edited so far.</summary>
    ChordTemplate AfterDiagram { get; }

    Chord AfterChord { get; }

    int StringCount { get; }

    /// <summary>One row per string, low to high: its fret and finger, editable.</summary>
    IReadOnlyList<IShapeEditString> Strings { get; }

    /// <summary>The name being typed; ignored while <see cref="IsNoName" />.</summary>
    string NameDraft { get; set; }

    /// <summary>The fret pattern the name box shows while it is empty.</summary>
    string NamePlaceholder { get; }

    /// <summary>True when the shape deliberately has no name: the game shows none over it.</summary>
    bool IsNoName { get; set; }

    /// <summary>The name the frets spell ("Am7"), empty when nothing is recognised.</summary>
    string SuggestedName { get; }

    bool HasSuggestedName { get; }

    /// <summary>Copies <see cref="SuggestedName" /> into the name box.</summary>
    ICommand UseSuggestedNameCommand { get; }

    /// <summary>True while hand shapes over the reached chords follow the edit.</summary>
    bool KeepHandShapesInSync { get; set; }

    /// <summary>What happens to the hand shapes either way: "3 hand shapes follow" or "3 hand shapes keep the old grip and stop matching their chords".</summary>
    string HandShapeSyncText { get; }

    /// <summary>True when the edited grip already exists as another shape.</summary>
    bool HasMatch { get; }

    /// <summary>"These frets and fingers are already “Am” (22 uses)."</summary>
    string MatchText { get; }

    /// <summary>Points the reached uses at the existing shape instead of making another copy.</summary>
    ICommand UseMatchCommand { get; }

    /// <summary>The one sentence Apply carries out: "A new shape “Am (2)” is made for this chord; the other 13 keep Am."</summary>
    string ResultText { get; }

    /// <summary>"Apply to every use (14)".</summary>
    string ApplyLabel { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}

/// <summary>One scope an edit can reach.</summary>
public interface IShapeEditScope : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>"This chord", "Selection", "Every use".</summary>
    string Label { get; }

    /// <summary>What it reaches: "1 chord · bar 12", "4 chords", "14 chords on 3 levels".</summary>
    string Detail { get; }

    bool IsSelected { get; }

    /// <summary>False when the scope reaches nothing (no selection); the view greys it.</summary>
    bool IsAvailable { get; }

    ICommand SelectCommand { get; }
}

/// <summary>One string of the grip.</summary>
public interface IShapeEditString : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>The open string's note: "E", "A", "D".</summary>
    string StringName { get; }

    /// <summary>The fret as typed: a number, <c>0</c> for open, <c>x</c> for unplayed.</summary>
    string Fret { get; set; }

    /// <summary>The finger as typed: 1–4, <c>T</c> for the thumb, empty for none.</summary>
    string Finger { get; set; }

    /// <summary>True when the fret or finger differs from the shape as it is now; the view marks the row.</summary>
    bool IsChanged { get; }
}

/// <summary>One bar of the song strip.</summary>
public interface IShapeUseBar
{
    /// <summary>The bar holds a use of the shape.</summary>
    bool IsUse { get; }

    /// <summary>The chosen scope reaches a use in this bar.</summary>
    bool IsReached { get; }

    /// <summary>The bar the editor was opened from.</summary>
    bool IsOrigin { get; }
}

/// <summary>
///     Opens <see cref="IShapeEditScreen" /> on <paramref name="Shape" /> (by reference, one of the active
///     arrangement's), from <paramref name="Origin" /> — the chord it was opened from, or null when it
///     was opened from the Chord shapes list, where "this chord" does not apply.
/// </summary>
public sealed record ShapeEditRequest(ChordTemplate Shape, Chord? Origin)
    : IDialogRequest<ShapeEditRequest, IShapeEditScreen, bool>
{
    /// <summary>
    ///     The shapes ticked in the Shapes panel for an Edit together (<see cref="Shape" /> is one of them): the
    ///     grip typed is given to all of them and they become one shape. Empty for the edit of one shape.
    /// </summary>
    public IReadOnlyList<ChordTemplate> Together { get; init; } = [];
}
