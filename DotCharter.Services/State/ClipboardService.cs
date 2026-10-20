using DotCharter.Services.Editing.Catalog;

namespace DotCharter.Services.State;

public class ClipboardPayload
{
    public FractionalPosition BasePosition { get; set; } = FractionalPosition.Zero;
    public List<GuitarNote> Notes { get; set; } = [];
    public List<Chord> Chords { get; set; } = [];
    public List<HandShape> HandShapes { get; set; } = [];
    public List<EventPoint> EventPoints { get; set; } = [];
    public List<ToneChange> ToneChanges { get; set; } = [];
    public List<FretAnchor> Anchors { get; set; } = [];

    /// <summary>
    ///     The arrangement the content was copied from (string count, capo, tuning),
    ///     so a paste can be checked against its destination the way a phrase
    /// catalog paste is. Null when nothing recorded it.
    /// </summary>
    public PhraseSourceContext? Source { get; set; }

    /// <summary>
    ///     Copies of the <see cref="ChordTemplate" />s the copied chords and hand
    ///     shapes reference, keyed by the SOURCE arrangement's template id.
    ///     <para>
    ///         Without these the payload carried raw ids, and an id only means
    ///         anything inside its own arrangement: pasting a Dmin barre copied
    ///         from Lead (id 7) into Rhythm, where id 7 is an open E5, produced a
    ///         chord that rendered and exported as E5 with Dmin frets — chord /
    ///         template divergence from the single most common editing gesture.
    ///         The paste path resolves-or-creates each of these in the target
    ///         arrangement and repoints the pasted element.
    ///     </para>
    /// </summary>
    public Dictionary<int, ChordTemplate> ChordTemplates { get; set; } = [];

    /// <summary>
    ///     A deep copy of this payload re-anchored so its <see cref="BasePosition" />
    ///     lands on <paramref name="targetPos" />. Every element is cloned, so the
    ///     source payload can be re-pasted any number of times.
    ///     <para>
    ///         The shift is expressed in <see cref="FractionalPosition" /> — beats and
    ///         fractions of a beat — never milliseconds, so a payload keeps its RHYTHM
    ///         when it lands somewhere with a different tempo. This is the one place
    ///         the offset is applied; the clipboard and the phrase catalog both go
    ///         through it so they cannot drift.
    ///     </para>
    /// </summary>
    public ClipboardPayload ShiftedTo(FractionalPosition targetPos)
    {
        var offset = targetPos - BasePosition;

        return new ClipboardPayload
        {
            BasePosition = targetPos,
            Notes = Notes.Select(n =>
            {
                var clone = n.Clone();
                clone.Position += offset;
                return clone;
            }).ToList(),
            Chords = Chords.Select(c =>
            {
                var clone = c.Clone();
                clone.Position += offset;
                return clone;
            }).ToList(),
            HandShapes = HandShapes.Select(h =>
            {
                var clone = h.Clone();
                clone.Position += offset;
                clone.EndPosition += offset;
                return clone;
            }).ToList(),
            EventPoints = EventPoints.Select(e => new EventPoint
            { Position = e.Position + offset, Phrase = e.Phrase, Section = e.Section }).ToList(),
            ToneChanges = ToneChanges.Select(t => new ToneChange
            { Position = t.Position + offset, ToneName = t.ToneName }).ToList(),
            Anchors = Anchors.Select(a =>
            {
                var clone = a.Clone();
                clone.Position += offset;
                return clone;
            }).ToList(),
            ChordTemplates = ChordTemplates.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
            Source = Source
        };
    }
}

public class ClipboardService : IClipboardService
{
    private readonly IAppLogger _logger;
    private ClipboardPayload? _payload;

    public ClipboardService(IAppLogger logger) => _logger = logger;

    public bool HasData => _payload != null && (_payload.Notes.Count > 0 || _payload.Chords.Count > 0 ||
                                                _payload.HandShapes.Count > 0 || _payload.EventPoints.Count > 0 ||
                                                _payload.ToneChanges.Count > 0 || _payload.Anchors.Count > 0);

    public void Copy(IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords, IEnumerable<HandShape> handShapes,
        IEnumerable<EventPoint> eventPoints, IEnumerable<ToneChange> toneChanges, IEnumerable<FretAnchor> anchors,
        IEnumerable<ChordTemplate>? chordTemplates = null, PhraseSourceContext? source = null)
    {
        var noteList = notes.ToList();
        var chordList = chords.ToList();
        var hsList = handShapes.ToList();
        var epList = eventPoints.ToList();
        var toneList = toneChanges.ToList();
        var anchorList = anchors.ToList();

        if (noteList.Count == 0 && chordList.Count == 0 && hsList.Count == 0 && epList.Count == 0 &&
            toneList.Count == 0 && anchorList.Count == 0)
        {
            _payload = null;
            _logger.Trace(AppLogCategory.Editing, "Clipboard cleared (nothing to copy).");
            return;
        }

        var minPos = FractionalPosition.Max(FractionalPosition.Zero, new FractionalPosition(999999));
        if (noteList.Any())
            minPos = FractionalPosition.Min(minPos, noteList.Min(n => n.Position));
        if (chordList.Any()) minPos = FractionalPosition.Min(minPos, chordList.Min(c => c.Position));
        if (hsList.Any())
            minPos = FractionalPosition.Min(minPos, hsList.Min(h => h.Position));
        if (epList.Any()) minPos = FractionalPosition.Min(minPos, epList.Min(e => e.Position));
        if (toneList.Any()) minPos = FractionalPosition.Min(minPos, toneList.Min(t => t.Position));
        if (anchorList.Any()) minPos = FractionalPosition.Min(minPos, anchorList.Min(a => a.Position));

        _payload = new ClipboardPayload
        {
            BasePosition = minPos,
            Notes = noteList.Select(n => n.Clone()).ToList(),
            Chords = chordList.Select(c => c.Clone()).ToList(),
            HandShapes = hsList.Select(h => h.Clone()).ToList(),
            EventPoints = epList.Select(e => new EventPoint
            { Position = e.Position, Phrase = e.Phrase, Section = e.Section }).ToList(),
            ToneChanges = toneList.Select(t => new ToneChange
            { Position = t.Position, ToneName = t.ToneName }).ToList(),
            Anchors = anchorList.Select(a => a.Clone()).ToList(),
            // Cloned, and keyed by the SOURCE id: the templates must survive the
            // source arrangement being edited (or closed) between copy and paste.
            ChordTemplates = (chordTemplates ?? [])
                .Where(t => t.Id >= 0)
                .GroupBy(t => t.Id)
                .ToDictionary(g => g.Key, g => g.First().Clone()),
            Source = source
        };

        _logger.Info(AppLogCategory.Editing,
            $"Copied to clipboard: notes={noteList.Count}, chords={chordList.Count}, handShapes={hsList.Count}, events={epList.Count}, tones={toneList.Count}, anchors={anchorList.Count}, basePosition={minPos}.");
    }

    public ClipboardPayload? GetPastedData(FractionalPosition targetPos)
    {
        if (!HasData || _payload == null)
        {
            _logger.Trace(AppLogCategory.Editing, $"Paste requested at {targetPos} but clipboard is empty.");
            return null;
        }

        _logger.Info(AppLogCategory.Editing,
            $"Pasting clipboard at {targetPos} (offset={targetPos - _payload.BasePosition}): notes={_payload.Notes.Count}, chords={_payload.Chords.Count}, handShapes={_payload.HandShapes.Count}, events={_payload.EventPoints.Count}, tones={_payload.ToneChanges.Count}, anchors={_payload.Anchors.Count}.");

        return _payload.ShiftedTo(targetPos);
    }
}
