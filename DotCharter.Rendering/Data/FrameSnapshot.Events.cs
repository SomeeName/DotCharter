// FrameSnapshot.Events: events, lyrics, tones, templates, anchors, beats and hand shapes - the windowed projections and the HUD's active tone.
using DotCharter.Rendering.Data.Render;
using DotCharter.Core.Graphics;

namespace DotCharter.Rendering.Data;

public partial class FrameSnapshot
{
    // Cached measure-start count before the visible window (see the beat loop
    // below). Per snapshot instance — the 2D and 3D renderers each own one.
    private object? _measurePrefixBeatsRef;
    private int _measurePrefixBeatCount = -1;
    private int _measurePrefixBeatStart = -1;
    private int _measurePrefixCount;

    // Sticky-prefix cache for event points. Pinned badges (EventLayer.
    // DrawPinnedLabels) and the 3D HUD walk the projected events BACKWARDS
    // for the latest SECTION-carrying and the latest PHRASE-carrying entry —
    // each can sit arbitrarily far before the visible window (sections are
    // sparse; phrases dense). The backward scan early-exits once both are
    // found, but a chart missing one kind entirely would rescan its whole
    // prefix every frame, so results are cached per (collection identity,
    // count, window start) with the same 250 ms staleness bound the ghost
    // buffers use — in-place text edits change neither instance nor count.
    private const long StickyRefreshIntervalMs = 250;

    // Staleness bound for the UNVERSIONED chord-template latch (see
    // UpdateTemplates below) — same reasoning, same quarter second: an
    // in-place template edit changes neither the list instance nor its count.
    private const long TemplateRefreshIntervalMs = 250;
    private object? _stickyEventsRef;
    private int _stickyEventsCount;
    private int _stickyEventsWindowStart = -1;
    private long _stickyEventsTick;
    private int _stickySectionIdx = -1;
    private int _stickyPhraseIdx = -1;

    // The 3D HUD previews the next lyric line up to 5 s past the playhead
    // (HudComponent.RenderLyrics: StartTime <= TimeMs+5000),
    // which reaches beyond the 3D camera window (+4 s look-ahead) — the lyric
    // window is extended to cover it.
    private const double LyricLookaheadMs = 5000.0;

    private void UpdateEventsAndLyrics(SnapshotUpdateContext ctx)
    {
        var eventPoints = ctx.EventPoints;
        var previewEvents = ctx.PreviewEventPoints;
        var lyricPhrases = ctx.LyricPhrases;
        var tones = ctx.Tones;
        var beats = ctx.ActiveBeats;

        // Events/tones/lyrics are projected from the visible window, not the FULL collection per frame —
        // a full projection would be an O(total chart) rebuild. GetVisibleRange's window
        // already includes the nearest item at-or-before the buffered left
        // edge, which is exactly the one-item lookback the pinned badge /
        // active-tone walks need for everything except the section/phrase
        // stickies handled below.
        TimelineMath.GetVisibleRange(eventPoints, ctx.ViewStartMs, ctx.ViewEndMs, beats,
            ctx.VisibleRangeBufferMs, out var epStart, out var epEnd);

        var now = Environment.TickCount64;
        if (!ReferenceEquals(eventPoints, _stickyEventsRef) ||
            eventPoints.Count != _stickyEventsCount ||
            epStart != _stickyEventsWindowStart ||
            now - _stickyEventsTick >= StickyRefreshIntervalMs)
        {
            _stickySectionIdx = -1;
            _stickyPhraseIdx = -1;
            for (var i = epStart - 1; i >= 0 && (_stickySectionIdx < 0 || _stickyPhraseIdx < 0); i--)
            {
                var ep = eventPoints[i];
                if (_stickySectionIdx < 0 && !string.IsNullOrEmpty(ep.Section)) _stickySectionIdx = i;
                if (_stickyPhraseIdx < 0 && !string.IsNullOrEmpty(ep.Phrase)) _stickyPhraseIdx = i;
            }

            _stickyEventsRef = eventPoints;
            _stickyEventsCount = eventPoints.Count;
            _stickyEventsWindowStart = epStart;
            _stickyEventsTick = now;
        }

        // Prepend the sticky entries in index (= time) order, deduplicated,
        // then the windowed range: backward walks over the projected array
        // then land on exactly the items the full projection produced.
        var stickyLo = Math.Min(_stickySectionIdx, _stickyPhraseIdx);
        var stickyHi = Math.Max(_stickySectionIdx, _stickyPhraseIdx);
        var epWindowCount = epEnd >= epStart && epEnd >= 0 ? epEnd - epStart + 1 : 0;
        EnsureCapacity(ref EventPoints, epWindowCount + 2);
        EventPointCount = 0;
        if (stickyLo >= 0 && stickyLo != stickyHi) ProjectEventPoint(eventPoints, stickyLo, ctx, beats);
        if (stickyHi >= 0) ProjectEventPoint(eventPoints, stickyHi, ctx, beats);
        for (var i = epStart; i <= epEnd; i++) ProjectEventPoint(eventPoints, i, ctx, beats);

        // Preview events are in-flight edit artifacts — a handful at most,
        // and not guaranteed sorted. Keep the full projection.
        PreviewEventPointCount = previewEvents.Count;
        EnsureCapacity(ref PreviewEventPoints, PreviewEventPointCount);
        for (var i = 0; i < PreviewEventPointCount; i++)
        {
            PreviewEventPoints[i] = new RenderEventPoint
            {
                Time = GetCachedPositionMs(previewEvents[i].Position, beats),
                Phrase = previewEvents[i].Phrase,
                Section = previewEvents[i].Section,
                IsSelected = false,
                IsHovered = false
            };
        }

        UpdateProposedEventPoints(ctx, beats);

        // Tones need no extra sticky handling: the pinned tone badge and the
        // HUD's active tone want the latest tone before the viewport / the
        // playhead, and the window's inclusive left edge is exactly that item.
        TimelineMath.GetVisibleRange(tones, ctx.ViewStartMs, ctx.ViewEndMs, beats,
            ctx.VisibleRangeBufferMs, out var tStart, out var tEnd);
        ToneCount = tEnd >= tStart && tEnd >= 0 ? tEnd - tStart + 1 : 0;
        EnsureCapacity(ref Tones, ToneCount);
        for (var i = 0; i < ToneCount; i++)
        {
            var t = tones[tStart + i];
            Tones[i] = new RenderToneChange
            {
                Time = GetCachedPositionMs(t.Position, beats),
                ToneName = t.ToneName,
                IsSelected = _selTonesCache.Contains(t),
                IsHovered = ctx.HoveredTone == t
            };
        }

        ResolveActiveTone(ctx);

        // Lyric phrases window by START time; the inclusive left edge keeps
        // the phrase still running across the window start. One extra phrase
        // of lookback absorbs mild overlap the same way AnchorStart-1 does.
        var lyricViewEnd = Math.Max(ctx.ViewEndMs, ctx.CurrentTimeMs + LyricLookaheadMs);
        TimelineMath.GetVisibleRange(lyricPhrases, ctx.ViewStartMs, lyricViewEnd, beats,
            ctx.VisibleRangeBufferMs, out var lpStart, out var lpEnd);
        lpStart = Math.Max(0, lpStart - 1);

        LyricPhraseCount = lpEnd >= lpStart && lpEnd >= 0 ? lpEnd - lpStart + 1 : 0;
        EnsureCapacity(ref LyricPhrases, LyricPhraseCount);
        var totalWords = 0;
        // Indexed — a foreach through the IList interface boxes one enumerator
        // per snapshot per frame.
        for (var i = 0; i < LyricPhraseCount; i++) totalWords += lyricPhrases[lpStart + i].Lyrics.Count;
        EnsureCapacity(ref LyricWords, totalWords);
        LyricWordCount = 0;
        for (var i = 0; i < LyricPhraseCount; i++)
        {
            var lp = lyricPhrases[lpStart + i];
            var wordOffset = LyricWordCount;
            var wordCount = lp.Lyrics.Count;
            for (var j = 0; j < wordCount; j++)
            {
                LyricWords[LyricWordCount++] = new RenderLyricWord
                {
                    Text = lp.Lyrics[j].Text,
                    Time = GetCachedPositionMs(lp.Lyrics[j].Position, beats),
                    EndTime = GetCachedPositionMs(lp.Lyrics[j].EndPosition, beats),
                    IsWordPart = lp.Lyrics[j].IsWordPart,
                    IsHovered = ReferenceEquals(ctx.HoveredLyric, lp.Lyrics[j]),
                    IsSelected = ReferenceEquals(ctx.SelectedLyric, lp.Lyrics[j])
                };
            }
            // The phrase's rendered bounds are its SYLLABLES' bounds — first
            // start to last end. The container's own Position/EndPosition are
            // stored fields nothing updates when auto-sync or a drag moves the
            // syllables, so brackets drawn from them detach from the pills.
            // Syllables are kept ascending
            // (the drag clamp forbids crossing), so first/last are the
            // extremes; the container bounds remain the fallback for a phrase
            // with no syllables.
            LyricPhrases[i] = new RenderLyricPhrase
            {
                StartTime = wordCount > 0
                    ? LyricWords[wordOffset].Time
                    : GetCachedPositionMs(lp.Position, beats),
                EndTime = wordCount > 0
                    ? LyricWords[LyricWordCount - 1].EndTime
                    : GetCachedPositionMs(lp.EndPosition, beats),
                WordOffset = wordOffset, WordCount = wordCount
            };
        }
    }

    // Resolve the single tone name the HUD shows this frame. Priority:
    //   1. HUD disabled (config toggle) ⇒ nothing.
    //   2. The rig-applied preset, when the in-game-tone coordinator is driving
    //      (GameplayToneHud.Rig set) — this is the tone actually on the rig.
    //   3. The arrangement's own tone: the latest ToneChange at/before the
    //      playhead, else the base/starting tone.
    // The change tick is bumped whenever the resolved name changes, so the HUD
    // can flash a brief emphasis on a switch.
    private void ResolveActiveTone(SnapshotUpdateContext ctx)
    {
        var name = "";
        var approx = false;
        var fromRig = false;

        if (GameplayToneHud.Enabled)
        {
            if (GameplayToneHud.Rig is { } rig)
            {
                name = rig.Name;
                approx = rig.Approximated;
                fromRig = true;
            }
            else
            {
                name = ResolveArrangementTone(ctx);
            }
        }

        ActiveToneName = name;
        ActiveToneApproximated = approx;
        ActiveToneFromRig = fromRig;
        if (!string.Equals(name, _activeTonePrev, StringComparison.Ordinal))
        {
            ActiveToneChangedTick = Environment.TickCount64;
            _activeTonePrev = name;
        }
    }

    // The chart's own current tone: the latest tone change at or before the
    // playhead wins; before the first change (or a song with none) the base
    // tone plays. ctx.Tones is the FULL, possibly-unsorted change list, so this
    // takes the max-time entry ≤ playhead rather than trusting order.
    private string ResolveArrangementTone(SnapshotUpdateContext ctx)
    {
        var tones = ctx.Tones;
        var beats = ctx.ActiveBeats;
        var latest = "";
        var latestTime = double.NegativeInfinity;
        for (var i = 0; i < tones.Count; i++)
        {
            var t = tones[i];
            var timeMs = GetCachedPositionMs(t.Position, beats);
            if (timeMs <= ctx.CurrentTimeMs && timeMs >= latestTime)
            {
                latestTime = timeMs;
                latest = t.ToneName;
            }
        }

        if (!string.IsNullOrEmpty(latest)) return latest;

        var arr = ctx.Arrangement;
        if (arr is null) return "";
        if (!string.IsNullOrWhiteSpace(arr.StartingTone)) return arr.StartingTone;
        return arr.ToneBase ?? "";
    }

    private void ProjectEventPoint(IList<EventPoint> src, int i, SnapshotUpdateContext ctx,
        IList<BeatMarker> beats)
    {
        var ep = src[i];
        EventPoints[EventPointCount++] = new RenderEventPoint
        {
            Time = GetCachedPositionMs(ep.Position, beats),
            Phrase = ep.Phrase,
            Section = ep.Section,
            IsSelected = _selEventsCache.Contains(ep),
            IsHovered = ctx.HoveredEventPoint == ep,
            SectionIsEditTarget = ctx.EditTargetSections.Count > 0 && ctx.EditTargetSections.Contains(ep),
            PhraseIsEditTarget = ctx.EditTargetPhrases.Count > 0 && ctx.EditTargetPhrases.Contains(ep)
        };
    }

    /// <summary>The markers a pending change would add that fall in the buffered view.
    /// A handful at most, sorted by position; nothing is allocated once the array has grown.</summary>
    private void UpdateProposedEventPoints(SnapshotUpdateContext ctx, IList<BeatMarker> beats)
    {
        var proposed = ctx.ProposedEventPoints;
        ProposedEventPointCount = 0;
        if (proposed.Count == 0) return;
        EnsureCapacity(ref ProposedEventPoints, proposed.Count);
        var fromMs = ctx.ViewStartMs - ctx.VisibleRangeBufferMs;
        var toMs = ctx.ViewEndMs + ctx.VisibleRangeBufferMs;
        for (var i = 0; i < proposed.Count; i++)
        {
            var ep = proposed[i];
            var timeMs = GetCachedPositionMs(ep.Position, beats);
            if (timeMs < fromMs || timeMs > toMs) continue;
            ProposedEventPoints[ProposedEventPointCount++] = new RenderEventPoint
            {
                Time = timeMs,
                Phrase = ep.Phrase,
                Section = ep.Section,
                SectionIsEditTarget = ep.Section.Length > 0,
                PhraseIsEditTarget = ep.Phrase.Length > 0
            };
        }
    }

    // --- chord-template projection latch ---------------------------------
    // The table must not be re-projected EVERY frame on BOTH renderers — per
    // template a struct build plus 8x GetFret + 8x GetFinger DICTIONARY
    // lookups — while templates only ever change on a chart edit.
    //
    // Identity + Count alone is NOT a sound latch here, because the editing
    // verbs mutate templates IN PLACE without replacing the list or changing
    // its length: FingeringGenerationService writes template.Frets/.Fingers,
    // ArrangementAutoFixService clamps template.Frets[s] up to the capo,
    // ChartSpacingEnforcer and DoubleStopChordNames assign template.Name,
    // RefretService replaces template.Fingers wholesale. So:
    //   * a VERSIONED context (SnapshotUpdateContext.TemplatesVersion >= 0)
    //     latches EXACTLY — the caller promises a bump accompanies every
    //     template change, the same contract SelectionVersion carries;
    //   * an UNVERSIONED context (the default -1, what every caller passes
    //     today) latches on identity + count under the same 250 ms staleness
    //     bound the sticky-event cache above uses for exactly this reason.
    //     60 Hz -> 4 Hz, and an in-place edit still lands within a quarter
    //     second.
    // Templates is id-indexed, so a skipped rebuild leaves it byte-identical.
    private object? _templatesRef;
    private int _templatesCount = -1;
    private long _templatesVersion = -1;
    private long _templatesTick;

    /// <summary>
    ///     Test seam: monotonic millisecond source for the unversioned template
    ///     latch's staleness bound. Production leaves this null and the latch
    ///     reads <see cref="Environment.TickCount64" />.
    /// </summary>
    internal Func<long>? TemplateLatchClock;

    // Deliberately NOT windowed: templates are a lookup table, not timeline
    // content — HighwayRenderComponent/ChordRenderer index Templates[TemplateId]
    // directly, so array position must stay the template id. Count scales with
    // unique chord shapes (bounded, ~100s), not with song length.
    private unsafe void UpdateTemplates(SnapshotUpdateContext ctx)
    {
        var activeTemplates = ctx.ActiveTemplates;
        var version = ctx.TemplatesVersion;
        var now = TemplateLatchClock?.Invoke() ?? Environment.TickCount64;
        var sameTable = ReferenceEquals(activeTemplates, _templatesRef)
                        && activeTemplates.Count == _templatesCount
                        && version == _templatesVersion;
        if (sameTable && (version >= 0 || now - _templatesTick < TemplateRefreshIntervalMs))
            return;

        _templatesRef = activeTemplates;
        _templatesCount = activeTemplates.Count;
        _templatesVersion = version;
        _templatesTick = now;

        TemplateCount = activeTemplates.Count;
        EnsureCapacity(ref Templates, TemplateCount);
        for (var i = 0; i < TemplateCount; i++)
        {
            var t = activeTemplates[i];
            // ChordTemplate.IsArpeggio, never `t.Arpeggio`: two flags spell
            // arpeggio and the import paths set different ones. Every renderer
            // downstream reads a FOLDED answer — this `RenderChordTemplate.Arpeggio`
            // (3D brackets) or `RenderHandShape.IsArpeggio` (the 2D bar), both
            // projected from IsArpeggio in this file.
            var rct = new RenderChordTemplate
            { Id = t.Id, Name = t.Name, Arpeggio = t.IsArpeggio };
            for (var s = 0; s < 8; s++)
            {
                rct.Frets[s] = (sbyte)t.GetFret(s);
                rct.Fingers[s] = (sbyte)t.GetFinger(s);
            }

            Templates[i] = rct;
        }
    }

    /// <summary>
    ///     The template whose <c>Id</c> is <paramref name="templateId" />, or null for
    ///     the <c>-1</c> sentinel and an id no template carries. Keyed on the ID, as
    ///     the validator's <c>HandShapeTemplate</c> lookup is, so a bar's name and its
    ///     D09 finding agree; the position is tried first because ids are
    ///     contiguous in practice, so the scan only runs for a chart whose ids are
    ///     not. Allocation-free: indexed, no enumerator.
    /// </summary>
    internal static ChordTemplate? FindTemplate(IList<ChordTemplate> templates, int templateId)
    {
        if (templateId < 0) return null;
        if (templateId < templates.Count && templates[templateId].Id == templateId) return templates[templateId];
        for (var i = 0; i < templates.Count; i++)
        {
            if (templates[i].Id == templateId)
                return templates[i];
        }

        return null;
    }

    private void UpdateCoreTimeline(SnapshotUpdateContext ctx)
    {
        AnchorCount = ctx.AnchorEnd >= ctx.AnchorStart && ctx.AnchorEnd >= 0 ? ctx.AnchorEnd - ctx.AnchorStart + 1 : 0;
        EnsureCapacity(ref Anchors, AnchorCount);
        for (var i = 0; i < AnchorCount; i++)
        {
            var a = ctx.ActiveAnchors[ctx.AnchorStart + i];
            var timeMs = GetCachedPositionMs(a.Position, ctx.ActiveBeats);

            var nextA = ctx.AnchorStart + i + 1 < ctx.ActiveAnchors.Count
                ? ctx.ActiveAnchors[ctx.AnchorStart + i + 1]
                : null;
            var endMs = nextA != null
                ? GetCachedPositionMs(nextA.Position, ctx.ActiveBeats)
                : Math.Max(ctx.CurrentTimeMs + HighwaySpeedPolicy.MaxHorizonMs, timeMs + RenderingConstants.AnchorTailMaxMs);

            Anchors[i] = new RenderAnchor
            {
                Time = timeMs,
                EndTime = endMs,
                Fret = (byte)a.Fret,
                Width = (byte)a.Width,
                IsSelected = _selAnchorsCache.Contains(a),
                IsHovered = ctx.HoveredAnchor == a,
                IsEditTarget = ctx.EditTargetAnchors.Count > 0 && ctx.EditTargetAnchors.Contains(a)
            };
        }

        UpdateProposedAnchors(ctx);

        BeatCount = ctx.BeatEnd >= ctx.BeatStart && ctx.BeatEnd >= 0 ? ctx.BeatEnd - ctx.BeatStart + 1 : 0;
        EnsureCapacity(ref Beats, BeatCount);

        // Measure numbers need the count of measure-starts BEFORE the window.
        // Walking 0..BeatEnd every frame was O(all beats before the viewport)
        // per frame — at the end of a long song, the whole grid, every frame.
        // The prefix only changes when the window start moves or the beats
        // collection changes (edits/undo swap the instance or its count), so
        // recount only then; ordinary frames reuse the cached prefix.
        var beatsList = ctx.ActiveBeats;
        if (!ReferenceEquals(beatsList, _measurePrefixBeatsRef) ||
            beatsList.Count != _measurePrefixBeatCount ||
            ctx.BeatStart != _measurePrefixBeatStart)
        {
            var prefix = 0;
            for (var i = 0; i < ctx.BeatStart; i++)
            {
                if (beatsList[i].IsMeasure)
                    prefix++;
            }

            _measurePrefixBeatsRef = beatsList;
            _measurePrefixBeatCount = beatsList.Count;
            _measurePrefixBeatStart = ctx.BeatStart;
            _measurePrefixCount = prefix;
        }

        var measureCounter = _measurePrefixCount;
        for (var i = ctx.BeatStart; i <= ctx.BeatEnd; i++)
        {
            var b = beatsList[i];
            if (b.IsMeasure) measureCounter++;

            Beats[i - ctx.BeatStart] = new RenderBeat
            {
                Time = b.Time, IsMeasure = b.IsMeasure, IsAnchor = b.IsAnchor,
                Bpm = b.Bpm, BeatsInMeasure = b.BeatsInMeasure, NoteDenominator = b.NoteDenominator,
                MeasureNumber = measureCounter
            };
        }

        HandShapeCount = ctx.HandShapeEnd >= ctx.HandShapeStart && ctx.HandShapeEnd >= 0
            ? ctx.HandShapeEnd - ctx.HandShapeStart + 1
            : 0;
        EnsureCapacity(ref HandShapes, HandShapeCount);
        for (var i = 0; i < HandShapeCount; i++)
        {
            var hs = ctx.ActiveHandShapes[ctx.HandShapeStart + i];
            // A committed bar prints its TEMPLATE's name (HandShape.Name is the
            // preview gesture's scratch label and goes stale on a committed bar).
            var template = FindTemplate(ctx.ActiveTemplates, hs.TemplateId);
            HandShapes[i] = new RenderHandShape
            {
                Time = GetCachedPositionMs(hs.Position, ctx.ActiveBeats),
                EndTime = GetCachedPositionMs(hs.EndPosition, ctx.ActiveBeats),
                TemplateId = hs.TemplateId,
                Label = template?.Name,
                IsArpeggio = template?.IsArpeggio ?? false,
                IsSelected = _selHandShapesCache.Contains(hs),
                IsHovered = ctx.HoveredHandShape == hs,
                HasFinding = ctx.HandShapeFindings.Contains(hs),
                IsEditTarget = ctx.EditTargetHandShapes.Contains(hs)
            };
            if (template is { GhostMask: not 0 }) FillGhostGems(ref HandShapes[i], template, hs.Position, ctx);
        }
    }

    /// <summary>
    ///     The bar's ghost gems: each ghost string's held fret, and which of them a played
    ///     gem also sits on at the bar's start (binary search over the start-sorted sounds — a
    ///     ghost bar is rare, a level is not small).
    /// </summary>
    private static void FillGhostGems(ref RenderHandShape bar, ChordTemplate template, FractionalPosition start,
        SnapshotUpdateContext ctx)
    {
        for (var s = 0; s < RenderHandShape.MaxGhostStrings; s++)
        {
            if (!template.IsGhost(s)) continue;
            bar.GhostMask |= 1 << s;
            bar.GhostFrets |= (ulong)(byte)Math.Clamp(template.Frets[s], 0, 255) << (s * 8);
        }

        var notes = ctx.ActiveNotes;
        for (var i = FirstAtOrAfter(notes, start); i < notes.Count && notes[i].Position == start; i++)
        {
            if ((bar.GhostMask & (1 << Math.Clamp(notes[i].StringIndex, 0, 30))) != 0)
                bar.GhostNudgeMask |= 1 << notes[i].StringIndex;
        }

        var chords = ctx.ActiveChords;
        for (var i = FirstAtOrAfter(chords, start); i < chords.Count && chords[i].Position == start; i++)
        {
            var chordNotes = chords[i].ChordNotes;
            for (var n = 0; n < chordNotes.Count; n++)
            {
                if ((bar.GhostMask & (1 << Math.Clamp(chordNotes[n].StringIndex, 0, 30))) != 0)
                    bar.GhostNudgeMask |= 1 << chordNotes[n].StringIndex;
            }
        }
    }

    private static int FirstAtOrAfter(IList<GuitarNote> sorted, FractionalPosition position)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].Position < position) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static int FirstAtOrAfter(IList<Chord> sorted, FractionalPosition position)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].Position < position) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>The suggested bars that reach into the view. The list is sorted by start,
    /// so the walk stops at the first one starting past the window.</summary>
    private void UpdateSuggestedHandShapes(SnapshotUpdateContext ctx)
    {
        var beats = ctx.ActiveBeats;
        var suggested = ctx.SuggestedHandShapes;
        var from = ctx.ViewStartMs - ctx.VisibleRangeBufferMs;
        var to = ctx.ViewEndMs + ctx.VisibleRangeBufferMs;
        EnsureCapacity(ref SuggestedHandShapes, suggested.Count);
        SuggestedHandShapeCount = 0;
        for (var i = 0; i < suggested.Count; i++)
        {
            var hs = suggested[i];
            var start = GetCachedPositionMs(hs.Position, beats);
            if (start > to) break;
            var end = GetCachedPositionMs(hs.EndPosition, beats);
            if (end < from) continue;
            SuggestedHandShapes[SuggestedHandShapeCount++] = new RenderHandShape
            {
                Time = start,
                EndTime = end,
                TemplateId = hs.TemplateId,
                Label = hs.Name,
                IsSuggested = true
            };
        }
    }

    /// <summary>
    ///     The anchors a pending change would add, windowed like the live ones: each
    ///     spans to the next proposed anchor (or, for the last, the anchor tail) and is kept
    ///     when that span reaches the view. Indexed over a position-sorted list; nothing is
    ///     allocated once the array has grown.
    /// </summary>
    private void UpdateProposedAnchors(SnapshotUpdateContext ctx)
    {
        var proposed = ctx.ProposedAnchors;
        ProposedAnchorCount = 0;
        if (proposed.Count == 0) return;
        EnsureCapacity(ref ProposedAnchors, proposed.Count);
        var fromMs = ctx.ViewStartMs - ctx.VisibleRangeBufferMs;
        var toMs = ctx.ViewEndMs + ctx.VisibleRangeBufferMs;
        for (var i = 0; i < proposed.Count; i++)
        {
            var a = proposed[i];
            var timeMs = GetCachedPositionMs(a.Position, ctx.ActiveBeats);
            if (timeMs > toMs) break;
            var endMs = i + 1 < proposed.Count
                ? GetCachedPositionMs(proposed[i + 1].Position, ctx.ActiveBeats)
                : timeMs + RenderingConstants.AnchorTailMaxMs;
            if (endMs < fromMs) continue;
            ProposedAnchors[ProposedAnchorCount++] = new RenderAnchor
            {
                Time = timeMs,
                EndTime = endMs,
                Fret = (byte)a.Fret,
                Width = (byte)a.Width,
                IsEditTarget = true
            };
        }
    }

    private void UpdatePreviewHandShapesAndAnchors(SnapshotUpdateContext ctx)
    {
        var beats = ctx.ActiveBeats;

        PreviewHandShapeCount = ctx.PreviewHandShapes.Count;
        EnsureCapacity(ref PreviewHandShapes, PreviewHandShapeCount);
        for (var i = 0; i < PreviewHandShapeCount; i++)
        {
            var hs = ctx.PreviewHandShapes[i];
            PreviewHandShapes[i] = new RenderHandShape
            {
                Time = GetCachedPositionMs(hs.Position, beats),
                EndTime = GetCachedPositionMs(hs.EndPosition, beats),
                TemplateId = hs.TemplateId,
                IsSelected = false,
                IsHovered = false,
                Label = hs.Name,
                IsArpeggio = FindTemplate(ctx.ActiveTemplates, hs.TemplateId)?.IsArpeggio ?? false
            };
        }

        PreviewAnchorCount = ctx.PreviewAnchors.Count;
        EnsureCapacity(ref PreviewAnchors, PreviewAnchorCount);
        for (var i = 0; i < PreviewAnchorCount; i++)
        {
            var a = ctx.PreviewAnchors[i];
            var timeMs = GetCachedPositionMs(a.Position, beats);

            var endMs = Math.Max(ctx.CurrentTimeMs + HighwaySpeedPolicy.MaxHorizonMs,
                timeMs + RenderingConstants.AnchorTailMaxMs);
            // Indexed: ActiveAnchors is an IList<T> over an ObservableCollection,
            // so a foreach boxes an enumerator — here once per PREVIEW ANCHOR per
            // frame, and this runs on the 3D gameplay path too, not just the 2D
            // timeline.
            var activeAnchors = ctx.ActiveAnchors;
            for (var j = 0; j < activeAnchors.Count; j++)
            {
                var realA = activeAnchors[j];
                if (realA.Position > a.Position)
                {
                    endMs = GetCachedPositionMs(realA.Position, beats);
                    break;
                }
            }

            PreviewAnchors[i] = new RenderAnchor
            {
                Time = timeMs,
                EndTime = endMs,
                Fret = (byte)a.Fret,
                Width = (byte)a.Width,
                IsSelected = false,
                IsHovered = false
            };
        }
    }

    // Sticky-prefix cache for showlights, same idea as the event one above:
    // ShowlightsRenderComponent keeps the LAST fog/beam/laser entry at or
    // before the playhead — stage-light state is set once and held for
    // minutes, so the setting entry usually sits far before the visible
    // window. The latest pre-window entry PER LANE is prepended to the
    // windowed projection; replaying the (windowed) array then lands on the
    // identical per-lane state as replaying the whole chart did.
    private object? _stickyShowlightsRef;
    private int _stickyShowlightsCount;
    private int _stickyShowlightsWindowStart = -1;
    private long _stickyShowlightsTick;
    private int _stickyFogIdx = -1;
    private int _stickyBeamIdx = -1;
    private int _stickyLaserIdx = -1;

    private void UpdateShowlights(SnapshotUpdateContext ctx)
    {
        var beats = ctx.ActiveBeats;
        var src = ctx.Showlights;

        TimelineMath.GetVisibleRange(src, ctx.ViewStartMs, ctx.ViewEndMs, beats,
            ctx.VisibleRangeBufferMs, out var slStart, out var slEnd);

        var now = Environment.TickCount64;
        if (!ReferenceEquals(src, _stickyShowlightsRef) ||
            src.Count != _stickyShowlightsCount ||
            slStart != _stickyShowlightsWindowStart ||
            now - _stickyShowlightsTick >= StickyRefreshIntervalMs)
        {
            _stickyFogIdx = -1;
            _stickyBeamIdx = -1;
            _stickyLaserIdx = -1;
            for (var i = slStart - 1;
                 i >= 0 && (_stickyFogIdx < 0 || _stickyBeamIdx < 0 || _stickyLaserIdx < 0);
                 i--)
            {
                // Lane resolution mirrors the projection below exactly:
                // Fog/Beams map 1:1, everything else lands on Lasers.
                ShowlightCatalog.TryGetDefinition(src[i].Type, src[i].Note, out var def);
                switch (def.Lane)
                {
                    case ShowlightLane.Fog:
                        if (_stickyFogIdx < 0) _stickyFogIdx = i;
                        break;
                    case ShowlightLane.Beams:
                        if (_stickyBeamIdx < 0) _stickyBeamIdx = i;
                        break;
                    default:
                        if (_stickyLaserIdx < 0) _stickyLaserIdx = i;
                        break;
                }
            }

            _stickyShowlightsRef = src;
            _stickyShowlightsCount = src.Count;
            _stickyShowlightsWindowStart = slStart;
            _stickyShowlightsTick = now;
        }

        // Prepend the (distinct) sticky entries in ascending index order so
        // the projected array stays time-sorted, then the windowed range.
        var a = _stickyFogIdx;
        var b = _stickyBeamIdx;
        var c = _stickyLaserIdx;
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);

        var slWindowCount = slEnd >= slStart && slEnd >= 0 ? slEnd - slStart + 1 : 0;
        EnsureCapacity(ref Showlights, slWindowCount + 3);
        ShowlightCount = 0;
        if (a >= 0) ProjectShowlight(src, a, beats);
        if (b >= 0) ProjectShowlight(src, b, beats);
        if (c >= 0) ProjectShowlight(src, c, beats);
        for (var i = slStart; i <= slEnd; i++) ProjectShowlight(src, i, beats);

        UpdateShowlightChip(ctx.ShowlightChipLabels);
    }

    // The editor chip's text is rebuilt only when the state at the playhead or
    // the labels change, so an ordinary frame allocates nothing.
    private ShowlightChipLabels? _chipLabels;
    private int _chipFog = int.MinValue, _chipBeam = int.MinValue, _chipLaser = int.MinValue;

    private void UpdateShowlightChip(ShowlightChipLabels? labels)
    {
        if (labels is null)
        {
            ShowlightChipText = string.Empty;
            _chipLabels = null;
            return;
        }

        var fog = StageLights.ResolveLane(Showlights, ShowlightCount, RenderShowlightLane.Fog, CurrentTimeMs).Current;
        var beam = StageLights.ResolveLane(Showlights, ShowlightCount, RenderShowlightLane.Beams, CurrentTimeMs).Current;
        var laser = StageLights.ResolveLane(Showlights, ShowlightCount, RenderShowlightLane.Lasers, CurrentTimeMs).Current;
        var f = fog?.Note ?? -1;
        var b = beam?.Note ?? -1;
        var l = laser?.Note ?? -1;
        if (ReferenceEquals(labels, _chipLabels) && f == _chipFog && b == _chipBeam && l == _chipLaser) return;

        _chipLabels = labels;
        _chipFog = f;
        _chipBeam = b;
        _chipLaser = l;
        ShowlightChipText = labels.Compose(fog?.Note, beam?.Note, laser?.Note);
    }

    private void ProjectShowlight(IList<Showlight> src, int i, IList<BeatMarker> beats)
    {
        var sl = src[i];
        ShowlightCatalog.TryGetDefinition(sl.Type, sl.Note, out var def);
        var lane = def.Lane;
        var note = sl.Note != 0 ? sl.Note : def.Note;
        var color = ShowlightPalette.ColorFromNote(note);

        Showlights[ShowlightCount++] = new RenderShowlight
        {
            Time = GetCachedPositionMs(sl.Position, beats),
            Note = note,
            Lane = lane switch
            {
                ShowlightLane.Fog => RenderShowlightLane.Fog,
                ShowlightLane.Beams => RenderShowlightLane.Beams,
                _ => RenderShowlightLane.Lasers
            },
            R = color.R,
            G = color.G,
            B = color.B
        };
    }
}
