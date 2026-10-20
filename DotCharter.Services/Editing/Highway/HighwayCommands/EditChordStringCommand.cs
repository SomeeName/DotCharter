using System.Collections.ObjectModel;
using DotCharter.Services.State;
using BendValue = DotCharter.Core.Domain.Guitar.BendValue;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

internal enum EditChordStringOutcome
{
    None,
    AddedChordNote,
    RemovedChordNote,
    CollapsedChordToNote
}

internal sealed class EditChordStringCommand : IHighwayEditCommand
{
    private readonly Arrangement? _arrangement;
    private readonly Chord _chord;
    private readonly Action? _onPostExecute;
    private readonly ObservableCollection<Chord> _placedChords;
    private readonly ObservableCollection<GuitarNote> _placedNotes;
    private readonly int _stringIndex;

    public EditChordStringCommand(
        ObservableCollection<GuitarNote> placedNotes,
        ObservableCollection<Chord> placedChords,
        Arrangement? arrangement,
        Chord chord,
        int stringIndex,
        Action? onPostExecute)
    {
        _placedNotes = placedNotes;
        _placedChords = placedChords;
        _arrangement = arrangement;
        _chord = chord;
        _stringIndex = stringIndex;
        _onPostExecute = onPostExecute;
    }

    public EditChordStringOutcome Outcome { get; private set; }
    public GuitarNote? CollapsedNote { get; private set; }
    public Chord? NewChordState { get; private set; }

    public bool Execute(IHistoryService history)
    {
        var action = BuildAction();
        return action != null && history.Do(action);
    }

    private IChartAction? BuildAction()
    {
        var existingNote = _chord.ChordNotes.FirstOrDefault(n => n.StringIndex == _stringIndex);
        if (existingNote != null)
        {
            if (_chord.ChordNotes.Count <= 2)
                return BuildCollapseAction(existingNote);

            return BuildRemoveChordNoteAction();
        }

        return BuildAddChordNoteAction();
    }

    private IChartAction? BuildCollapseAction(ChordNote existingNote)
    {
        var remaining = _chord.ChordNotes.FirstOrDefault(n => n != existingNote);
        if (remaining == null) return null;

        CollapsedNote = new GuitarNote
        {
            Position = _chord.Position,
            StringIndex = remaining.StringIndex,
            Sustain = remaining.Sustain,
            Mute = remaining.Mute,
            Hopo = remaining.Hopo,
            Harmonic = remaining.Harmonic,
            Vibrato = remaining.Vibrato,
            VibratoStrength = remaining.VibratoStrength,
            Tremolo = remaining.Tremolo,
            LinkNext = remaining.LinkNext,
            SlideTo = remaining.SlideTo,
            UnpitchedSlide = remaining.UnpitchedSlide,
            BassPicking = remaining.BassPicking,
            GraceFret = remaining.GraceFret,
            BendValues = new ObservableCollection<BendValue>(remaining.BendValues.Select(b => b.Clone()).ToList()),
            Accent = _chord.Accent,
            Ignore = _chord.Ignore,
            PassOtherNotes = _chord.PassOtherNotes,
            Fret = remaining.Fret
        };
        Outcome = EditChordStringOutcome.CollapsedChordToNote;

        return new CompositeAction(new IChartAction[]
        {
            new RemoveChordsAction(_placedChords, new[] { _chord }, _onPostExecute),
            new AddNotesAction(_placedNotes, new[] { CollapsedNote }, _onPostExecute)
        });
    }

    private IChartAction BuildRemoveChordNoteAction()
    {
        var oldState = _chord.Clone();
        var newState = _chord.Clone();
        newState.ChordNotes.Remove(newState.ChordNotes.First(n => n.StringIndex == _stringIndex));
        NewChordState = newState;
        Outcome = EditChordStringOutcome.RemovedChordNote;

        return BuildReshapeAction(oldState, newState);
    }

    private IChartAction BuildAddChordNoteAction()
    {
        var oldState = _chord.Clone();
        var newState = _chord.Clone();
        newState.ChordNotes.Add(new ChordNote { StringIndex = _stringIndex, Sustain = FractionalPosition.Zero });
        NewChordState = newState;
        Outcome = EditChordStringOutcome.AddedChordNote;

        return BuildReshapeAction(oldState, newState);
    }

    /// <summary>
    ///     Commits a chord whose NOTE SET changed. Adding/removing a
    ///     chord note and leaving <c>TemplateId</c> alone would, on right-clicking one
    ///     string of a chord that shares its template with ten others, leave every
    ///     one of them describing a shape it does not play, permanently (the
    ///     export would write the chord under a chordId whose template lacks the
    ///     string, the 3D hand-shape bar would draw the old shape, and RefretPlanner
    ///     would pin it as template drift). The rule lives in
    ///     <see cref="ChordReshapeAction" />, shared with the placement path.
    /// </summary>
    private IChartAction BuildReshapeAction(Chord oldState, Chord newState) => ChordReshapeAction.Build(_placedChords, _arrangement, _chord, oldState, newState, _onPostExecute);
}
