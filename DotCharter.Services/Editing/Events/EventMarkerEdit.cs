using System.Collections.ObjectModel;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Events;

/// <summary>What the event dialog saves on one marker: the section, phrase and tone, and the RS event codes.</summary>
public sealed record EventMarkerFields(
    string Section,
    string Phrase,
    SectionStatus Status,
    string Events,
    bool SoloPhrase,
    string Tone);

/// <summary>
///     The undo actions the event dialog records for one arrangement. The marker
///     is REPLACED — the existing <see cref="EventPoint" /> removed and a freshly
///     built one added — so every field of <see cref="EventPoint" /> is carried in
///     the initializer below; a field left off is a silent deletion of that field
///     on every edited marker (<c>UI/EventDialogFidelityTests</c>).
/// </summary>
public static class EventMarkerEdit
{
    /// <summary>A tone change within this of the marker is the marker's tone: imports place tones off-grid.</summary>
    public const double ToneToleranceMs = 60.0;

    /// <summary>An event point within this of the marker is the marker: they are authored on the grid.</summary>
    public const double EventToleranceMs = 5.0;

    public static EventPoint? EventAt(Arrangement arrangement, double timeMs, IList<BeatMarker> beats) =>
        arrangement.EventPoints.FirstOrDefault(ep =>
            Math.Abs(ep.Position.GetPositionMs(beats) - timeMs) < EventToleranceMs);

    /// <summary>The tone change nearest <paramref name="timeMs" /> inside <see cref="ToneToleranceMs" />.</summary>
    public static ToneChange? NearestTone(IEnumerable<ToneChange> tones, double timeMs, IList<BeatMarker> beats)
    {
        ToneChange? best = null;
        var bestDelta = double.MaxValue;
        foreach (var t in tones)
        {
            var delta = Math.Abs(t.Position.GetPositionMs(beats) - timeMs);
            if (delta >= ToneToleranceMs || delta >= bestDelta) continue;
            best = t;
            bestDelta = delta;
        }

        return best;
    }

    /// <summary>Appends to <paramref name="actions" /> whatever makes <paramref name="arrangement" />'s marker at
    ///     <paramref name="position" /> say <paramref name="fields" />; nothing when it already does.</summary>
    public static void AppendActions(Arrangement arrangement, FractionalPosition position, double timeMs,
        IList<BeatMarker> beats, EventMarkerFields fields, List<IChartAction> actions)
    {
        var existingEvent = EventAt(arrangement, timeMs, beats);
        // A marker carrying ONLY event codes is a real marker — the crowd cues
        // and the metronome ticks live on their own, without a section or a
        // phrase — so it has to keep the point alive.
        var shouldSaveEvent = !string.IsNullOrWhiteSpace(fields.Section) ||
                              !string.IsNullOrWhiteSpace(fields.Phrase) ||
                              !string.IsNullOrWhiteSpace(fields.Events);
        // Status only means something ON a section — a phrase-only event
        // always carries None, whatever the (disabled) combo showed.
        var status = string.IsNullOrWhiteSpace(fields.Section) ? SectionStatus.None : fields.Status;
        var eventChanged = existingEvent == null ||
                           existingEvent.Position != position ||
                           existingEvent.Section != fields.Section ||
                           existingEvent.Phrase != fields.Phrase ||
                           existingEvent.Status != status ||
                           existingEvent.Events != fields.Events;
        if (existingEvent != null && (!shouldSaveEvent || eventChanged))
            actions.Add(new RemoveEventPointsAction(arrangement.EventPoints, [existingEvent]));

        if (shouldSaveEvent && eventChanged)
        {
            actions.Add(new AddEventPointsAction(arrangement.EventPoints,
            [
                new EventPoint
                {
                    Position = position,
                    Section = fields.Section,
                    Phrase = fields.Phrase,
                    Status = status,
                    Events = fields.Events
                }
            ]));
        }

        // Solo is a property of the PHRASE, not of this marker, so it is written
        // whenever a phrase name is present — including when the marker itself is
        // unchanged and the only edit was the checkbox.
        if (!string.IsNullOrWhiteSpace(fields.Phrase))
            actions.Add(new SetPhraseSoloAction(arrangement, fields.Phrase, fields.SoloPhrase));

        AppendToneActions(arrangement.ToneChanges, position, timeMs, beats, fields.Tone, actions);
    }

    private static void AppendToneActions(ObservableCollection<ToneChange> tones, FractionalPosition position,
        double timeMs, IList<BeatMarker> beats, string tone, List<IChartAction> actions)
    {
        var existingTone = NearestTone(tones, timeMs, beats);
        // Warned about in the dialog when the tone has no body, never refused.
        var shouldSaveTone = !string.IsNullOrWhiteSpace(tone);
        var toneChanged = existingTone == null ||
                          existingTone.Position != position ||
                          existingTone.ToneName != tone;
        if (existingTone != null && (!shouldSaveTone || toneChanged))
            actions.Add(new RemoveToneChangesAction(tones, [existingTone]));

        if (shouldSaveTone && toneChanged)
            actions.Add(new AddToneChangesAction(tones, [new ToneChange { Position = position, ToneName = tone }]));
    }
}
