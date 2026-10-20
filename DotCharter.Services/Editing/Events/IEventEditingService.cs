using EventPoint = DotCharter.Core.Domain.Events.EventPoint;

namespace DotCharter.Services.Editing.Events;

public interface IEventEditingService
{
    void RemoveEventPoints(IEnumerable<EventPoint> eventPoints);

    /// <summary>
    ///     The section name the last saved marker used, and the phrase that went
    ///     with it. Remembered so the same one can be stamped again without
    ///     reopening the dialog — sections repeat hard (an owner
    ///     session placed 18 choruses among 44 markers, and the corpus is 31.8%
    ///     chorus and 19.1% verse). Empty until a marker has been saved.
    /// </summary>
    string LastSection { get; }

    string LastPhrase { get; }

    /// <summary>Records what a saved marker used, so the stamp can repeat it.</summary>
    void RememberLastMarker(string section, string phrase);

    /// <summary>
    ///     The RS event codes and the arrangement NAMES the last saved marker
    ///     used — the rest of the dialog's answer, alongside
    ///     <see cref="LastSection" /> / <see cref="LastPhrase" />.
    ///     <para>
    ///         Placing structure is a RUN, not a set of unrelated decisions: a
    ///         chorus is not four bars long, so the same section goes down over
    ///         and over, on the same set of arrangements, until the song changes.
    ///         Re-picking all of it at every marker is the work this removes.
    ///         Both are empty until a marker has been saved.
    ///     </para>
    /// </summary>
    string LastEvents { get; }

    IReadOnlyList<string> LastArrangementNames { get; }

    /// <summary>Records the other half of a saved marker's answer.</summary>
    void RememberLastTargets(string events, IEnumerable<string> arrangementNames);

    /// <summary>
    ///     Drops <see cref="LastSection" /> / <see cref="LastPhrase" /> at
    ///     <paramref name="position" /> as one undoable step, merging onto a marker
    ///     already there rather than stacking a second one. Returns false when
    ///     nothing has been stamped yet or the marker would change nothing.
    /// </summary>
    bool StampLastMarker(FractionalPosition position);
    bool CommitEventDragAndDrop(IEnumerable<EventPoint> oldStates, IEnumerable<EventPoint> newStates);
}
