using DotCharter.Localization;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The sentences of the shape editor: what a scope reaches and what Apply will do, in the user's language.</summary>
internal sealed class ShapeEditWords(ILocalizationService loc)
{
    public string Chords(int count) => loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", count);

    /// <summary>"1 chord · bar 12", "4 chords", "14 chords on 3 levels".</summary>
    public string Detail(ShapeEditReach reach, int? bar) =>
        reach.Chords.Count == 0 && reach.HandShapes.Count == 0 ? loc["ShapeEdit_Detail_None"]
        : reach.Levels > 1 ? loc.Format("ShapeEdit_Detail_Levels", Chords(reach.Chords.Count), reach.Levels)
        : bar is { } b && reach.Chords.Count == 1 ? loc.Format("ShapeEdit_Detail_AtBar", Chords(1), b)
        : Chords(reach.Chords.Count);

    /// <summary>"37 chords · 13 hand shapes": what the ticked shapes reach together.</summary>
    public string DetailTogether(ShapeEditReach reach) =>
        reach.Count == 0 ? loc["ShapeEdit_Detail_None"]
        : reach.HandShapes.Count == 0 ? Chords(reach.Chords.Count)
        : loc.Format("ChordShapes_UsesPair", Chords(reach.Chords.Count),
            loc.Plural("ChordShapes_HandShapesOne", "ChordShapes_HandShapes", reach.HandShapes.Count));

    /// <summary>"14 chords change · 6 keep “Am”".</summary>
    public string Preview(ShapeEditPlan plan, string name) =>
        plan.Reach.Chords.Count == 0 ? loc["ShapeEdit_Preview_None"]
        : plan.ChordsKept <= 0 ? loc.Format("ShapeEdit_Preview_All", Chords(plan.Reach.Chords.Count))
        : loc.Format("ShapeEdit_Preview_Rest", Chords(plan.Reach.Chords.Count), plan.ChordsKept, name);

    /// <summary>The one sentence Apply carries out.</summary>
    public string Result(ShapeEditPlan plan, string name, string matchName, int shapes = 0) => plan.Outcome switch
    {
        ShapeEditOutcome.MergesShapes when plan.Match is not null => loc.Format("ShapeEdit_Result_TogetherJoin", shapes, matchName),
        ShapeEditOutcome.MergesShapes when plan.ShapesEmptied == 0 => loc["ShapeEdit_Result_TogetherKept"],
        ShapeEditOutcome.MergesShapes => loc.PluralWith("ShapeEdit_Result_TogetherOne", "ShapeEdit_Result_Together",
            plan.ShapesEmptied, shapes, plan.ShapesEmptied),
        ShapeEditOutcome.ChangesShape => loc.Format("ShapeEdit_Result_Changes", name),
        ShapeEditOutcome.MakesNewShape when plan.Reach.Chords.Count == 1 =>
            loc.Format("ShapeEdit_Result_NewOne", plan.ChordsKept, name),
        ShapeEditOutcome.MakesNewShape =>
            loc.Format("ShapeEdit_Result_NewMany", plan.Reach.Chords.Count, plan.ChordsKept, name),
        ShapeEditOutcome.JoinsExisting => loc.Format("ShapeEdit_Result_Join",
            plan.Reach.Chords.Count == 0 ? plan.Reach.HandShapes.Count.ToString() : Chords(plan.Reach.Chords.Count),
            matchName),
        _ => loc["ShapeEdit_Result_None"]
    };

    public string Apply(ShapeEditScope scope, ShapeEditPlan plan, int shapes = 0) =>
        scope == ShapeEditScope.Together ? loc.Format("ShapeEdit_Apply_Together", shapes, plan.Reach.Chords.Count)
        : scope == ShapeEditScope.EveryUse && plan.TotalChords > 1 ? loc.Format("ShapeEdit_Apply_Every", plan.TotalChords)
        : plan.Reach.Chords.Count <= 1 ? loc["ShapeEdit_Apply_This"]
        : loc.Format("ShapeEdit_Apply_Many", plan.Reach.Chords.Count);

    public string HandShapes(int count, bool follow)
    {
        if (count == 0) return loc["ShapeEdit_Hands_None"];
        return follow
            ? loc.PluralWith("ShapeEdit_Hands_FollowOne", "ShapeEdit_Hands_Follow", count, count)
            : loc.PluralWith("ShapeEdit_Hands_KeepOne", "ShapeEdit_Hands_Keep", count, count);
    }

    public string Match(string name, int uses) =>
        loc.PluralWith("ShapeEdit_MatchOne", "ShapeEdit_Match", uses, name.Length > 0 ? name : "?", uses);
}
