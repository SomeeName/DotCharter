using System.Collections.ObjectModel;
using DotCharter.Services.State;
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

internal sealed class AdjustSelectedSustainsCommand : IHighwayEditCommand
{
    private readonly IList<BeatMarker> _beats;
    private readonly AppConfig _config;
    private readonly FractionalPosition _delta;
    private readonly Action? _onPostExecute;
    private readonly ObservableCollection<Chord> _placedChords;
    private readonly ObservableCollection<GuitarNote> _placedNotes;
    private readonly IReadOnlyList<Chord> _selectedChords;
    private readonly IReadOnlyList<GuitarNote> _selectedNotes;
    private readonly double _totalAudioLengthMs;

    public AdjustSelectedSustainsCommand(
        ObservableCollection<GuitarNote> placedNotes,
        ObservableCollection<Chord> placedChords,
        IReadOnlyList<GuitarNote> selectedNotes,
        IReadOnlyList<Chord> selectedChords,
        FractionalPosition delta,
        double totalAudioLengthMs,
        IList<BeatMarker> beats,
        AppConfig config,
        Action? onPostExecute)
    {
        _placedNotes = placedNotes;
        _placedChords = placedChords;
        _selectedNotes = selectedNotes;
        _selectedChords = selectedChords;
        _delta = delta;
        _totalAudioLengthMs = totalAudioLengthMs;
        _beats = beats;
        _config = config;
        _onPostExecute = onPostExecute;
    }

    public bool Execute(IHistoryService history)
    {
        var action = BuildAction();
        return action != null && history.Do(action);
    }

    /// <summary>What the step would do to each selected sound, writing nothing (the inspector's plan).</summary>
    public IReadOnlyList<SustainStepOutcome> Preview()
    {
        var outcomes = new List<SustainStepOutcome>(_selectedNotes.Count + _selectedChords.Count);
        foreach (var note in _selectedNotes)
        {
            var (sustain, limit) = AdjustSustain(note.Position, note.Sustain, note.LinkNext, note.PassOtherNotes);
            outcomes.Add(new SustainStepOutcome(note, sustain != note.Sustain, limit));
        }

        foreach (var chord in _selectedChords)
        {
            var changes = false;
            var limit = SustainStepLimit.None;
            foreach (var chordNote in chord.ChordNotes)
            {
                var (sustain, cut) = AdjustSustain(chord.Position, chordNote.Sustain, chordNote.LinkNext, chord.PassOtherNotes);
                changes |= sustain != chordNote.Sustain;
                if (limit == SustainStepLimit.None) limit = cut;
            }

            outcomes.Add(new SustainStepOutcome(chord, changes, limit));
        }

        return outcomes;
    }

    private IChartAction? BuildAction()
    {
        var actions = new List<IChartAction>(2);

        var noteAction = BuildNoteAction();
        if (noteAction != null) actions.Add(noteAction);

        var chordAction = BuildChordAction();
        if (chordAction != null) actions.Add(chordAction);

        return actions.Count switch
        {
            0 => null,
            1 => actions[0],
            _ => new CompositeAction(actions)
        };
    }

    private IChartAction? BuildNoteAction()
    {
        if (_selectedNotes.Count == 0) return null;

        var changedNotes = new List<GuitarNote>();
        var oldStates = new List<GuitarNote>();
        var newStates = new List<GuitarNote>();

        foreach (var note in _selectedNotes)
        {
            var oldState = note.Clone();
            var newState = note.Clone();
            newState.Sustain = AdjustSustain(
                newState.Position,
                newState.Sustain,
                newState.LinkNext,
                newState.PassOtherNotes).Sustain;

            if (newState.Sustain == oldState.Sustain) continue;

            // Bend points are absolute positions — move them with the sustain so
            // they stay proportional and never ring past the new end.
            BendMath.RescaleToSustain(newState.BendValues, newState.Position, oldState.Sustain, newState.Sustain);

            changedNotes.Add(note);
            oldStates.Add(oldState);
            newStates.Add(newState);
        }

        return changedNotes.Count == 0
            ? null
            : new MutateNotesAction(_placedNotes, changedNotes, oldStates, newStates, _onPostExecute,
                MergeVerbs.Sustain);
    }

    private IChartAction? BuildChordAction()
    {
        if (_selectedChords.Count == 0) return null;

        var changedChords = new List<Chord>();
        var oldStates = new List<Chord>();
        var newStates = new List<Chord>();

        foreach (var chord in _selectedChords)
        {
            var oldState = chord.Clone();
            var newState = chord.Clone();
            foreach (var chordNote in newState.ChordNotes)
            {
                var priorSustain = chordNote.Sustain;
                chordNote.Sustain = AdjustSustain(
                    newState.Position,
                    chordNote.Sustain,
                    chordNote.LinkNext,
                    newState.PassOtherNotes).Sustain;
                // Move each string's bend points with its sustain (see BuildNoteAction).
                BendMath.RescaleToSustain(
                    chordNote.BendValues, newState.Position, priorSustain, chordNote.Sustain);
            }

            if (ChordSustainsEqual(oldState, newState)) continue;

            changedChords.Add(chord);
            oldStates.Add(oldState);
            newStates.Add(newState);
        }

        return changedChords.Count == 0
            ? null
            : new MutateChordsAction(_placedChords, changedChords, oldStates, newStates, _onPostExecute,
                MergeVerbs.Sustain);
    }

    /// <summary>The stepped sustain, and what cut the step short (<see cref="SustainStepLimit.None" /> when nothing did).</summary>
    private (FractionalPosition Sustain, SustainStepLimit Limit) AdjustSustain(
        FractionalPosition position,
        FractionalPosition sustain,
        bool linkNext,
        bool passOtherNotes)
    {
        if (linkNext) return (sustain, SustainStepLimit.Linked);

        var maxSustain = TimelineMath.PositionFromMs(_totalAudioLengthMs, _beats) - position;
        var ceiling = SustainStepLimit.AudioEnd;
        if (!passOtherNotes && FindNextSoundPosition(position) is { } nextPosition)
        {
            maxSustain = nextPosition - position - ChartSpacingEnforcer.GetMinDistance(position, _beats, _config);
            ceiling = SustainStepLimit.NextSound;
        }

        maxSustain = FractionalPosition.Max(FractionalPosition.Zero, maxSustain);

        var adjustedSustain = sustain + _delta;
        if (adjustedSustain < FractionalPosition.Zero) return (FractionalPosition.Zero, SustainStepLimit.Zero);
        return adjustedSustain > maxSustain ? (maxSustain, ceiling) : (adjustedSustain, SustainStepLimit.None);
    }

    private FractionalPosition? FindNextSoundPosition(FractionalPosition position)
    {
        FractionalPosition? nextPosition = null;

        foreach (var note in _placedNotes)
        {
            if (note.Position <= position) continue;
            if (nextPosition == null || note.Position < nextPosition.Value) nextPosition = note.Position;
        }

        foreach (var chord in _placedChords)
        {
            if (chord.Position <= position) continue;
            if (nextPosition == null || chord.Position < nextPosition.Value) nextPosition = chord.Position;
        }

        return nextPosition;
    }

    private static bool ChordSustainsEqual(Chord oldState, Chord newState)
    {
        if (oldState.ChordNotes.Count != newState.ChordNotes.Count) return false;
        for (var i = 0; i < oldState.ChordNotes.Count; i++)
        {
            if (!ChordNoteSustainEqual(oldState.ChordNotes[i], newState.ChordNotes[i]))
                return false;
        }

        return true;
    }

    private static bool ChordNoteSustainEqual(ChordNote oldState, ChordNote newState) => oldState.Sustain == newState.Sustain;
}
