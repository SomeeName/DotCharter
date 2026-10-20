using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using EventPoint = DotCharter.Core.Domain.Events.EventPoint;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing;

/// <summary>
/// Owns clipboard copy / paste / paste-special operations for the global
/// editing pipeline. Pulled out of <see cref="GlobalEditingCoordinator"/>
/// so the (significant) clipboard logic — selection promotion, range
/// computation, overwrite-target collection, commit fan-out — can be
/// understood and unit-tested in isolation.
/// </summary>
public sealed class EditingClipboardCoordinator
{
    private readonly IClipboardService _clipboardManager;
    private readonly IConfigurationService _configService;
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly ISelectionService _selectionManager;
    private readonly ITempoService _tempoService;
    private readonly IAppLogger _logger;

    public EditingClipboardCoordinator(
        IClipboardService clipboardManager,
        IConfigurationService configService,
        IHighwayEditingService highwayEditingService,
        ISelectionService selectionManager,
        ITempoService tempoService,
        IAppLogger logger)
    {
        _clipboardManager = clipboardManager;
        _configService = configService;
        _highwayEditingService = highwayEditingService;
        _selectionManager = selectionManager;
        _tempoService = tempoService;
        _logger = logger;
    }

    public void Copy(TrackEditorContext track, EditorFocusArea focusArea)
    {
        // A copy carries EXACTLY the selection. It must not promote the
        // hand shapes over the copied sounds and the governing FHP: one
        // chord inside a bar would copy the whole bar, and the bar's earlier start
        // would become the payload origin: the chord would paste late, by the distance
        // from the bar start to the chord. A hand shape or anchor comes along
        // only when it is selected itself (Ctrl-click and Shift-range select
        // them; so does a click in their own band).
        var notesToCopy = _selectionManager.SelectedNotes.ToList();
        var chordsToCopy = _selectionManager.SelectedChords.ToList();
        var handShapesToCopy = _selectionManager.SelectedHandShapes.ToList();
        var anchorsToCopy = _selectionManager.SelectedAnchors.ToList();
        var eventsToCopy = _selectionManager.SelectedEventPoints.ToList();
        var tonesToCopy = _selectionManager.SelectedTones.ToList();

        // Copying in the EVENT ROWS copies events, not whatever was left selected
        // on the highway. An owner session selected a note, then a
        // section marker, then copied — and the paste dropped a stray note that
        // merged into a CHORD at the destination, undone one second later. The
        // focus area is the user saying which timeline they are working in; a
        // marker copy that also moves notes is never what was asked for.
        if (focusArea == EditorFocusArea.TimelineEvents && (eventsToCopy.Count > 0 || tonesToCopy.Count > 0))
        {
            notesToCopy.Clear();
            chordsToCopy.Clear();
            handShapesToCopy.Clear();
            anchorsToCopy.Clear();
        }

        // The chords/hand shapes carry raw TemplateIds, which mean nothing
        // outside their own arrangement — carry the templates themselves so the
        // paste can resolve-or-create them in the target.
        _clipboardManager.Copy(notesToCopy, chordsToCopy, handShapesToCopy, eventsToCopy, tonesToCopy, anchorsToCopy,
            ReferencedTemplates(track, chordsToCopy, handShapesToCopy),
            PhraseSourceContext.From(track.Arrangement));
        _logger.Info(AppLogCategory.Editing,
            $"Copied unified selection from track '{track.Arrangement.Name}'. FocusArea={focusArea}, notes={notesToCopy.Count}, chords={chordsToCopy.Count}, handshapes={handShapesToCopy.Count}, events={eventsToCopy.Count}, tones={tonesToCopy.Count}, anchors={anchorsToCopy.Count}.");
    }

    /// <summary>
    ///     The selected highway content, PROMOTED: hand shapes overlapping the
    ///     selected span and the fret anchors governing it are pulled in even
    ///     though the user only selected sounds. Used by phrase CAPTURE only — a
    ///     stored phrase is a self-contained unit that needs its hand position.
    /// <see cref="Copy" /> carries the bare selection.
    ///     Visible for testing.
    /// </summary>
    public (List<GuitarNote> Notes, List<Chord> Chords, List<HandShape> HandShapes, List<FretAnchor> Anchors)
        CollectHighwayContent(TrackEditorContext track, EditorFocusArea focusArea)
    {
        var notesToCopy = _selectionManager.SelectedNotes.ToList();
        var chordsToCopy = _selectionManager.SelectedChords.ToList();
        var handShapesToCopy = _selectionManager.SelectedHandShapes.ToList();
        var anchorsToCopy = _selectionManager.SelectedAnchors.ToList();

        if (focusArea == EditorFocusArea.NoteHighway && (notesToCopy.Count > 0 || chordsToCopy.Count > 0))
        {
            var minPos = FractionalPosition.Min(
                notesToCopy.Count > 0 ? notesToCopy.Min(n => n.Position) : new FractionalPosition(9999),
                chordsToCopy.Count > 0 ? chordsToCopy.Min(c => c.Position) : new FractionalPosition(9999)
            );
            var maxPos = FractionalPosition.Max(
                notesToCopy.Count > 0 ? notesToCopy.Max(n => n.EndPosition) : FractionalPosition.Zero,
                chordsToCopy.Count > 0 ? chordsToCopy.Max(c => c.EndPosition) : FractionalPosition.Zero
            );

            foreach (var hs in track.HandShapes.Where(hs => hs.EndPosition > minPos && hs.Position < maxPos))
            {
                if (!handShapesToCopy.Contains(hs))
                    handShapesToCopy.Add(hs);
            }

            // ...and the bar over each copied SOUND, which the span test above
            // cannot reach. A strum has ZERO sustain by convention (the hand
            // shape carries the duration), so copying one chord gives
            // minPos == maxPos and `hs.Position < maxPos` rejects the bar that
            // starts exactly on it — the commonest copy there is. The owner's
            // session logged "chords=1, handShapes=0, anchors=1" for
            // exactly that, then spent 90 seconds pasting, undoing and
            // re-copying until they Ctrl-clicked the bar in by hand. The
            // governing ANCHOR was already promoted by its own rule below,
            // which is why the asymmetry was so confusing.
            handShapesToCopy.AddRange(SoundCompanions.CoveringHandShapes(
                notesToCopy.Select(n => n.Position).Concat(chordsToCopy.Select(c => c.Position)),
                track.HandShapes, handShapesToCopy));

            // An anchor GOVERNS everything from its position until the next
            // anchor, so the FHP in force at the selection start usually
            // begins BEFORE the first copied note — a position-in-range test
            // silently drops it ("copy doesn't take the FHP with it"). Carry
            // it along CLAMPED to the selection start: the paste then
            // recreates the governing fret at the first pasted note instead
            // of shifting the whole payload back to the anchor's origin.
            var governing = track.Anchors
                .Where(a => a.Position <= minPos)
                .MaxBy(a => a.Position);
            if (governing != null)
            {
                var clamped = governing.Clone();
                clamped.Position = minPos;
                anchorsToCopy.RemoveAll(a => a.Position <= minPos);
                anchorsToCopy.Add(clamped);
            }

            foreach (var a in track.Anchors.Where(a => a.Position > minPos && a.Position <= maxPos))
            {
                if (!anchorsToCopy.Contains(a))
                    anchorsToCopy.Add(a);
            }
        }

        return (notesToCopy, chordsToCopy, handShapesToCopy, anchorsToCopy);
    }

    /// <summary>The distinct templates the copied chords + hand shapes point at.
    /// A dangling id simply has no entry — the paste then leaves the element's
    /// id alone rather than inventing a shape.</summary>
    private static List<ChordTemplate> ReferencedTemplates(
        TrackEditorContext track, IEnumerable<Chord> chords, IEnumerable<HandShape> handShapes)
    {
        var ids = new HashSet<int>();
        foreach (var chord in chords)
        {
            if (chord.TemplateId >= 0)
                ids.Add(chord.TemplateId);
        }

        foreach (var handShape in handShapes)
        {
            if (handShape.TemplateId >= 0)
                ids.Add(handShape.TemplateId);
        }

        return track.Arrangement.ChordTemplates.Where(t => ids.Contains(t.Id)).ToList();
    }

    /// <summary>
    ///     Pastes the clipboard at the grid-snapped playhead. Checked like a phrase
    /// catalog paste: content the destination cannot hold (strings it does
    ///     not have, frets outside 0..24 or below its capo) is REFUSED, and a capo or
    ///     tuning that differs from the source is pasted with a warning. Returns null
    ///     when there was nothing to paste.
    /// </summary>
    public PhrasePasteOutcome? Paste(TrackEditorContext track, double currentTimeMs, int gridDivisor)
    {
        if (!_clipboardManager.HasData)
        {
            _logger.Info(AppLogCategory.Editing, "Paste requested but clipboard has no chart data.");
            return null;
        }

        var payload = ResolvePastePayload(track, currentTimeMs, gridDivisor);
        if (payload == null) return null;
        var check = CheckFits(payload, track, "Paste");
        if (check.IsBlocked) return new PhrasePasteOutcome(PhrasePasteResult.Blocked, check);

        var cfg = _configService.LoadAppConfig();
        var notes = cfg.PasteIncludeNotes ? payload.Notes : [];
        var chords = cfg.PasteIncludeChords ? payload.Chords : [];
        var hs = cfg.PasteIncludeHandShapes ? payload.HandShapes : [];
        var anchors = cfg.PasteIncludeFretAnchors ? payload.Anchors : [];
        var events = cfg.PasteIncludeEvents ? payload.EventPoints : [];
        var tones = cfg.PasteIncludeTones ? payload.ToneChanges : [];

        _logger.Info(AppLogCategory.Editing,
            $"Pasting clipboard into track '{track.Arrangement.Name}' at currentTimeMs={currentTimeMs:0.###}, gridDivisor={gridDivisor}. Notes={notes.Count}/{payload.Notes.Count}, chords={chords.Count}/{payload.Chords.Count}, handshapes={hs.Count}/{payload.HandShapes.Count}, events={events.Count}/{payload.EventPoints.Count}, tones={tones.Count}/{payload.ToneChanges.Count}, anchors={anchors.Count}/{payload.Anchors.Count}.");

        ApplyPasteCommit(track,
            [], notes,
            [], chords,
            [], hs,
            [], anchors,
            [], events,
            [], tones,
            payload.ChordTemplates);
        return Outcome(check);
    }

    /// <summary>Paste Special, with the same fit check as <see cref="Paste" />.</summary>
    public PhrasePasteOutcome? PasteSpecial(TrackEditorContext track, double currentTimeMs, int gridDivisor,
        PasteSpecialMode mode)
    {
        if (!_clipboardManager.HasData)
        {
            _logger.Info(AppLogCategory.Editing,
                $"PasteSpecial({mode}) requested but clipboard has no chart data.");
            return null;
        }

        var payload = ResolvePastePayload(track, currentTimeMs, gridDivisor);
        if (payload == null) return null;
        var check = CheckFits(payload, track, $"PasteSpecial({mode})");
        if (check.IsBlocked) return new PhrasePasteOutcome(PhrasePasteResult.Blocked, check);

        var (rangeStart, rangeEnd) = ComputePayloadRange(payload);
        var (notesToRemove, chordsToRemove, hsToRemove, anchorsToRemove, eventsToRemove, tonesToRemove) =
            CollectOverwriteTargets(track, payload, mode, rangeStart, rangeEnd);

        _logger.Info(AppLogCategory.Editing,
            $"PasteSpecial({mode}) into track '{track.Arrangement.Name}' at currentTimeMs={currentTimeMs:0.###}, range=[{rangeStart},{rangeEnd}]. " +
            $"Adding notes={payload.Notes.Count}, chords={payload.Chords.Count}, handshapes={payload.HandShapes.Count}, anchors={payload.Anchors.Count}, events={payload.EventPoints.Count}, tones={payload.ToneChanges.Count}. " +
            $"Removing notes={notesToRemove.Count}, chords={chordsToRemove.Count}, handshapes={hsToRemove.Count}, anchors={anchorsToRemove.Count}, events={eventsToRemove.Count}, tones={tonesToRemove.Count}.");

        ApplyPasteCommit(track,
            notesToRemove, payload.Notes,
            chordsToRemove, payload.Chords,
            hsToRemove, payload.HandShapes,
            anchorsToRemove, payload.Anchors,
            eventsToRemove, payload.EventPoints,
            tonesToRemove, payload.ToneChanges,
            payload.ChordTemplates);
        return Outcome(check);
    }

    private PhrasePasteCheck CheckFits(ClipboardPayload payload, TrackEditorContext track, string verb)
    {
        var check = PhrasePasteCompatibility.Evaluate(payload, payload.Source, track.Arrangement);
        if (check.Severity != PhrasePasteSeverity.Ok)
        {
            _logger.Info(AppLogCategory.Editing,
                $"{verb} into track '{track.Arrangement.Name}': {check.Severity} — " +
                string.Join("; ", check.Issues.Select(i => i.LocalizationKey)));
        }

        return check;
    }

    private static PhrasePasteOutcome Outcome(PhrasePasteCheck check) => new(
        check.Severity == PhrasePasteSeverity.Warning ? PhrasePasteResult.PastedWithWarning : PhrasePasteResult.Pasted,
        check);

    /// <summary>
    ///     Stores the current highway selection in the phrase catalog under
    ///     <paramref name="name" />, promoted by <see cref="CollectHighwayContent" />
    ///     (unlike <see cref="Copy" />), minus the event points and tone changes — see
    ///     <c>PhraseCatalogFragment</c> for why structure is not a riff.
    /// </summary>
    public PhraseCatalogEntry? CaptureToCatalog(
        IPhraseCatalogService catalog, TrackEditorContext track, EditorFocusArea focusArea, string name)
    {
        var (notes, chords, handShapes, anchors) = CollectHighwayContent(track, focusArea);
        return catalog.Add(name, track.Arrangement, notes, chords, handShapes, anchors);
    }

    /// <summary>
    ///     Pastes a catalog phrase at the grid-snapped playhead, through the same
    ///     history-backed commit every other paste uses — so it is one undo step
    ///     and the spacing enforcer's collateral is captured with it.
    ///     <para>
    ///         A phrase is pasted whole: unlike the clipboard, it is not filtered
    ///         by the <c>AppConfig.PasteInclude*</c> preferences. A catalog entry
    ///         is an explicit, named object whose element counts the user can see;
    ///         silently dropping half of it to a setting made for ad-hoc range
    ///         copies would be a surprise, not a preference.
    ///     </para>
    /// </summary>
    public PhrasePasteOutcome PastePhrase(
        TrackEditorContext track, PhraseCatalogEntry entry, double currentTimeMs, int gridDivisor)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var check = PhrasePasteCompatibility.Evaluate(entry, track.Arrangement);
        if (check.IsBlocked)
        {
            _logger.Info(AppLogCategory.Editing,
                $"Phrase '{entry.Name}' refused for track '{track.Arrangement.Name}': " +
                string.Join("; ", check.Issues.Select(i => i.LocalizationKey)));
            return new PhrasePasteOutcome(PhrasePasteResult.Blocked, check);
        }

        var targetPos = _tempoService.SnapToGrid(currentTimeMs, track.Beats, gridDivisor);
        var payload = entry.Payload.ShiftedTo(targetPos);

        _logger.Info(AppLogCategory.Editing,
            $"Pasting phrase '{entry.Name}' into track '{track.Arrangement.Name}' at {targetPos} " +
            $"(currentTimeMs={currentTimeMs:0.###}, gridDivisor={gridDivisor}): notes={payload.Notes.Count}, " +
            $"chords={payload.Chords.Count}, handshapes={payload.HandShapes.Count}, anchors={payload.Anchors.Count}, " +
            $"severity={check.Severity}.");

        ApplyPasteCommit(track,
            [], payload.Notes,
            [], payload.Chords,
            [], payload.HandShapes,
            [], payload.Anchors,
            [], [],
            [], [],
            payload.ChordTemplates);

        return new PhrasePasteOutcome(
            check.Severity == PhrasePasteSeverity.Warning
                ? PhrasePasteResult.PastedWithWarning
                : PhrasePasteResult.Pasted,
            check);
    }

    private ClipboardPayload? ResolvePastePayload(TrackEditorContext track, double currentTimeMs, int gridDivisor)
    {
        var targetPos = _tempoService.SnapToGrid(currentTimeMs, track.Beats, gridDivisor);
        return _clipboardManager.GetPastedData(targetPos);
    }

    /// <summary>
    /// Computes the [start..end] FractionalPosition span of every event in
    /// the payload. Visible for testing.
    /// </summary>
    public static (FractionalPosition start, FractionalPosition end) ComputePayloadRange(ClipboardPayload payload)
    {
        var positions = new List<FractionalPosition>();
        var ends = new List<FractionalPosition>();

        foreach (var n in payload.Notes) { positions.Add(n.Position); ends.Add(n.EndPosition); }
        foreach (var c in payload.Chords) { positions.Add(c.Position); ends.Add(c.EndPosition); }
        foreach (var h in payload.HandShapes) { positions.Add(h.Position); ends.Add(h.EndPosition); }
        foreach (var a in payload.Anchors) { positions.Add(a.Position); ends.Add(a.Position); }
        foreach (var e in payload.EventPoints) { positions.Add(e.Position); ends.Add(e.Position); }
        foreach (var t in payload.ToneChanges) { positions.Add(t.Position); ends.Add(t.Position); }

        if (positions.Count == 0) return (FractionalPosition.Zero, FractionalPosition.Zero);

        var start = positions[0];
        var end = ends[0];
        for (var i = 1; i < positions.Count; i++)
        {
            if (positions[i] < start) start = positions[i];
            if (ends[i] > end) end = ends[i];
        }
        return (start, end);
    }

    /// <summary>
    ///     The LAST ONSET in the payload - not its last END. Visible for testing.
    ///     <para>
    ///         <see cref="ComputePayloadRange" /> returns the span the payload
    ///         OCCUPIES, i.e. one sustain past the last onset, and a hand shape is
    ///         correctly cleared by overlapping that span. A note, chord, anchor,
    ///         marker or tone change is a POINT, and the ones the paste replaces
    ///         are the ones at an onset the paste writes to. Testing those against
    ///         the span's end over-reached by exactly the last sustain: copy a run
    ///         whose last note holds to beat 8, paste-special it, and the existing
    ///         sound starting at beat 8 - the neighbour immediately AFTER the
    ///         pasted content, which nothing was pasted over - was deleted too.
    ///     </para>
    /// </summary>
    public static FractionalPosition ComputePayloadLastOnset(ClipboardPayload payload)
    {
        FractionalPosition? last = null;

        void Consider(FractionalPosition position)
        {
            if (last == null || position > last.Value) last = position;
        }

        foreach (var n in payload.Notes) Consider(n.Position);
        foreach (var c in payload.Chords) Consider(c.Position);
        foreach (var h in payload.HandShapes) Consider(h.Position);
        foreach (var a in payload.Anchors) Consider(a.Position);
        foreach (var e in payload.EventPoints) Consider(e.Position);
        foreach (var t in payload.ToneChanges) Consider(t.Position);

        return last ?? FractionalPosition.Zero;
    }

    /// <summary>Visible for testing.</summary>
    internal static (List<GuitarNote> notes, List<Chord> chords, List<HandShape> hs, List<FretAnchor> anchors, List<EventPoint> events, List<ToneChange> tones)
        CollectOverwriteTargets(TrackEditorContext track, ClipboardPayload payload, PasteSpecialMode mode,
            FractionalPosition rangeStart, FractionalPosition rangeEnd)
    {
        var notes = new List<GuitarNote>();
        var chords = new List<Chord>();
        var hs = new List<HandShape>();
        var anchors = new List<FretAnchor>();
        var events = new List<EventPoint>();
        var tones = new List<ToneChange>();

        if (mode == PasteSpecialMode.Preserve) return (notes, chords, hs, anchors, events, tones);

        var clearAll = mode == PasteSpecialMode.OverwriteAll;

        // Point-like objects are replaced at the ONSETS the paste writes to;
        // only the hand-shape test below is a span OVERLAP, and it is the one
        // that wants rangeEnd. See ComputePayloadLastOnset.
        var lastOnset = ComputePayloadLastOnset(payload);

        if (clearAll || payload.Notes.Count > 0)
        {
            notes.AddRange(track.Notes
                .Where(n => n.Position >= rangeStart && n.Position <= lastOnset));
        }

        if (clearAll || payload.Chords.Count > 0)
        {
            chords.AddRange(track.Chords
                .Where(c => c.Position >= rangeStart && c.Position <= lastOnset));
        }

        if (clearAll || payload.HandShapes.Count > 0)
        {
            hs.AddRange(track.HandShapes
                .Where(h => h.EndPosition > rangeStart && h.Position < rangeEnd));
        }

        if (clearAll || payload.Anchors.Count > 0)
        {
            anchors.AddRange(track.Anchors
                .Where(a => a.Position >= rangeStart && a.Position <= lastOnset));
        }

        if (clearAll || payload.EventPoints.Count > 0)
        {
            events.AddRange(track.EventPoints
                .Where(e => e.Position >= rangeStart && e.Position <= lastOnset));
        }

        if (clearAll || payload.ToneChanges.Count > 0)
        {
            tones.AddRange(track.Tones
                .Where(t => t.Position >= rangeStart && t.Position <= lastOnset));
        }

        return (notes, chords, hs, anchors, events, tones);
    }

    private void ApplyPasteCommit(TrackEditorContext track,
        List<GuitarNote> notesToRemove, List<GuitarNote> notesToAdd,
        List<Chord> chordsToRemove, List<Chord> chordsToAdd,
        List<HandShape> hsToRemove, List<HandShape> hsToAdd,
        List<FretAnchor> anchorsToRemove, List<FretAnchor> anchorsToAdd,
        List<EventPoint> eventsToRemove, List<EventPoint> eventsToAdd,
        List<ToneChange> tonesToRemove, List<ToneChange> tonesToAdd,
        IReadOnlyDictionary<int, ChordTemplate>? incomingTemplates = null)
    {
        var additionalActions = new List<IChartAction>();
        if (eventsToRemove.Count > 0)
            additionalActions.Add(new RemoveEventPointsAction(track.EventPoints, eventsToRemove));
        if (eventsToAdd.Count > 0)
            additionalActions.Add(new AddEventPointsAction(track.EventPoints, eventsToAdd));
        if (tonesToRemove.Count > 0)
            additionalActions.Add(new RemoveToneChangesAction(track.Tones, tonesToRemove));
        if (tonesToAdd.Count > 0)
            additionalActions.Add(new AddToneChangesAction(track.Tones, tonesToAdd));

        _highwayEditingService.CommitHandShapeDragAndDrop(
            notesToRemove, notesToAdd,
            chordsToRemove, chordsToAdd,
            hsToRemove, hsToAdd,
            anchorsToRemove, anchorsToAdd,
            additionalActions,
            incomingTemplates);
    }
}
