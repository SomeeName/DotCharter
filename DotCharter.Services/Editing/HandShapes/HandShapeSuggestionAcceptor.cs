using DotCharter.Services.State;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
/// Turns accepted <see cref="SuggestedHandShape" />s into real bars: a click on one dashed
///     bar, or "Accept suggested hand shapes in selection". Every gesture is ONE undo node, and it goes
/// down the SAME path as Generate hand shapes (arrangement):
///     <see cref="ArrangementHandShapeGenerator.BuildActions" /> — the minted templates first, then
///     the bars — so one Ctrl+Z takes back the bars and every template they minted
///     (<c>HandShapeTemplateMintTests</c>).
///     <para>
///         <b>Plain by default.</b> The finished charts bar these runs with plain hand shapes, so
///         a plain accept reuses the suggestion's declared template (the grip itself, or the chord
///         shape containing it) or mints the grip through <see cref="ChordTemplateResolver" />.
///         An ARPEGGIO accept resolves that same fret shape in the arpeggio flavour
///         (<see cref="HandShapeFromSelectionGenerator.ResolveArpeggio" />, the Generate arpeggio
///         verb's resolver), never by flagging a shared template.
///     </para>
/// </summary>
public sealed class HandShapeSuggestionAcceptor(IHistoryService history, IAppLogger logger)
{
    /// <summary>The history label of an accept.</summary>
    public const string HistoryLabelKey = "History_AcceptSuggestedHandShapes";

    /// <summary>The <paramref name="suggestions" /> that hold at least one of
    /// <paramref name="positions" /> — what "accept in selection" accepts.</summary>
    public static List<SuggestedHandShape> Holding(
        IReadOnlyList<SuggestedHandShape> suggestions, IEnumerable<FractionalPosition> positions)
    {
        var picked = new List<SuggestedHandShape>();
        var wanted = positions.ToList();
        foreach (var suggestion in suggestions)
        {
            if (wanted.Exists(suggestion.Covers)) picked.Add(suggestion);
        }

        return picked;
    }

    /// <summary>
    ///     The accept as a plan for one level: each suggestion's bar on its template, every template
    ///     a mint (never added to the arrangement here). A run's chords keep their own templates —
    ///     the bar is the grip, the chord is what it strikes.
    /// </summary>
    public static ArrangementHandShapePlan Plan(
        Arrangement arrangement, ChartLevel level, IEnumerable<SuggestedHandShape> picks, bool arpeggio)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(picks);

        var minted = new List<ChordTemplate>();
        var bars = new List<PlannedHandShape>();
        foreach (var pick in picks)
        {
            var frets = pick.Existing?.Frets ?? pick.Frets;
            var template = arpeggio
                ? HandShapeFromSelectionGenerator.ResolveArpeggio(arrangement, frets, minted)
                : pick.Existing ?? ChordTemplateResolver.Resolve(arrangement, frets, ArpeggioFlavour.Plain, null, minted);
            bars.Add(new PlannedHandShape(pick.Position, pick.EndPosition, template, [], []));
        }

        return new ArrangementHandShapePlan(minted, bars.Count == 0 ? [] : [new LevelHandShapePlan(level, bars)]);
    }

    /// <summary>Accepts <paramref name="picks" /> as ONE undo node and returns the number of bars
    /// added (0 when there was nothing to accept or the history refused the step).</summary>
    public int Accept(Arrangement arrangement, ChartLevel level, IReadOnlyList<SuggestedHandShape> picks, bool arpeggio)
    {
        var plan = Plan(arrangement, level, picks, arpeggio);
        if (plan.IsEmpty) return 0;

        var actions = ArrangementHandShapeGenerator.BuildActions(arrangement, plan);
        if (!history.Do(new CompositeAction(actions) { LabelKey = HistoryLabelKey })) return 0;

        logger.Info(AppLogCategory.Editing,
            $"Accepted {plan.HandShapeCount} suggested hand shape(s){(arpeggio ? " as arpeggios" : "")} on " +
            $"'{arrangement.Name}', templates minted={plan.MintedTemplates.Count}.");
        return plan.HandShapeCount;
    }

    /// <summary>The bar a rendered suggestion stands for: its span, its template's id when it reuses
    /// one, and its label — the template's name, else the name the grip would be minted with, else
    /// its frets (a grip the namer leaves unnamed still says what it is).</summary>
    public static HandShape ToPreview(Arrangement arrangement, SuggestedHandShape suggestion)
    {
        var name = suggestion.Existing is { Name.Length: > 0 } existing
            ? existing.Name
            : ChordNameSuggester.SuggestFirstChordName(arrangement, suggestion.Frets);
        if (string.IsNullOrEmpty(name))
        {
            var shape = new ChordTemplate();
            foreach (var (stringIndex, fret) in suggestion.Frets) shape.Frets[stringIndex] = fret;
            name = shape.GetTemplateFrets(arrangement.StringCount > 0 ? arrangement.StringCount : 6);
        }

        return new HandShape
        {
            Position = suggestion.Position,
            EndPosition = suggestion.EndPosition,
            TemplateId = suggestion.Existing?.Id ?? -1,
            Name = name
        };
    }
}
