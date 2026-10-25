using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>One row of the "Apply to" ladder: its words, its live count, and
/// whether it is the rung the next edit takes.</summary>
public sealed partial class ChordApplyRungRow(ChordApplyRung rung) : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>The live count: "12 chords · 4 hand shapes · all levels".</summary>
    [ObservableProperty] private string _detail = string.Empty;

    [ObservableProperty] private bool _isAvailable;

    /// <summary>Lit when the next edit takes this rung — the chosen one, or the
    /// one an unavailable choice falls through to.</summary>
    [ObservableProperty] private bool _isEffective;

    [ObservableProperty] private string _tip = string.Empty;

    [ObservableProperty] private string _accessibleName = string.Empty;

    public ChordApplyRung Rung { get; } = rung;

    /// <summary>The counts behind <see cref="Detail" />.</summary>
    public ChordApplyRungReach Reach { get; internal set; }

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The count over the reason: the compact scope bar prints only the lit
    /// rung's count, so every other rung says its reach on hover.</summary>
    public string ToolTipText => Detail.Length > 0 ? $"{Detail}\n{Tip}" : Tip;

    partial void OnDetailChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ToolTipText));
    }

    partial void OnTipChanged(string value) => OnPropertyChanged(nameof(ToolTipText));

    /// <summary>Re-announces <see cref="IsEffective" /> even when it did not
    /// change: a click on the lit toggle unchecks it locally, and the OneWay
    /// binding only re-reads on a change notification.</summary>
    internal void Republish() => OnPropertyChanged(nameof(IsEffective));
}

/// <summary>
/// The words of the chord panel's "Apply to" ladder, read off the same
///     <see cref="ChordApplyScope" /> the edit is resolved on — so a rung cannot
///     promise one reach while the planner writes another.
///     <para>
///         It takes the place of a two-state scope switch and the separate
///         "Finger N alike shapes" button. Every rung is always on screen with
///         its count; one with nothing to reach is disabled and says why, and
///         the lit rung is the one the next edit really takes.
///     </para>
/// </summary>
public sealed partial class ChordApplyLadder : ObservableObject
{
    private const string Separator = " · ";
    private readonly ILocalizationService _loc;

    /// <summary>Set when a reshape was refused because nothing is selected;
    /// cleared by the next load or selection change.</summary>
    [ObservableProperty] private bool _showsReshapeHint;

    [ObservableProperty] private bool _isVisible;

    public ChordApplyLadder(ILocalizationService loc)
    {
        _loc = loc;
        Rows = [ThisChord, Selected, EveryUse, SameGrip];
    }

    /// <summary>The strings the ladder speaks — shared with the panel's preview row.</summary>
    internal ILocalizationService Localization => _loc;

    public ChordApplyRungRow ThisChord { get; } = new(ChordApplyRung.ThisChord);
    public ChordApplyRungRow Selected { get; } = new(ChordApplyRung.Selected);
    public ChordApplyRungRow EveryUse { get; } = new(ChordApplyRung.EveryUse);
    public ChordApplyRungRow SameGrip { get; } = new(ChordApplyRung.SameGrip);

    public IReadOnlyList<ChordApplyRungRow> Rows { get; }

    /// <summary>Save has work on "+ same grip" with nothing typed: a grip
    /// template is fingered differently (<see cref="ChordApplyScope.GripFingeringDiffers" />).</summary>
    public bool GripsNeedFingering { get; private set; }

    /// <summary>The rung the next edit takes, or null when there is nothing to edit.</summary>
    public ChordApplyRung? Effective { get; private set; }

    /// <summary>What the lit rung reaches ("4 chords · all levels") — the one count the compact
    /// scope bar prints under its rungs. Empty when nothing is lit.</summary>
    public string EffectiveDetail => Rows.FirstOrDefault(r => r.Rung == Effective)?.Detail ?? string.Empty;

    public bool HasEffectiveDetail => EffectiveDetail.Length > 0;

    /// <summary>
    ///     What the next fret or shape edit does, in one sentence, BEFORE it is made: "“Am” itself
    ///     changes, so every use plays the new grip" or "A new shape is made for these 4 chords; the
    ///     other 10 keep “Am”". The old "Selected" rung silently meant either one, depending on
    ///     whether the selection happened to cover every use. Empty when the edit is refused or is a
    ///     technique edit, which never writes the shape.
    /// </summary>
    public string ResultText { get; private set; } = string.Empty;

    public bool HasResultText => ResultText.Length > 0;

    /// <summary>
    ///     How many sounds one press of Save re-fingers with the pending draft —
    ///     every chord and hand shape, the "same grip" templates' included. It is
    ///     resolved once per <see cref="Update" /> or draft change on the cached
    ///     scope, never per read: the scope walks the whole arrangement.
    /// </summary>
    public int FingeringTargetCount { get; private set; }

    private ChordApplyScope? _scope;
    private ChordApplyRung _chosen;
    private ChordApplyFamily _family;

    /// <summary>Re-reads every rung off <paramref name="scope" />. <paramref name="family" />
    /// is the edits on screen: over the Technique and Playback segments the rungs count
    /// the chords a technique reaches, and "selected" is the whole selection.</summary>
    internal void Update(ChordApplyScope? scope, ChordApplyRung chosen, ChordTemplate? draft = null,
        ChordApplyFamily family = ChordApplyFamily.Shape)
    {
        _scope = scope;
        _chosen = chosen;
        _family = family;
        IsVisible = scope != null;
        Effective = scope?.Effective(chosen, family);
        OnPropertyChanged(nameof(Effective));
        // Save fingering is a SHAPE edit, whichever segment is on screen.
        GripsNeedFingering = scope?.Effective(chosen) == ChordApplyRung.SameGrip && scope.GripFingeringDiffers;
        OnPropertyChanged(nameof(GripsNeedFingering));
        foreach (var row in Rows)
            Fill(row, scope);
        OnPropertyChanged(nameof(EffectiveDetail));
        OnPropertyChanged(nameof(HasEffectiveDetail));
        ResultText = family == ChordApplyFamily.Shape ? ResultOf(scope, chosen) : string.Empty;
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(HasResultText));
        UpdateFingering(draft);
    }

    /// <summary>Re-counts <see cref="FingeringTargetCount" /> for a new draft:
    /// typing the last missing finger is what lets a fingering reach the grips.</summary>
    internal void UpdateFingering(ChordTemplate? draft)
    {
        var targets = _scope?.Resolve(_chosen, ChordTemplateEditKind.Fingering, draft);
        FingeringTargetCount = targets is null or { Path: ChordApplyPath.Refused }
            ? 0
            : targets.ChordCount + targets.HandShapeCount;
    }

    private string ResultOf(ChordApplyScope? scope, ChordApplyRung chosen)
    {
        if (scope is null) return string.Empty;
        var targets = scope.Resolve(chosen, ChordTemplateEditKind.Shape);
        var shape = scope.Loaded;
        var name = shape.Unnamed || shape.Name.Length == 0 ? shape.GetTemplateFrets() : shape.Name;
        switch (targets.Path)
        {
            case ChordApplyPath.InPlace:
                return _loc.Format("ShapeEdit_Result_Changes", name);
            case ChordApplyPath.Rebind:
                var reached = targets.Sounds.Chords.Count;
                var kept = Math.Max(0, scope.Reach(ChordApplyRung.EveryUse).Chords - reached);
                return reached == 1
                    ? _loc.Format("ShapeEdit_Result_NewOne", kept, name)
                    : _loc.Format("ShapeEdit_Result_NewMany", reached, kept, name);
            default:
                return string.Empty;
        }
    }

    private void Fill(ChordApplyRungRow row, ChordApplyScope? scope)
    {
        var reach = scope?.Reach(row.Rung, _family) ?? default;
        row.Reach = reach;
        row.IsAvailable = reach.IsAvailable;
        row.IsEffective = Effective == row.Rung;
        row.Republish();
        row.Label = LabelOf(row.Rung, scope, reach);
        row.Detail = reach.IsAvailable ? DetailOf(row.Rung, reach, scope!) : string.Empty;
        row.Tip = TipOf(row.Rung, reach.IsAvailable);
        row.AccessibleName = row.Detail.Length > 0 ? $"{row.Label}, {row.Detail}" : row.Label;
    }

    private string LabelOf(ChordApplyRung rung, ChordApplyScope? scope, ChordApplyRungReach reach) => rung switch
    {
        ChordApplyRung.ThisChord when _family == ChordApplyFamily.Technique => _loc["ChordTemplate_Scope_ThisChord"],
        ChordApplyRung.ThisChord => ThisLabel(scope?.ThisSounds ?? ChordApplySounds.None),
        ChordApplyRung.Selected => _loc.Format("ChordTemplate_Apply_Selected", reach.Sounds),
        ChordApplyRung.EveryUse => _loc["ChordTemplate_Apply_EveryUse"],
        _ => _loc["ChordTemplate_Apply_SameGrip"]
    };

    /// <summary>"this chord", or — when the hand-shape sync folded the bar over
    /// it in — "this chord + its hand shape", so the sync is never invisible.</summary>
    private string ThisLabel(ChordApplySounds sounds)
    {
        if (sounds.Chords.Count > 0)
        {
            return sounds.HandShapes.Count > 0
                ? _loc["ChordTemplate_Apply_ThisChordAndHandShape"]
                : _loc["ChordTemplate_Scope_ThisChord"];
        }

        return sounds.HandShapes.Count > 0 ? _loc["ChordTemplate_Scope_ThisHandShape"] : _loc["ChordTemplate_Scope_ThisChord"];
    }

    private string DetailOf(ChordApplyRung rung, ChordApplyRungReach reach, ChordApplyScope scope)
    {
        switch (rung)
        {
            // "this chord" prints a count only when the edit reaches more than
            // it: the unbound strums under its hand shape join an in-place edit.
            case ChordApplyRung.ThisChord when reach.Sounds <= scope.ThisSounds.Count || _family == ChordApplyFamily.Technique:
                return string.Empty;
            case ChordApplyRung.SameGrip:
                return string.Join(Separator,
                    _loc.Plural("ChordTemplate_Apply_MoreShapesOne", "ChordTemplate_Apply_MoreShapes", reach.GripTemplates),
                    _loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", reach.GripChords));
        }

        if (reach.Sounds == 0) return _loc["ChordTemplate_Reach_Unused"];
        var parts = new List<string> { _loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", reach.Chords) };
        if (reach.HandShapes > 0)
            parts.Add(_loc.Plural("ChordTemplate_Reach_HandShapesOne", "ChordTemplate_Reach_HandShapes", reach.HandShapes));
        if (reach.Levels > 1) parts.Add(_loc["ChordTemplate_Reach_AllLevels"]);
        return string.Join(Separator, parts);
    }

    private string TipOf(ChordApplyRung rung, bool available)
    {
        if (!available)
        {
            return rung == ChordApplyRung.SameGrip
                ? _loc["ChordTemplate_Apply_NoGripTip"]
                : _loc["ChordTemplate_Apply_NeedsSelectionTip"];
        }

        return rung switch
        {
            ChordApplyRung.ThisChord => _loc["ChordTemplate_Apply_ThisChordTip"],
            ChordApplyRung.Selected => _loc["ChordTemplate_Apply_SelectedTip"],
            ChordApplyRung.EveryUse => _loc["ChordTemplate_Apply_EveryUseTip"],
            _ => _loc["ChordTemplate_Apply_SameGripTip"]
        };
    }

    /// <summary>The Save button's caption, with the count once it reaches
    /// more than one sound.</summary>
    public string SaveFingeringLabel(int sounds) =>
        sounds > 1 ? _loc.Format("ChordTemplate_SaveFingering_Many", sounds) : _loc["ChordTemplate_SaveFingering"];

    /// <summary>What the status rail says after a technique or playback edit the ladder took
    /// past the selection: "Technique applied to 14 chords on 3 shapes".</summary>
    public string DescribeTechnique(IReadOnlyList<Chord> chords) =>
        _loc.Format("ChordTemplate_Applied", _loc["ChordTemplate_Applied_Technique"],
            _loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", chords.Count),
            _loc.Plural("ChordTemplate_Applied_ShapesOne", "ChordTemplate_Applied_Shapes",
                chords.Select(c => c.TemplateId).Distinct().Count()));

    /// <summary>
    ///     What the status rail says after a commit: "Fingering applied to 14
    ///     chords · 4 hand shapes on 3 shapes". A shape, arpeggio or template
    ///     change on the "same grip" rung adds that it stayed on its own shape —
    ///     the rung was lit, so the charter must hear that it did not apply.
    /// </summary>
    public string Describe(ChordApplyTargets targets)
    {
        var what = _loc[$"ChordTemplate_Applied_{targets.Kind}"];
        var parts = new List<string>
        {
            _loc.Plural("ChordTemplate_Reach_ChordsOne", "ChordTemplate_Reach_Chords", targets.ChordCount)
        };
        if (targets.HandShapeCount > 0)
            parts.Add(_loc.Plural("ChordTemplate_Reach_HandShapesOne", "ChordTemplate_Reach_HandShapes", targets.HandShapeCount));

        var message = _loc.Format("ChordTemplate_Applied", what, string.Join(Separator, parts),
            _loc.Plural("ChordTemplate_Applied_ShapesOne", "ChordTemplate_Applied_Shapes", targets.ShapeCount));
        if (targets.GripsWithheld) return $"{message} {_loc["ChordTemplate_Applied_GripNeedsEveryFinger"]}";
        var reachesGrips = targets.Kind is ChordTemplateEditKind.Fingering or ChordTemplateEditKind.Name;
        return targets.Rung == ChordApplyRung.SameGrip && !reachesGrips
            ? $"{message} {_loc.Format("ChordTemplate_Applied_GripSkipped", what)}"
            : message;
    }
}
