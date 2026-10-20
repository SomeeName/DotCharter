using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.State;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing;

/// <summary>
///     Splits the hand shape covering a selected sound AT that sound: the
///     shape's tail becomes a second shape starting there, and the first one is
///     trimmed back to meet it.
///     <para>
///     The charting-workflow half of the let-ring
///     re-fret report: a bar drawn across a fret change declares the wrong fret
///     for everything after it (inspector E05), and the fix by hand was
///     delete-and-redraw-two. Select the note where the hand moves and press the
///     key instead.
///     </para>
///     <para>
///     The trimmed shape ends exactly ON the split position and the new one
///     starts there; the GAP between them is not computed here.
///     <c>ChartSpacingEnforcer</c> runs on every committed highway edit and
///     applies the same minimum distance every other shape gets, so hand-rolling
///     one here would be a second, divergent answer to a question the chart model
///     already answers.
///     </para>
/// </summary>
public sealed class HandShapeSplitter
{
    private readonly IFingeringGenerationService _fingeringService;
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly ISelectionService _selectionManager;
    private readonly IAppLogger _logger;

    public HandShapeSplitter(
        IFingeringGenerationService fingeringService,
        IHighwayEditingService highwayEditingService,
        ISelectionService selectionManager,
        IAppLogger logger)
    {
        _fingeringService = fingeringService;
        _highwayEditingService = highwayEditingService;
        _selectionManager = selectionManager;
        _logger = logger;
    }

    /// <summary>One shape's split: where its own end moves to, and the spans that
    /// become new shapes after it.</summary>
    public readonly record struct Split(
        HandShape Shape,
        FractionalPosition TrimmedEnd,
        IReadOnlyList<(FractionalPosition Start, FractionalPosition End)> NewSpans);

    /// <summary>
    ///     Pure: which shapes split, and where. A position only splits a shape it
    ///     falls STRICTLY inside — splitting at a shape's own start or end would
    ///     produce a zero-width shape and change nothing a charter can see, so
    ///     those are no-ops rather than errors. Several positions inside one shape
    ///     cut it into several pieces. Visible for testing.
    /// </summary>
    public static List<Split> ComputeSplits(
        IReadOnlyList<HandShape> shapes, IReadOnlyCollection<FractionalPosition> positions)
    {
        var splits = new List<Split>();
        if (shapes.Count == 0 || positions.Count == 0) return splits;

        foreach (var shape in shapes)
        {
            var inside = positions
                .Where(p => p.CompareTo(shape.Position) > 0 && p.CompareTo(shape.EndPosition) < 0)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            if (inside.Count == 0) continue;

            var spans = new List<(FractionalPosition Start, FractionalPosition End)>(inside.Count);
            for (var i = 0; i < inside.Count; i++)
                spans.Add((inside[i], i + 1 < inside.Count ? inside[i + 1] : shape.EndPosition));

            splits.Add(new Split(shape, inside[0], spans));
        }

        return splits;
    }

    public void SplitAtSelection(TrackEditorContext track)
    {
        var positions = new HashSet<FractionalPosition>();
        foreach (var note in _selectionManager.SelectedNotes) positions.Add(note.Position);
        foreach (var chord in _selectionManager.SelectedChords) positions.Add(chord.Position);

        if (positions.Count == 0)
        {
            _logger.Info(AppLogCategory.Editing, "SplitHandShape skipped: no notes or chords selected.");
            return;
        }

        var splits = ComputeSplits(track.HandShapes, positions);
        if (splits.Count == 0)
        {
            _logger.Info(AppLogCategory.Editing,
                "SplitHandShape skipped: nothing selected sits strictly inside a hand shape.");
            return;
        }

        var newTemplates = new List<ChordTemplate>();
        var replaced = new List<HandShape>();
        var added = new List<HandShape>();
        var nextTemplateId = _fingeringService.NextTemplateId(track.Arrangement);

        foreach (var split in splits)
        {
            replaced.Add(split.Shape);

            // The head keeps the original template: its content did not change,
            // and re-deriving it would silently re-shape a bar the charter did
            // not ask about.
            added.Add(new HandShape
            {
                Position = split.Shape.Position,
                EndPosition = split.TrimmedEnd,
                Name = split.Shape.Name,
                TemplateId = split.Shape.TemplateId
            });

            foreach (var (start, end) in split.NewSpans)
            {
                added.Add(new HandShape
                {
                    Position = start,
                    EndPosition = end,
                    Name = split.Shape.Name,
                    // A new piece describes a NEW hand position — that is the
                    // whole point of splitting — so it gets a template built
                    // from what actually sounds inside it. A span with no
                    // sounds falls back to the original's.
                    TemplateId = ResolveTemplateId(track, start, end, split.Shape.TemplateId,
                        newTemplates, ref nextTemplateId)
                });
            }
        }

        var additional = new List<IChartAction>();
        if (newTemplates.Count > 0)
            additional.Add(new AddChordTemplatesAction(track.Arrangement.ChordTemplates, newTemplates));

        _highwayEditingService.CommitHandShapeDragAndDrop(
            [], [],
            [], [],
            replaced, added,
            [], [],
            additional);

        _logger.Info(AppLogCategory.Editing,
            $"SplitHandShape split {replaced.Count} hand shape(s) into {added.Count} " +
            $"(new templates={newTemplates.Count}) on track '{track.Arrangement.Name}'.");
    }

    private int ResolveTemplateId(TrackEditorContext track, FractionalPosition start, FractionalPosition end,
        int fallbackTemplateId, List<ChordTemplate> newTemplates, ref int nextTemplateId)
    {
        var seed = _fingeringService.BuildTemplateForRange(track.Arrangement, track.Notes, track.Chords, start, end);
        if (seed == null) return fallbackTemplateId;

        // Re-check against templates already queued in this batch so two spans
        // with the same shape share one template (the ChordTemplate rule: shared,
        // deduped by shape, never mutated in place).
        var existing = _fingeringService.FindEquivalentTemplate(track.Arrangement, seed)
                       ?? newTemplates.FirstOrDefault(t => t.Equals(seed));
        if (existing != null) return existing.Id;

        seed.Id = nextTemplateId++;
        newTemplates.Add(seed);
        return seed.Id;
    }
}
