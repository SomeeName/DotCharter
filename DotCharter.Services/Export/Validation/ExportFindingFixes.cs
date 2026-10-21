namespace DotCharter.Services.Export.Validation;

/// <summary>Which existing verb repairs an export finding. <see cref="None" /> means the
/// charter has to decide; <see cref="ExportFindingFixes.NoAutomaticFix" /> says why.</summary>
public enum ExportFixKind
{
    None,

    /// <summary>The auto-fix pipeline on the finding's arrangement (sorting, duplicates, sustain
    /// overlaps, frets under the capo).</summary>
    AutoFix,

    /// <summary>Fret-hand positions where none cover the notes: a level with no anchors gets a full
    /// generation, a level with anchors only gets new ones BEFORE its first, so hand-placed anchors
    /// are never replaced.</summary>
    FillFhp,

    /// <summary>The structure generator: COUNT and END phrases and the no-guitar sections.</summary>
    GenerateStructure,

    /// <summary>Not a chart edit: opens the Tone designer on the finding's arrangement. An undefined
    /// key the project can only mean one way is renamed in place instead (<c>RocksmithFixes</c> asks
    /// <see cref="Editing.Tones.ToneKeyRepair" />): that answer depends on the project, not the key.</summary>
    OpenToneDesigner
}

/// <summary>
///     The one decision per <see cref="ChartExportValidator" /> key: the verb that fixes it, or the
///     reason there is none. <c>ExportFindingFixesTests</c> reflects over every <c>Key*</c> constant
///     and fails when one is in neither table, so a new finding cannot ship without a decision.
/// </summary>
public static class ExportFindingFixes
{
    public static IReadOnlyDictionary<string, ExportFixKind> Fixes { get; } = new Dictionary<string, ExportFixKind>(StringComparer.Ordinal)
    {
        [ChartExportValidator.KeyMissingFhpCoverage] = ExportFixKind.FillFhp,
        [ChartExportValidator.KeyOverlappingNotes] = ExportFixKind.AutoFix,
        [ChartExportValidator.KeyNoteBelowCapo] = ExportFixKind.AutoFix,
        [ChartExportValidator.KeyNoPhrases] = ExportFixKind.GenerateStructure,
        [ChartExportValidator.KeyToneWithoutSlot] = ExportFixKind.OpenToneDesigner,
        [ChartExportValidator.KeyMissingBaseTone] = ExportFixKind.OpenToneDesigner,
        [ChartExportValidator.KeyTooManyTones] = ExportFixKind.OpenToneDesigner,
        [ChartExportValidator.KeyToneNotDefined] = ExportFixKind.OpenToneDesigner
    };

    /// <summary>Keys with no automatic fix, each with the reason a machine should not decide it.</summary>
    public static IReadOnlyDictionary<string, string> NoAutomaticFix { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ChartExportValidator.KeyNoBeatMap] = "The beat map is the song's tempo; it comes from the charter or the beat detector, not from a repair.",
        [ChartExportValidator.KeyNoArrangements] = "There is nothing to repair: the project has no instrumental arrangement to chart.",
        [ChartExportValidator.KeyNoNotes] = "The notes ARE the charting; an empty arrangement is untick-or-chart, not a fix.",
        [ChartExportValidator.KeyEmptyPhrase] = "An unused phrase name is harmless, and only the charter knows whether it was meant to be placed.",
        [ChartExportValidator.KeyTooManyPhrases] = "Which phrases to merge is a judgement about the song's form.",
        [ChartExportValidator.KeyFretOutOfRange] = "Refretting the whole arrangement to move one note would rewrite the charter's fingering.",
        [ChartExportValidator.KeyStringOutOfRange] = "A string the arrangement does not have cannot be folded onto one it has without changing what is played.",
        [ChartExportValidator.KeyExtendedRangeGuitar] = "A notice, not a fault: a 7-string guitar ships for the rs7string mod, and only the charter decides to drop strings.",
        [ChartExportValidator.KeyEightStringMod] = "Only a later rs7string build will play an eighth string; dropping or refretting it would change the chart, so the charter decides.",
        [ChartExportValidator.KeyFirstNoteTooEarly] = "Moving the first note means padding the audio too: a deliberate tempo-map operation.",
        [ChartExportValidator.KeyFirstPhraseNotAtStart] = "Where the first phrase starts decides which phrase the early notes belong to.",
        [ChartExportValidator.KeyLyricTooLong] = "Only the charter can split a lyric syllable.",
        [ChartExportValidator.KeyTooManyLevels] = "Which difficulty levels to drop changes what the player is offered; the charter picks them.",
        [ChartExportValidator.KeyTooManyNotes] = "Thinning a level this dense is a musical decision, and a split into arrangements is the charter's.",
        [ChartExportValidator.KeyFingerOutOfRange] = "A finger past the little finger is a typo in the chord box; only the charter knows which finger was meant.",
        [ChartExportValidator.KeyStringBeyondTuning] = "A note on a string the tuning lacks has no string to move to without changing what is played.",
        [ChartExportValidator.KeyStringCountUnsupported] = "A package spells six strings (eight for a guitar); dropping strings is a rearrangement the charter decides.",
        [ChartExportValidator.KeyDisplayNameGlyphs] = "A romanised or English title is a choice of words, never a transliteration a machine should guess (#747)."
    };

    /// <summary>The verb that fixes a finding with this key, or <see cref="ExportFixKind.None" />.</summary>
    public static ExportFixKind For(string messageKey) =>
        Fixes.TryGetValue(messageKey, out var kind) ? kind : ExportFixKind.None;
}
