using DotCharter.Localization;

namespace DotCharter.Services.Editing.Selection.Patterns;

/// <summary>
///     A pattern in one readable line — "Chords · same frets · same strings ·
///     + hand shape, FHP".
///     <para>
///         Twenty-odd facets spread over six sections is a form nobody can hold
///         in their head, and the question the builder exists to answer ("which
///         of these definitions is useful?") is a COMPARISON. Comparing means
///         reading several patterns at once, which means each one has to be
///         legible without opening it. So this is not decoration — it is the
///         difference between a list of names and a list of definitions.
///     </para>
///     <para>
///         Deliberately terse and deliberately lossy: it names what a charter
///         would notice, in the order they would say it, and drops facets left at
///         their default.
///     </para>
///     <para>
///         <b>Every clause is LOCALIZED.</b> English literals would leave the Pattern Builder's rail — the one line the whole
///         dialog exists to make comparable — in English under de-DE and
///         ja-JP while the form beside it is translated. The scope and technique
///         clauses deliberately reuse the CHECKBOX keys the dialog already draws
///         (<see cref="TechniqueKey" />), so a facet cannot be worded one way in
///         the form and another in the summary.
///     </para>
/// </summary>
public static class PatternSummary
{
    /// <summary>The separator between clauses — a middle dot, matching the rest
    ///     of the app's chip/meta rows.</summary>
    public const string Separator = " · ";

    /// <summary>
    ///     The resource key naming a technique facet — the SAME key the Pattern
    ///     Builder's checkbox for that facet uses, which is the point: the
    ///     dialog's form and its summary may not word a facet differently.
    ///     Twelve facets, four of them named from the Select-By-Query
    ///     vocabulary because that is where those four were first translated.
    /// </summary>
    public static string TechniqueKey(TechniqueFacet facet) => facet switch
    {
        TechniqueFacet.Mute => "Pattern_Technique_Mute",
        TechniqueFacet.Hopo => "Pattern_Technique_Hopo",
        TechniqueFacet.Harmonic => "Query_Technique_Harmonic",
        TechniqueFacet.Bend => "Query_Technique_Bend",
        TechniqueFacet.Slide => "Query_Technique_Slide",
        TechniqueFacet.Vibrato => "Query_Technique_Vibrato",
        TechniqueFacet.Tremolo => "Pattern_Technique_Tremolo",
        TechniqueFacet.Accent => "Pattern_Technique_Accent",
        TechniqueFacet.LinkNext => "Pattern_Technique_LinkNext",
        TechniqueFacet.BassPicking => "Pattern_Technique_BassPicking",
        TechniqueFacet.Ignored => "Pattern_Technique_Ignored",
        TechniqueFacet.PassOtherNotes => "Pattern_Technique_PassOtherNotes",
        _ => ""
    };

    /// <summary>The resource key naming an element kind in scope — again the
    /// dialog's own checkbox keys.</summary>
    public static string ScopeKey(PatternScope kind) => kind switch
    {
        PatternScope.Notes => "Pattern_Scope_Notes",
        PatternScope.Chords => "Pattern_Scope_Chords",
        PatternScope.HandShapes => "Pattern_Scope_HandShapes",
        PatternScope.Anchors => "Pattern_Scope_Anchors",
        _ => ""
    };

    public static string Describe(MatchPattern pattern, ILocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var parts = new List<string>(8);

        var scope = ScopeText(pattern.Scope, loc);
        if (scope.Length > 0) parts.Add(scope);

        if (pattern.Grouping == PatternGrouping.AsRun)
        {
            parts.Add(loc[pattern.Rhythm switch
            {
                RhythmRule.Exact => "PatternSummary_RunSameRhythm",
                RhythmRule.Proportional => "PatternSummary_RunAnyTempo",
                _ => "PatternSummary_RunAnySpacing"
            }]);
        }

        parts.Add(loc[pattern.Fret switch
        {
            FretRule.Exact => "PatternSummary_FretsSame",
            FretRule.Transposable => "PatternSummary_FretsAnyPosition",
            _ => "PatternSummary_FretsAny"
        }]);

        parts.Add(loc[pattern.Strings switch
        {
            StringRule.Exact => "PatternSummary_StringsSame",
            StringRule.Relative => "PatternSummary_StringsAny",
            _ => "PatternSummary_StringsIgnored"
        }]);

        if (!pattern.MatchSize && pattern.Strings == StringRule.Ignore)
            parts.Add(loc["PatternSummary_AnySize"]);
        if (!pattern.MatchSoundKind) parts.Add(loc["PatternSummary_ChordEqualsNotes"]);

        if (pattern.Sustain == SustainRule.SameLength) parts.Add(loc["PatternSummary_SameSustain"]);
        else if (pattern.Sustain == SustainRule.SameKind) parts.Add(loc["PatternSummary_SameHeldStruck"]);

        if (pattern.MatchBeatOffset) parts.Add(loc["PatternSummary_SameBeatOffset"]);
        if (pattern.MatchFingering) parts.Add(loc["PatternSummary_SameFingering"]);
        if (pattern.MatchChordName) parts.Add(loc["PatternSummary_SameChordName"]);
        if (pattern.MatchArpeggio) parts.Add(loc["PatternSummary_SameArpeggio"]);

        var techniques = TechniqueText(pattern.Techniques, loc);
        if (techniques.Length > 0) parts.Add(techniques);

        if (pattern.Scope.HasFlag(PatternScope.Anchors))
        {
            if (pattern.MatchAnchorWidth) parts.Add(loc["PatternSummary_SameAnchorWidth"]);
            parts.Add(loc[pattern.MatchAnchorSpan
                ? "PatternSummary_SameAnchorSpan"
                : "PatternSummary_AnyAnchorSpan"]);
        }

        if (pattern.Range == PatternRange.SameSection) parts.Add(loc["PatternSummary_SectionOnly"]);
        else if (pattern.Range == PatternRange.SamePhrase) parts.Add(loc["PatternSummary_PhraseOnly"]);

        // A COUNTED string, and it can legitimately be 1 — "within 1 frets" is
        // what it printed, in every language.
        if (pattern.MaxFretDistance > 0)
        {
            parts.Add(loc.Plural("PatternSummary_WithinFretsOne", "PatternSummary_WithinFretsFmt",
                pattern.MaxFretDistance));
        }

        var companions = CompanionText(pattern.Companions, loc);
        if (companions.Length > 0) parts.Add(companions);

        return string.Join(Separator, parts);
    }

    private static string ScopeText(PatternScope scope, ILocalizationService loc)
    {
        var kinds = new List<string>(4);
        foreach (var kind in new[]
                 {
                     PatternScope.Notes, PatternScope.Chords,
                     PatternScope.HandShapes, PatternScope.Anchors
                 })
        {
            if (scope.HasFlag(kind))
                kinds.Add(loc[ScopeKey(kind)]);
        }

        return kinds.Count == 0 ? loc["PatternSummary_ScopeNone"] : string.Join(" + ", kinds);
    }

    private static string TechniqueText(TechniqueFacet techniques, ILocalizationService loc)
    {
        if (techniques == TechniqueFacet.None) return string.Empty;

        if (techniques == TechniqueFacet.All) return loc["PatternSummary_EveryTechnique"];

        var names = new List<string>(4);
        foreach (var facet in Enum.GetValues<TechniqueFacet>())
        {
            if (facet is TechniqueFacet.None or TechniqueFacet.All) continue;
            if (!techniques.HasFlag(facet)) continue;
            names.Add(loc[TechniqueKey(facet)]);
        }

        // Past a handful the list stops being readable and the count is the
        // useful fact. Never 1 by construction (the branch needs five), so this
        // one is a Format and not a Plural pair.
        return names.Count > 4
            ? loc.Format("PatternSummary_TechniqueCountFmt", names.Count)
            : loc.Format("PatternSummary_TechniqueListFmt", string.Join(" + ", names));
    }

    private static string CompanionText(PatternCompanions companions, ILocalizationService loc)
    {
        if (companions == PatternCompanions.None) return string.Empty;

        var names = new List<string>(3);
        if (companions.HasFlag(PatternCompanions.CoveringHandShape))
            names.Add(loc["PatternSummary_CompanionHandShape"]);
        if (companions.HasFlag(PatternCompanions.GoverningAnchor))
            names.Add(loc["PatternSummary_CompanionAnchor"]);
        if (companions.HasFlag(PatternCompanions.SoundsInside))
            names.Add(loc["PatternSummary_CompanionSounds"]);
        return loc.Format("PatternSummary_CompanionsFmt", string.Join(", ", names));
    }
}
