using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>Which of DLC Builder's build steps run (<see cref="DlcBuilderBuildSteps" />).</summary>
/// <param name="ApplyFixes">Its arrangement improvements (<see cref="DdImprover" />), on every arrangement.</param>
/// <param name="GenerateDynamicDifficulty">Its level generator (<see cref="DdGenerator" />), on an arrangement with one level.</param>
/// <param name="LevelCountMode">How many levels each phrase gets.</param>
public sealed record DlcBuilderSteps(bool ApplyFixes, bool GenerateDynamicDifficulty, DdLevelCountMode LevelCountMode = DdLevelCountMode.Simple)
{
    /// <summary>What DLC Builder does on a release build with its default configuration.</summary>
    public static readonly DlcBuilderSteps Default = new(true, true);

    public bool Any => ApplyFixes || GenerateDynamicDifficulty;

    /// <summary>The steps the app's settings ask every Rocksmith export for.</summary>
    public static DlcBuilderSteps FromConfig(AppConfig config) => new(config.ExportApplyDlcBuilderFixes,
        config.ExportGenerateDynamicDifficulty, ParseMode(config.ExportDdLevelCountMode));

    public static DdLevelCountMode ParseMode(string? mode) =>
        string.Equals(mode, nameof(DdLevelCountMode.MLModel), StringComparison.OrdinalIgnoreCase)
            ? DdLevelCountMode.MLModel
            : DdLevelCountMode.Simple;
}

/// <summary>
///     The steps DLC Builder runs on every arrangement it builds, in its order: its
///     arrangement improvements (<see cref="DdImprover" />, <c>ArrangementImprover.applyAll</c>),
///     then, for an arrangement with exactly ONE difficulty level, dynamic difficulty
///     (<see cref="DdGenerator" />, <c>Generator.generateForArrangement</c>). They run on a COPY:
///     the export ships the copy and the chart is never touched (the chart's own "Apply DLC
///     Builder fixes" action is the one place that puts the result into a project).
///     <para>
///         <b>They run on the arrangement's RS2014 XML, as DLC Builder does.</b> The copy is
///         written with <see cref="RsXmlSongWriter" /> (the XML every export ships, so every
///         export-boundary rule has already applied), the steps rewrite that document's levels,
///         phrases, phrase iterations, chord templates, sections and events, and
///         <see cref="RsXmlSongReader" /> reads it back. The read-back levels, templates,
///         phrases and event points replace the copy's; tones, properties and everything else
///         stay the chart's own. Every position read back is put on the CHART's beat grid at
///         the whole millisecond the XML said, so an untouched note lands on exactly the
///         millisecond the plain export writes.
///     </para>
///     <para>
///         <b>Seven and eight strings.</b> The XML spells a 7-/8-string guitar in the package
///         order (E..e, then the extra strings); the steps are given the chart's order (lowest
///         first) and templates as wide as the guitar, and the result goes back in package
///         order. The SNG mapper then builds the RS7X block from the copy as for any chart, so
///         its template and ChordNotes rows match the generated levels by construction.
///     </para>
/// </summary>
public static class DlcBuilderBuildSteps
{
    /// <summary>Whether <see cref="DlcBuilderSteps.GenerateDynamicDifficulty" /> applies to
    /// <paramref name="arrangement" />: an instrument with exactly one difficulty level.</summary>
    public static bool GeneratesLevels(Arrangement arrangement) =>
        arrangement.ArrangementType != ArrangementType.Vocal && arrangement.Levels.Count == 1;

    /// <summary>
    ///     A copy of <paramref name="arrangement" /> with <paramref name="steps" /> applied, or
    ///     null when none of them applies (a vocal track, an arrangement without notes, no beat
    ///     map, or only level generation asked of a chart that has levels). Neither argument is
    ///     changed.
    /// </summary>
    /// <param name="project">The project the arrangement belongs to (its beats and song data).</param>
    /// <param name="arrangement">The arrangement.</param>
    /// <param name="songLengthMs">The song's length when known (the XML's <c>songLength</c>,
    /// which ends the last phrase), as <see cref="RsXmlSongWriter.Write(ChartProject, double?)" /> takes it.</param>
    /// <param name="steps">Which steps run.</param>
    public static Arrangement? Apply(ChartProject project, Arrangement arrangement, double? songLengthMs, DlcBuilderSteps steps)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(steps);
        var generate = steps.GenerateDynamicDifficulty && GeneratesLevels(arrangement);
        if (!steps.ApplyFixes && !generate) return null;
        if (arrangement.ArrangementType == ArrangementType.Vocal || project.Beats.Count < 2) return null;

        var single = new ChartProject
        {
            AudioFileName = project.AudioFileName,
            Bpm = project.Bpm,
            Chart = project.Chart,
            Beats = project.Beats,
            Arrangements = [arrangement],
        };
        var document = new RsXmlSongWriter().Write(single, songLengthMs is > 0 ? songLengthMs : null);
        var song = document.Root!;

        var extended = Rs7StringLayout.IsExtendedGuitar(arrangement);
        var stringCount = arrangement.StringCount;
        var dd = DdXml.Read(song, extended ? stringCount : Rs7StringLayout.NativeStrings);
        if (dd.Levels.Count == 0 || dd.PhraseIterations.Count == 0) return null;
        if (dd.Levels.All(l => l.Notes.Count + l.Chords.Count == 0)) return null;

        if (extended) Renumber(dd, s => Rs7StringLayout.ToDomain(s, stringCount));
        if (steps.ApplyFixes) DdImprover.ApplyAll(dd);
        // DLC Builder decides on the level count AFTER its improvements, as here.
        if (generate && dd.Levels.Count == 1) DdGenerator.GenerateForArrangement(DdGeneratorConfig.For(steps.LevelCountMode), dd);
        if (extended) Renumber(dd, s => Rs7StringLayout.ToSng(s, stringCount));
        DdXml.Write(song, dd);

        var readBack = new RsXmlSongReader().ReadDocument(document, "dlc-builder-steps.xml");
        OntoChartGrid(readBack, project.Beats);
        var built = readBack.Arrangements[0];

        var copy = ChartProjectCloner.CloneArrangement(arrangement);
        copy.Levels = built.Levels;
        copy.ChordTemplates = built.ChordTemplates;
        copy.FretHandMuteTemplates = built.FretHandMuteTemplates;
        copy.Phrases = built.Phrases;
        copy.PhraseIterations = built.PhraseIterations;
        copy.PhraseInfos = built.PhraseInfos;
        copy.EventPoints = built.EventPoints;
        // The phrases are the explicit lists now; a phrase marker on an event point would be
        // merged back in by the writers.
        foreach (var point in copy.EventPoints) point.Phrase = "";
        return copy;
    }

    /// <summary>
    ///     <see cref="Apply" /> for an export: the copy, or <paramref name="arrangement" /> itself
    ///     when nothing applies or a step fails. A failure ships the chart as it is, with a
    ///     warning, rather than failing the export. The log names every arrangement changed.
    /// </summary>
    public static Arrangement ApplyOrKeep(ChartProject project, Arrangement arrangement, double? songLengthMs,
        DlcBuilderSteps steps, IAppLogger logger, ICollection<string>? warnings = null)
    {
        if (!steps.Any) return arrangement;
        try
        {
            if (Apply(project, arrangement, songLengthMs, steps) is not { } built) return arrangement;
            var levels = built.Levels.Count != arrangement.Levels.Count
                ? $"generated dynamic difficulty ({built.Levels.Count} levels, {built.Phrases.Count} phrases, {steps.LevelCountMode} level counts)"
                : "no levels generated";
            logger.Info(AppLogCategory.Project,
                $"DLC Builder steps on arrangement '{arrangement.Name}': fixes {(steps.ApplyFixes ? "applied" : "off")}, {levels}.");
            return built;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException
                                       or IndexOutOfRangeException or KeyNotFoundException or FormatException
                                       or NullReferenceException)
        {
            warnings?.Add($"Arrangement '{arrangement.Name}': DLC Builder's fixes and dynamic difficulty could not be applied " +
                          $"({ex.Message}); it ships as charted.");
            logger.Warning(AppLogCategory.Project,
                $"DLC Builder steps failed. Arrangement='{arrangement.Name}', message='{ex.Message}'.");
            return arrangement;
        }
    }

    /// <summary>Renumbers every string index — notes, chord notes and template columns.</summary>
    private static void Renumber(DdArrangement dd, Func<int, int> map)
    {
        foreach (var level in dd.Levels)
        {
            foreach (var note in level.Notes) note.String = map(note.String);
            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes ?? []) chordNote.String = map(chordNote.String);
            }
        }

        foreach (var template in dd.ChordTemplates)
        {
            var frets = (int[])template.Frets.Clone();
            var fingers = (int[])template.Fingers.Clone();
            for (var s = 0; s < frets.Length; s++)
            {
                template.Frets[map(s)] = frets[s];
                template.Fingers[map(s)] = fingers[s];
            }
        }
    }

    /// <summary>
    ///     Moves every position of <paramref name="readBack" /> (read on the XML's own beats,
    ///     which are whole milliseconds) onto <paramref name="chartBeats" /> at the same whole
    ///     millisecond, sustains included.
    /// </summary>
    private static void OntoChartGrid(ChartProject readBack, IList<BeatMarker> chartBeats)
    {
        var xmlBeats = readBack.Beats;
        int Ms(FractionalPosition p) => TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(p, xmlBeats));
        FractionalPosition OnChart(int ms) => TimelineMath.PositionFromImportedMs(ms, chartBeats);

        // Sustains are durations from their sound's onset: take the ends before the onsets move.
        var noteEnds = new List<(GuitarNote Note, int EndMs)>();
        var chordNoteEnds = new List<(Chord Chord, ChordNote Note, int EndMs)>();
        foreach (var level in readBack.Arrangements.SelectMany(a => a.Levels))
        {
            noteEnds.AddRange(level.Notes.Where(n => n.Sustain > FractionalPosition.Zero)
                .Select(n => (n, Ms(n.Position + n.Sustain))));
            chordNoteEnds.AddRange(level.Chords.SelectMany(c => c.ChordNotes
                .Where(cn => cn.Sustain > FractionalPosition.Zero)
                .Select(cn => (c, cn, Ms(c.Position + cn.Sustain)))));
        }

        ChartProjectPositions.Remap(readBack, p => OnChart(Ms(p)));

        foreach (var (note, endMs) in noteEnds) note.Sustain = OnChart(endMs) - note.Position;
        foreach (var (chord, chordNote, endMs) in chordNoteEnds) chordNote.Sustain = OnChart(endMs) - chord.Position;
    }
}
