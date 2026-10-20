using System.Globalization;
using System.Text;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;

namespace DotCharter.Services.Export.Validation;

public enum ExportFindingSeverity
{
    Warning,
    Error
}

/// <summary>A single pre-export validation finding. <see cref="MessageKey"/>
/// is a localization key ("Export.Validation.*"); <see cref="Detail"/> carries
/// the interpolation payload so the UI can format a localized message.</summary>
public sealed record ExportValidationFinding(
    string ArrangementName,
    ExportFindingSeverity Severity,
    string MessageKey,
    double? TimeMs = null,
    string Detail = "")
{
    public bool HasTime => TimeMs.HasValue;

    /// <summary>The verb that repairs this finding, or <see cref="ExportFixKind.None" />.</summary>
    public ExportFixKind Fix => ExportFindingFixes.For(MessageKey);
}

/// <summary>
/// Pure pre-export rule check for the PSARC pipeline (0.9.0
/// validation inspector). Complements the editing-time
/// <c>ArrangementValidationService</c> with the rules that decide whether an
/// export produces a playable package: FHP coverage, overlapping notes,
/// empty phrases, tone gaps, out-of-range frets, first-beat/silence rules
/// and vocals limits. UI hookup follows in the export dialog.
/// </summary>
public sealed class ChartExportValidator
{
    public const string KeyNoBeatMap = "Export.Validation.NoBeatMap";
    public const string KeyNoArrangements = "Export.Validation.NoArrangements";
    public const string KeyNoNotes = "Export.Validation.NoNotes";
    public const string KeyMissingFhpCoverage = "Export.Validation.MissingFhpCoverage";
    public const string KeyOverlappingNotes = "Export.Validation.OverlappingNotes";
    public const string KeyEmptyPhrase = "Export.Validation.EmptyPhrase";
    public const string KeyNoPhrases = "Export.Validation.NoPhrases";
    public const string KeyTooManyPhrases = "Export.Validation.TooManyPhrases";
    public const string KeyToneWithoutSlot = "Export.Validation.ToneWithoutSlot";
    public const string KeyMissingBaseTone = "Export.Validation.MissingBaseTone";
    public const string KeyTooManyTones = "Export.Validation.TooManyTones";
    public const string KeyToneNotDefined = "Export.Validation.ToneNotDefined";
    public const string KeyFretOutOfRange = "Export.Validation.FretOutOfRange";
    public const string KeyStringOutOfRange = "Export.Validation.StringOutOfRange";
    public const string KeyExtendedRangeGuitar = "Export.Validation.ExtendedRangeGuitar";
    public const string KeyEightStringMod = "Export.Validation.EightStringMod";
    public const string KeyNoteBelowCapo = "Export.Validation.NoteBelowCapo";
    public const string KeyFirstNoteTooEarly = "Export.Validation.FirstNoteTooEarly";
    public const string KeyFirstPhraseNotAtStart = "Export.Validation.FirstPhraseNotAtStart";
    public const string KeyLyricTooLong = "Export.Validation.LyricTooLong";
    public const string KeyDisplayNameGlyphs = "Export.Validation.DisplayNameGlyphs";

    private const int MaxFret = 24;

    private const int MaxPhrases = 100;
    private const double MinFirstNoteMs = 1000.0;
    private const int MaxLyricBytes = 47; // 48-byte fixed SNG field incl. terminator

    public IReadOnlyList<ExportValidationFinding> Validate(ChartProject project)
    {
        var findings = new List<ExportValidationFinding>();

        if (project.Beats.Count == 0)
        {
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Error, KeyNoBeatMap));
            return findings;
        }

        var instrumentals = project.Arrangements
            .Where(a => a.ArrangementType != ArrangementType.Vocal)
            .ToList();

        if (instrumentals.Count == 0)
            findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Error, KeyNoArrangements));

        foreach (var arrangement in instrumentals)
            ValidateArrangement(project, arrangement, findings);

        foreach (var track in project.VocalTracks)
            ValidateVocals(project, track, findings);

        ValidateDisplayNames(project, findings);
        return findings;
    }

    /// <summary>
    ///     The song list's strings, resolved exactly as the package writes them, that hold a
    /// character the game's font most likely lacks. A WARNING, never a rewrite: the
    ///     charter types an English or romanised name and keeps the original script in the
    ///     Japanese title/artist fields. The sort field is checked too, so what sorts matches
    ///     what shows.
    /// </summary>
    private static void ValidateDisplayNames(ChartProject project, List<ExportValidationFinding> findings)
    {
        var meta = RsPackageMetadataFactory.Create(project, new RocksmithBuildOptions { OutputPath = "", AudioPath = "" });
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in (string[])[meta.Title, meta.Artist, meta.ArtistSort, meta.Album])
        {
            if (RocksmithDlcKey.LacksGameGlyphs(value) && reported.Add(value))
                findings.Add(new ExportValidationFinding("", ExportFindingSeverity.Warning, KeyDisplayNameGlyphs, null, value));
        }
    }

    private static void ValidateArrangement(
        ChartProject project, Arrangement arrangement, List<ExportValidationFinding> findings)
    {
        var beats = project.Beats;
        var sink = new ArrangementFindings(arrangement.Name, findings);

        var hasNotes = arrangement.Levels.Any(l => l.Notes.Count > 0 || l.Chords.Count > 0);
        if (!hasNotes)
        {
            sink.Add(ExportFindingSeverity.Error, KeyNoNotes);
            return;
        }

        CheckFhpCoverage(arrangement, beats, sink);
        CheckOverlappingNotes(arrangement, beats, sink);
        CheckPhrases(arrangement, beats, sink);
        if (arrangement.ToneChanges.Count > 0)
            CheckTones(project, arrangement, beats, sink);
        CheckFretsAndStrings(arrangement, beats, sink);

        // ---- first-note silence rule -------------------------------------------
        var firstNoteMs = ContentTimesMs(arrangement, beats).DefaultIfEmpty(double.MaxValue).Min();
        if (firstNoteMs < MinFirstNoteMs)
            sink.Add(ExportFindingSeverity.Warning, KeyFirstNoteTooEarly, firstNoteMs);
    }

    /// <summary>Every finding of one arrangement, stamped with its name.</summary>
    private readonly record struct ArrangementFindings(string Arrangement, List<ExportValidationFinding> Findings)
    {
        public void Add(ExportFindingSeverity severity, string key, double? timeMs = null, string detail = "") =>
            Findings.Add(new ExportValidationFinding(Arrangement, severity, key, timeMs, detail));
    }

    private static void CheckFhpCoverage(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        foreach (var level in arrangement.Levels)
        {
            if (level.Anchors.Count == 0)
            {
                // Warning, not Error: the SNG mapper synthesizes a covering
                // anchor for an anchor-less level, so the export is game-safe;
                // the charter should still author real FHPs.
                var firstMs = FirstContentTimeMs(level, beats);
                if (firstMs.HasValue)
                    sink.Add(ExportFindingSeverity.Warning, KeyMissingFhpCoverage, firstMs, "level has no anchors");
                continue;
            }

            var firstAnchorMs = level.Anchors.Min(a => a.Position).GetPositionMs(beats);
            var uncovered = level.Notes.Select(n => n.Position)
                .Concat(level.Chords.Select(c => c.Position))
                .Select(p => p.GetPositionMs(beats))
                .Where(ms => ms < firstAnchorMs - 0.5)
                .OrderBy(ms => ms)
                .ToList();

            if (uncovered.Count > 0)
            {
                sink.Add(ExportFindingSeverity.Error, KeyMissingFhpCoverage, uncovered[0],
                    $"{uncovered.Count} note(s) before the first anchor");
            }
        }
    }

    private static void CheckOverlappingNotes(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        foreach (var level in arrangement.Levels)
        {
            foreach (var group in level.Notes.GroupBy(n => n.StringIndex))
            {
                var ordered = group.OrderBy(n => n.Position).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var previous = ordered[i - 1];
                    var current = ordered[i];
                    var previousEndMs = previous.EndPosition.GetPositionMs(beats);
                    var currentMs = current.Position.GetPositionMs(beats);
                    var previousMs = previous.Position.GetPositionMs(beats);

                    if (currentMs - previousMs < 0.5 || currentMs < previousEndMs - 2.0)
                    {
                        sink.Add(ExportFindingSeverity.Error, KeyOverlappingNotes, currentMs,
                            $"string {current.StringIndex}");
                        break; // one finding per string per level is enough
                    }
                }
            }
        }
    }

    private static void CheckPhrases(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        var iterations = arrangement.PhraseIterations;
        // BOTH phrase stores (PhraseMarkers): an editor-authored chart keeps its phrases on
        // EventPoint.Phrase, and reading only Phrases/PhraseIterations called it phrase-less.
        if (arrangement.Phrases.Count == 0 && PhraseMarkers.All(arrangement).Count == 0)
        {
            sink.Add(ExportFindingSeverity.Warning, KeyNoPhrases);
            return;
        }

        if (arrangement.Phrases.Count > MaxPhrases)
        {
            sink.Add(ExportFindingSeverity.Error, KeyTooManyPhrases, null,
                arrangement.Phrases.Count.ToString());
        }

        // A phrase is USED if an iteration references it — OR if an event
        // point on the timeline names it.
        //
        // Only the second half matches how this app is edited. Nothing in
        // the editor writes `PhraseIterations`: placing a phrase writes
        // `EventPoint.Phrase`, and the id/iteration pair is not built until
        // `PhraseSectionGenerator` runs during export. Meanwhile the GP and
        // EOF importers DO fill `Phrases` (GpArrangementParser:
        // "if (!arr.Phrases.Contains(...)) arr.Phrases.Add(...)") without
        // ever adding an iteration.
        //
        // So an imported chart arrives with a full name list and an empty
        // iteration list, and this loop called every phrase in it unused —
        // "Phrase 'intro' is never used", for phrases visibly placed all
        // over the timeline. It was comparing two lists the editor never
        // maintains while ignoring the one it does.
        var placed = new HashSet<string>(
            arrangement.EventPoints
                .Select(e => e.Phrase)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        for (var phraseId = 0; phraseId < arrangement.Phrases.Count; phraseId++)
        {
            var phraseName = arrangement.Phrases[phraseId];
            if (IsStructuralPhrase(phraseName)) continue;
            var id = phraseId;
            if (iterations.Any(it => it.PhraseId == id)) continue;
            if (!string.IsNullOrWhiteSpace(phraseName) && placed.Contains(phraseName.Trim())) continue;

            sink.Add(ExportFindingSeverity.Warning, KeyEmptyPhrase, null, phraseName);
        }

        if (iterations.Count == 0) return;

        var firstIterationMs = iterations.Min(it => it.Position).GetPositionMs(beats);
        var firstContentMs = ContentTimesMs(arrangement, beats).DefaultIfEmpty(double.MaxValue).Min();
        if (firstContentMs < firstIterationMs - 0.5)
            sink.Add(ExportFindingSeverity.Error, KeyFirstPhraseNotAtStart, firstContentMs);
    }

    private static void CheckTones(ChartProject project, Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        var (toneBase, slots) = RsToneSlots.Resolve(arrangement);
        if (string.IsNullOrWhiteSpace(toneBase))
            sink.Add(ExportFindingSeverity.Warning, KeyMissingBaseTone);

        var distinctTones = arrangement.ToneChanges
            .Select(t => t.ToneName)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        if (distinctTones.Count > 4)
            sink.Add(ExportFindingSeverity.Error, KeyTooManyTones, null, distinctTones.Count.ToString());

        var unslotted = arrangement.ToneChanges.FirstOrDefault(change =>
            string.IsNullOrWhiteSpace(change.ToneName) || !slots.Contains(change.ToneName));
        if (unslotted != null)
        {
            sink.Add(ExportFindingSeverity.Warning, KeyToneWithoutSlot,
                unslotted.Position.GetPositionMs(beats), unslotted.ToneName);
        }

        // A declared tone without a project-owned DEFINITION still exports
        // (name only) but plays as the game's default tone — flag each one
        // so the charter knows to author or import the body.
        foreach (var missing in MissingToneDefinitions(project, toneBase, slots))
            sink.Add(ExportFindingSeverity.Warning, KeyToneNotDefined, null, missing);
    }

    // Six strings, or a 7-string GUITAR through the rs7string mod's layout and its
    // RS7X block (Rs7StringLayout): that one ships, with a warning naming
    // the mod. An 8-string guitar is REFUSED until the mod plays one
    // (Rs7StringLayout.ExportRefusal, which the SNG mapper enforces too).
    private static void CheckFretsAndStrings(Arrangement arrangement, IList<BeatMarker> beats, ArrangementFindings sink)
    {
        var maxStringIndex = SngStringLimits.MaxNoteStringIndex(arrangement);
        var strings = arrangement.StringCount.ToString(CultureInfo.InvariantCulture);
        if (Rs7StringLayout.IsExtendedGuitar(arrangement))
        {
            if (arrangement.StringCount > Rs7StringLayout.ModPlayableStrings)
                sink.Add(ExportFindingSeverity.Error, KeyEightStringMod);
            else
                sink.Add(ExportFindingSeverity.Warning, KeyExtendedRangeGuitar, null, strings);
        }

        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                CheckSound(arrangement, sink, note.Position.GetPositionMs(beats), note, maxStringIndex);

            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                    CheckSound(arrangement, sink, chord.Position.GetPositionMs(beats), chordNote, maxStringIndex);
            }
        }
    }

    /// <summary>
    ///     One note or chord note's fret and string. A NON-ZERO fret below the capo
    ///     is physically unplayable (the capo already stops the string higher up), and
    ///     a sound's pitch derives from max(fret, capo), so the chart says one thing and
    ///     the game plays another; the inspector flags it per sound as L01, so a chord
    ///     note gets the same rule as a single note.
    ///     <para>
    ///         A sound past the strings the package can carry is not merely dropped by
    ///         the SNG mapper — it is FOLDED onto the last one (Math.Clamp), where it
    ///         overwrites that string's real data. Refuse the export instead: an Error
    ///         blocks the dialog (CanExport) and makes the save companion skip, so the
    ///         fold never reaches a package.
    ///     </para>
    /// </summary>
    private static void CheckSound(Arrangement arrangement, ArrangementFindings sink, double timeMs, IGuitarSound sound,
        int maxStringIndex)
    {
        if (sound.Fret is < 0 or > MaxFret)
            sink.Add(ExportFindingSeverity.Error, KeyFretOutOfRange, timeMs, sound.Fret.ToString());
        else if (arrangement.Capo > 0 && sound.Fret != 0 && sound.Fret < arrangement.Capo)
            sink.Add(ExportFindingSeverity.Error, KeyNoteBelowCapo, timeMs, sound.Fret.ToString());

        if (sound.StringIndex < 0 || sound.StringIndex > maxStringIndex)
            sink.Add(ExportFindingSeverity.Error, KeyStringOutOfRange, timeMs, sound.StringIndex.ToString());
    }

    private void ValidateVocals(ChartProject project, VocalTrack track, List<ExportValidationFinding> findings)
    {
        foreach (var phrase in track.Phrases)
        {
            foreach (var lyric in phrase.Lyrics)
            {
                // "-"/"+" markers are appended on export, reserve one byte.
                var byteCount = Encoding.UTF8.GetByteCount(lyric.Text.TrimEnd(' ')) + 1;
                if (byteCount > MaxLyricBytes)
                {
                    findings.Add(new ExportValidationFinding(
                    track.Name,
                    ExportFindingSeverity.Error,
                    KeyLyricTooLong,
                    lyric.Position.GetPositionMs(project.Beats),
                    lyric.Text));
                }
            }
        }
    }

    // ---- tone definitions ----------------------------------------------------

    /// <summary>Referenced tone keys (base + slots) that have no matching
    /// <see cref="ChartProject.ToneDefinitions"/> entry. Matching is trim- and
    /// case-tolerant like the in-game-tone coordinator's key normalization.</summary>
    private static IEnumerable<string> MissingToneDefinitions(
        ChartProject project, string toneBase, string[] slots)
    {
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tone in project.ToneDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(tone.Key)) defined.Add(tone.Key.Trim());
            if (!string.IsNullOrWhiteSpace(tone.Name)) defined.Add(tone.Name.Trim());
        }

        return slots.Prepend(toneBase)
            .Where(tone => !string.IsNullOrWhiteSpace(tone))
            .Select(tone => tone.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tone => !defined.Contains(tone));
    }

    /// <summary>Counts the referenced tone keys across every instrumental
    /// arrangement and how many have a project-owned definition body — the
    /// "defined vs missing" summary shown next to the tone tooling.</summary>
    public static (int Referenced, int Defined) CountToneDefinitions(ChartProject project)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arrangement in project.Arrangements)
        {
            if (arrangement.ArrangementType == ArrangementType.Vocal) continue;
            if (arrangement.ToneChanges.Count == 0 &&
                string.IsNullOrWhiteSpace(arrangement.ToneBase) &&
                string.IsNullOrWhiteSpace(arrangement.StartingTone))
            {
                continue;
            }

            var (toneBase, slots) = RsToneSlots.Resolve(arrangement);
            foreach (var tone in slots.Prepend(toneBase))
            {
                if (!string.IsNullOrWhiteSpace(tone))
                    referenced.Add(tone.Trim());
            }

            foreach (var tone in MissingToneDefinitions(project, toneBase, slots))
                missing.Add(tone);
        }

        return (referenced.Count, referenced.Count - missing.Count);
    }

    private static bool IsStructuralPhrase(string name)
    {
        return name.Equals("COUNT", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("END", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("mover", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<double> ContentTimesMs(Arrangement arrangement, IList<BeatMarker> beats)
    {
        return arrangement.Levels.SelectMany(level =>
            level.Notes.Select(n => n.Position.GetPositionMs(beats))
                .Concat(level.Chords.Select(c => c.Position.GetPositionMs(beats))));
    }

    private static double? FirstContentTimeMs(ChartLevel level, IList<BeatMarker> beats)
    {
        var times = level.Notes.Select(n => n.Position.GetPositionMs(beats))
            .Concat(level.Chords.Select(c => c.Position.GetPositionMs(beats)))
            .ToList();
        return times.Count > 0 ? times.Min() : null;
    }
}
