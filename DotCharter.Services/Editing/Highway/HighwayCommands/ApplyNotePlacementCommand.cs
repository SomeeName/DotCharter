using System.Collections.ObjectModel;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     The one creation verb: a TOGGLE at (position, string) — create a note,
///     add the string to the chord already there, remove it again, and delete
///     the sound when the last string goes. EOF's XOR-at-the-seek-position
///     (<c>editor.c:3988-4008</c>, <c>:4035-4044</c>) and rscharter's
///     <c>toggleStringForSound</c> (<c>GuitarModeHandler.java:193-249</c>).
///     Driven by the pointer (right-click an empty lane) and by
///     the keyboard pen's <c>1..9</c>.
/// </summary>
internal sealed class ApplyNotePlacementCommand : IHighwayEditCommand
{
    private readonly Arrangement? _arrangement;
    private readonly IStrictCollisionService _collisionService;
    private readonly Action? _onPostExecute;
    private readonly ObservableCollection<Chord> _placedChords;
    private readonly ObservableCollection<GuitarNote> _placedNotes;
    private readonly int _previewFret;
    private readonly int _stringIndex;
    private readonly FractionalPosition _targetPosition;

    public ApplyNotePlacementCommand(
        ObservableCollection<GuitarNote> placedNotes,
        ObservableCollection<Chord> placedChords,
        IStrictCollisionService collisionService,
        Arrangement? arrangement,
        FractionalPosition targetPosition,
        int stringIndex,
        int previewFret,
        Action? onPostExecute)
    {
        _placedNotes = placedNotes;
        _placedChords = placedChords;
        _collisionService = collisionService;
        _arrangement = arrangement;
        _targetPosition = targetPosition;
        _stringIndex = stringIndex;
        _previewFret = previewFret;
        _onPostExecute = onPostExecute;
    }

    public bool ExistingChordFound { get; private set; }
    public int ExistingNotesCount { get; private set; }
    public bool ExactNoteRemoved { get; private set; }
    public int ActionCount { get; private set; }

    /// <summary>What the placement left behind on that string: <c>true</c> when
    /// the string is now PLAYED (created or added), <c>false</c> when it was
    /// removed. The pen reads it to decide whether to point the string-edit mask
    /// and the selection at the string it just pressed.</summary>
    public bool StringIsNowPlayed { get; private set; }

    public bool Execute(IHistoryService history)
    {
        var action = BuildAction();
        return action != null && history.Do(action);
    }

    private IChartAction? BuildAction()
    {
        var actions = new List<IChartAction>();
        var existingChord = _placedChords.FirstOrDefault(c => c.Position == _targetPosition);
        var existingNotes = _placedNotes.Where(n => n.Position == _targetPosition).ToList();
        var exactNote = existingNotes.FirstOrDefault(n => n.StringIndex == _stringIndex);

        ExistingChordFound = existingChord != null;
        ExistingNotesCount = existingNotes.Count;
        ExactNoteRemoved = exactNote != null;

        if (existingChord != null)
            AddChordPlacementActions(actions, existingChord);
        else if (existingNotes.Count > 0)
            AddExistingNotePlacementActions(actions, existingNotes, exactNote);
        else
            AddNewNotePlacementActions(actions);

        ActionCount = actions.Count;
        return actions.Count == 0 ? null : new CompositeAction(actions);
    }

    private void AddChordPlacementActions(ICollection<IChartAction> actions, Chord existingChord)
    {
        var existingChordNote = existingChord.ChordNotes.FirstOrDefault(n => n.StringIndex == _stringIndex);
        if (existingChordNote != null)
        {
            if (existingChord.ChordNotes.Count <= 2)
            {
                // FirstOrDefault, not First: the guard admits Count == 1, and a
                // one-note chord is routine rather than exotic —
                // ChartSpacingEnforcer.RemoveMutedNotesFromPartiallyMutedChords
                // produces them on every save, and both readers append
                // <chordNote> with no minimum. `First` threw on the empty
                // sequence. The sibling EditChordStringCommand.BuildCollapseAction
                // has always been written this way.
                var remaining = existingChord.ChordNotes.FirstOrDefault(n => n != existingChordNote);
                actions.Add(new RemoveChordsAction(_placedChords, new[] { existingChord }, _onPostExecute));

                // Nothing survives a one-note chord: removing it IS the edit.
                if (remaining == null) return;

                // Carry the whole sound, not just where it sits. Copying only
                // string/sustain/fret silently dropped mute, HOPO, harmonic,
                // vibrato, tremolo, link-next, slides and the bend curve every
                // time a two-note chord collapsed — the same field set the
                // sibling has always carried.
                var newNote = new GuitarNote
                {
                    Position = existingChord.Position,
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
                    BendValues = new ObservableCollection<BendValue>(
                        remaining.BendValues.Select(b => b.Clone()).ToList()),
                    Accent = existingChord.Accent,
                    Ignore = existingChord.Ignore,
                    PassOtherNotes = existingChord.PassOtherNotes,
                    Fret = remaining.Fret
                };
                actions.Add(new AddNotesAction(_placedNotes, new[] { newNote }, _onPostExecute));
            }
            else
            {
                var oldState = existingChord.Clone();
                var newState = existingChord.Clone();
                newState.ChordNotes.Remove(newState.ChordNotes.First(n => n.StringIndex == _stringIndex));
                actions.Add(BuildReshape(existingChord, oldState, newState));
            }
        }
        else
        {
            var oldState = existingChord.Clone();
            var newState = existingChord.Clone();
            newState.ChordNotes.Add(new ChordNote
            { StringIndex = _stringIndex, Fret = _previewFret, Sustain = FractionalPosition.Zero });
            actions.Add(BuildReshape(existingChord, oldState, newState));
            StringIsNowPlayed = true;
        }
    }

    /// <summary>
    ///     Adding or removing a chord string CHANGES THE SHAPE, so the chord has
    ///     to be repointed at a template describing it. This branch must not emit
    ///     a bare <c>MutateChordsAction</c> — the sibling right-click path
    ///     (<see cref="EditChordStringCommand" />) repoints, and without it
    ///     placing into an existing chord would leave the chord and
    ///     its template describing different shapes and the added string would be
    ///     discarded on the next save (an rscp <c>&lt;chordNote&gt;</c> has no
    ///     <c>fret</c> attribute). Both go through
    ///     <see cref="ChordReshapeAction" />.
    /// </summary>
    private IChartAction BuildReshape(Chord target, Chord oldState, Chord newState) => ChordReshapeAction.Build(_placedChords, _arrangement, target, oldState, newState, _onPostExecute);

    private void AddExistingNotePlacementActions(
        ICollection<IChartAction> actions,
        IReadOnlyList<GuitarNote> existingNotes,
        GuitarNote? exactNote)
    {
        if (exactNote != null)
        {
            actions.Add(new RemoveNotesAction(_placedNotes, new[] { exactNote }, _onPostExecute));
            return;
        }

        var newChord = new Chord { Position = _targetPosition, TemplateId = -1 };
        foreach (var existingNote in existingNotes)
        {
            // Carry the WHOLE sound, through the one shared converter. Copying
            // string/fret/sustain here would drop mute, HOPO, harmonic, vibrato,
            // tremolo, link-next, slides, bass picking and the bend curve every
            // time loose notes are promoted into a chord - the same fault the
            // COLLAPSE branch above avoids.
            newChord.ChordNotes.Add(SoundConversion.ToChordNote(existingNote));
            actions.Add(new RemoveNotesAction(_placedNotes, new[] { existingNote }, _onPostExecute));
        }

        newChord.ChordNotes.Add(new ChordNote
        { StringIndex = _stringIndex, Fret = _previewFret, Sustain = FractionalPosition.Zero });
        actions.Add(new AddChordsAction(_placedChords, new[] { newChord }, _onPostExecute));
        StringIsNowPlayed = true;
    }

    private void AddNewNotePlacementActions(ICollection<IChartAction> actions)
    {
        var newNote = new GuitarNote
        {
            Position = _targetPosition,
            StringIndex = _stringIndex,
            Fret = _previewFret,
            Sustain = FractionalPosition.Zero
        };
        var collisionResult = _collisionService.CalculateNoteCollisions(new[] { newNote },
            _placedNotes, new List<GuitarNote>());

        if (collisionResult.NotesToDestroy.Count > 0)
            actions.Add(new RemoveNotesAction(_placedNotes, collisionResult.NotesToDestroy, _onPostExecute));
        actions.Add(new AddNotesAction(_placedNotes, new[] { newNote }, _onPostExecute));
        StringIsNowPlayed = true;
    }
}
