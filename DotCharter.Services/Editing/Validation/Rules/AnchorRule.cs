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
                    var msg = type == ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary
                        ? "Anchor sits at a phrase boundary inside a hand shape — adjust by ~1 ms."
                        : "Anchor is placed inside a hand shape — this can break the hand shape rendering.";
                    issues.Add(new ArrangementIssue(type, msg, aMs, Anchor: anchor, HandShape: containing));
                }

                foreach (var endMs in slideEndsMs)
                {
                    if (Math.Abs(aMs - endMs) < 4)
                    {
                        issues.Add(new ArrangementIssue(
                            ArrangementIssueType.AnchorCloseToUnpitchedSlide,
                            "Anchor sits within 4 ms of an unpitched slide's end — move it slightly later.",
                            aMs, Anchor: anchor));
                        break;
                    }
                }
            }
        }
    }
}
