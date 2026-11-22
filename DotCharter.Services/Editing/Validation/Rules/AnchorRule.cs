namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Anchor-placement checks: anchor inside hand shape (with separate issue
///     type when also on a phrase boundary) and anchor too close to the end of
///     an unpitched slide. Mover-phrase anchors are skipped — they are allowed
///     by the in-game engine.
/// </summary>
public sealed class AnchorRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;

        foreach (var level in context.Arrangement.Levels)
        {
            if (level.Anchors.Count == 0) continue;

            var slideEndsMs = new List<double>();
            foreach (var n in level.Notes)
            {
                if (n.UnpitchedSlide && n.Sustain != FractionalPosition.Zero)
                    slideEndsMs.Add(n.EndPosition.GetPositionMs(beats));
            }

            foreach (var anchor in level.Anchors)
            {
                var aMs = anchor.Position.GetPositionMs(beats);
                if (context.IsOnMoverPhraseBoundary(aMs)) continue;

                HandShape? containing = null;
                foreach (var hs in level.HandShapes)
                {
                    if (anchor.Position > hs.Position && anchor.Position < hs.EndPosition)
                    {
                        containing = hs;
                        break;
                    }
                }

                if (containing != null)
                {
                    var type = context.IsOnPhraseBoundary(aMs)
                        ? ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary
                        : ArrangementIssueType.AnchorInsideHandShape;
                    var key = type == ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary
                        ? "Validation_AnchorInsideHandShapeAtPhraseBoundary"
                        : "Validation_AnchorInsideHandShape";
                    issues.Add(ArrangementIssues.Make(type, key, aMs, anchor: anchor, handShape: containing));
                }

                foreach (var endMs in slideEndsMs)
                {
                    if (Math.Abs(aMs - endMs) < 4)
                    {
                        issues.Add(ArrangementIssues.Make(ArrangementIssueType.AnchorCloseToUnpitchedSlide,
                            "Validation_AnchorCloseToUnpitchedSlide", aMs, anchor: anchor));
                        break;
                    }
                }
            }
        }
    }
}
