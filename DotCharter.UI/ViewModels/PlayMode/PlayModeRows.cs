// The read-model row/option records the Play Mode screens bind to (sections, tones) plus ArrangementOption.

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>One row of the results-screen section breakdown.</summary>
public sealed record SectionRow(string Name, string TimeLabel, string AccuracyLabel, int Misses, bool WasPlayed,
    double StartMs, string BarLabel = "")
{
    /// <summary>The section carries a chart BAR range. Empty for a chart with
    /// no measure markers and for runs recorded before the bar range existed - "not recorded",
    /// never bar 0 - and the row then shows only its timecode.</summary>
    public bool HasBarLabel => BarLabel.Length > 0;

    /// <summary>Speaks <see cref="MissesLabel" />; the results screen hands in its own.</summary>
    public required ILocalizationService Localization { get; init; }

    /// <summary>
    ///     Accuracy 0..1 — the BAR's length on the results breakdown
    ///     (<c>Results.dc.html</c>). It is the same number
    ///     <see cref="AccuracyLabel" /> formats; the label alone could not drive
    ///     a bar without being parsed back out of its own text.
    /// </summary>
    public double Accuracy { get; init; }

    /// <summary>Bar length. An unplayed section has no bar rather than a
    /// zero-length one that reads as "played, scored nothing".</summary>
    public double BarFraction => WasPlayed ? Math.Clamp(Accuracy, 0, 1) : 0;

    // The mockup's three bands. They are thresholds on the real accuracy, not
    // ranks: a run where every section is above 90 % gets ten green bars, which
    // is the correct picture.
    public bool IsStrong => WasPlayed && Accuracy >= 0.90;
    public bool IsFair => WasPlayed && Accuracy is >= 0.75 and < 0.90;
    public bool IsWeak => WasPlayed && Accuracy < 0.75;

    /// <summary>A section you dropped notes in is one you can practise. The
    /// mockup puts its "Practice" link on exactly these rows — offering it on a
    /// clean section is offering to drill something already solved.</summary>
    public bool CanPractise => WasPlayed && Misses > 0;

    /// <summary>
    ///     "7 missed", or "clean" when nothing was dropped.
    ///     <para>
    ///         Deliberately NOT a <c>Plural</c> pair: "1 missed" / "7 missed"
    ///         and "1 verpasst" / "7 verpasst" are both correct in both shipped
    ///         languages, so a second form would be identical everywhere — which
    ///         is the decorative pair `PluralStringTests` exists to reject.
    ///     </para>
    /// </summary>
    public string MissesLabel => !WasPlayed
        ? ""
        : Misses == 0
            ? Localization["PlayMode_Results_SectionClean"]
            : Localization.Format("PlayMode_Results_SectionMissedFmt", Misses);
}

/// <summary>One saved user tone profile in the pre-play "My tone" picker
/// (2.4 per-song tone preference). Id is the persisted preset id.</summary>
public sealed record MyTonePresetOption(string Id, string Name);

/// <summary>One selectable path (arrangement) on the pre-play screen.</summary>
public sealed partial class ArrangementOption
{
    private readonly ILocalizationService _localization;

    public ArrangementOption(Arrangement arrangement, ILocalizationService localization)
    {
        Arrangement = arrangement;
        _localization = localization;
    }

    public Arrangement Arrangement { get; }
    public string Name => Arrangement.Name;

    /// <summary>
    ///     What the path list calls it. Rocksmith names both guitar paths "Combo" (the
    ///     loader then makes the second "Combo (2)"), while the song list names paths by
    ///     their route — so the pre-play list said "Combo" for what the song list had
    ///     just called Lead. A generic "Combo" takes its path word here; a real name
    ///     stays. <see cref="Name" /> is untouched: it keys the run and its records.
    /// </summary>
    public string DisplayName { get; private set; } = "";

    private string PathWord
    {
        get
        {
            var p = Arrangement.Properties;
            if (p.PathBass != 0) return "Bass";
            if (p.PathLead != 0 && p.PathRhythm == 0) return "Lead";
            if (p.PathRhythm != 0 && p.PathLead == 0) return "Rhythm";
            return Arrangement.ArrangementType switch
            {
                ArrangementType.Lead => "Lead",
                ArrangementType.Rhythm => "Rhythm",
                ArrangementType.Bass => "Bass",
                _ => ""
            };
        }
    }

    /// <summary>Gives every option its <see cref="DisplayName" /> — two that come out the
    /// same are numbered in list order ("Lead", "Lead (2)") — and its
    /// <see cref="ShowsTuning" />.</summary>
    public static void AssignDisplayNames(IList<ArrangementOption> options)
    {
        var oneTuning = options.Select(o => o.TuningDisplayLabel).Distinct(StringComparer.Ordinal).Count() == 1;
        foreach (var option in options) option.ShowsTuning = !oneTuning;
        var labels = options
            .Select(o => GenericName().IsMatch(o.Name.Trim()) && o.PathWord.Length > 0 ? o.PathWord : o.Name)
            .ToList();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.Count; i++)
        {
            var label = labels[i];
            var n = seen[label] = seen.GetValueOrDefault(label) + 1;
            var repeated = labels.Count(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase)) > 1;
            options[i].DisplayName = repeated && n > 1 && label != options[i].Name ? $"{label} ({n})" : label;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^Combo(\s*\(\d+\))?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GenericName();

    public string TypeLabel => Arrangement.ArrangementType.ToString();
    public string TuningLabel => string.Join(" ", Arrangement.Tuning);

    /// <summary>
    ///     How many playable sounds this path holds, counted from the LOADED
    ///     chart's hardest level. Deliberately not the library index's
    ///     <c>NotesHard</c>: by the time this screen exists the real chart is
    ///     open, and counting it is exact for own charts and imports too — the
    ///     manifest figure only exists for Rocksmith packages.
    /// </summary>
    public int NoteCount
    {
        get
        {
            var level = Arrangement.Levels.Count == 0
                ? null
                : Arrangement.Levels.MaxBy(l => l.DifficultyIndex);
            return level == null ? 0 : level.Notes.Count + level.Chords.Count;
        }
    }

    public bool HasNoteCount => NoteCount > 0;

    /// <summary>The player's best accuracy on THIS path, or null when they have
    /// never finished a run of it. Null is the UNPLAYED state and must never be
    /// rendered as 0 %.</summary>
    public double? BestAccuracy { get; set; }

    /// <summary>Formatted best ("97.4%"), empty when unplayed.</summary>
    public string BestLabel { get; set; } = "";

    public bool HasBest => BestAccuracy.HasValue;

    /// <summary>
    ///     "982 notes · Drop D" — the row's mono sub-line. The tuning only when the paths
    ///     differ (<see cref="ShowsTuning" />): with one tuning for all, the card's header
    ///     says it once and every row repeating it was noise.
    /// </summary>
    public string DetailLabel => (HasNoteCount, ShowsTuning) switch
    {
        (true, true) => _localization.Format("PlayMode_GetReady_Path_Detail", NoteCount, TuningDisplayLabel),
        (true, false) => _localization.Plural("PlayMode_GetReady_Path_NotesOne", "PlayMode_GetReady_Path_Notes", NoteCount),
        (false, true) => TuningDisplayLabel,
        _ => ""
    };

    /// <summary>False when every path in the list shares one tuning (set with the display names).</summary>
    public bool ShowsTuning { get; private set; } = true;

    /// <summary>The tuning shown on the pre-play screen (path list + "Erwartete
    /// Stimmung"): the canonical common-tuning NAME when recognised, else the raw
    /// note letters (<see cref="TuningLabel" />, which stays the tooltip text and
    /// the tuning-verification value). Resolves from the arrangement's per-string
    /// OFFSETS when present (spelling-proof), falling back to the note letters, and
    /// reads a bass charted on a guitar path as the 5/4-string bass it is.</summary>
    public string TuningDisplayLabel => TuningNameResolver.ResolveFor(Arrangement) ?? TuningLabel;

    /// <summary>Uniform semitone offset vs. standard when every string shares
    /// one step (e.g. Eb standard = -1); null for non-uniform tunings.</summary>
    public int? UniformTuningStep
    {
        get
        {
            if (Arrangement.TuningPitches.Count == 0) return 0;
            var step = Arrangement.TuningPitches[0].Step;
            return Arrangement.TuningPitches.All(p => p.Step == step) ? step : null;
        }
    }
}
