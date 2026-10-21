using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>
/// Writes a <see cref="ChartProject"/> (single arrangement) to an RS-XML
/// <c>&lt;song&gt;</c> document. Element/attribute order mirrors
/// rscharter's <c>SongArrangement</c> XStream field declaration order.
/// </summary>
internal sealed class RsXmlSongWriter
{
    /// <summary>
    ///     Whether a two-string shape may carry a chord name in the written XML.
    ///     <para>
    ///         <b>This is an export-boundary strip, and that is deliberate.</b> A
    ///         name can reach a template from an import, from a paste, or from a
    ///         chart authored before the suppression existed — so turning the
    ///         setting on has to affect what SHIPS, not only what the namer mints
    ///         next. It is the same shape as <c>LinkLandingBreakup</c>: the chart
    ///         keeps what the charter authored and the writer emits what the format
    ///         should say.
    ///     </para>
    ///     <para>
    ///         Defaults to <see cref="DoubleStopNameOptions.Default" />, which is
    ///         what <c>AppConfig.SuppressDoubleStopChordNames</c> ships as — so a
    ///         caller that does not thread the setting still writes the shipped
    ///         behaviour rather than a third one.
    ///     </para>
    /// </summary>
    public DoubleStopNameOptions ChordNameOptions { get; init; } = DoubleStopNameOptions.Default;

    /// <summary>
    /// Writes a 7-/8-string guitar as a plain six-string arrangement: the notes
    ///     on the extra strings are dropped, no template keeps an extra column and neither
    ///     <c>strings</c> nor <c>string6+</c> is written. For the tools that cannot read
    ///     the spec XML — EOF aborts on a <c>string</c> above 5 and DLC Builder's SNG
    ///     build indexes six-wide arrays. Six-string guitars and basses are unaffected.
    /// </summary>
    public bool SixStringProjection { get; init; }

    /// <summary>
    ///     Writes a phrase that starts on a note between two beats as moveR&lt;N&gt; on the
    ///     beat before it (<see cref="RsXmlPhraseMover.ToMoverForm" />), for tools that
    ///     expect that form. Off: the real time, which DLC Builder accepts either way.
    /// </summary>
    public bool MoverPhrases { get; init; }

    /// <summary>What the last <see cref="Write(ChartProject, double?)" /> dropped when
    /// <see cref="SixStringProjection" /> applied to it; null otherwise.</summary>
    public SixStringLoss? LastLoss { get; private set; }

    public XDocument Write(ChartProject project) => Write(project, null);

    /// <param name="project">
    /// The project to write. Only <c>Arrangements[0]</c> is exported — callers
    /// wrap one arrangement per synthetic project to emit a multi-arrangement
    /// bundle.
    /// </param>
    /// <param name="audioLengthMs">
    /// Length of the song's audio, when the caller knows it. EOF writes
    /// <c>songLength</c> as <c>min(chart length, audio length) - 1 ms</c>
    /// (EOF_SRC/rs.c:590-593 + :1762) so the chart always reaches an end
    /// in-game. <see cref="ChartProject"/> carries no audio duration (it lives
    /// on the service-layer <c>ProjectState.TotalAudioLengthMs</c>), so callers
    /// that have it should pass it; omitting it falls back to the chart's own
    /// length, which is exactly EOF's no-audio-loaded branch.
    /// </param>
    public XDocument Write(ChartProject project, double? audioLengthMs)
    {
        var chartArr = project.Arrangements.Count > 0 ? project.Arrangements[0] : new Arrangement();
        // A 7-/8-string guitar is spelled in the rs7string layout (indices 0..5 = E A D G B e,
        // then the extra strings) with its chords projected to the package's rules, so the
        // XML and the SNG built from one chart agree. Everything below reads this copy.
        var projection = Rs7Projection.TryCreate(chartArr, sixStringOnly: SixStringProjection);
        LastLoss = projection?.Loss;
        var arr = projection?.Arrangement ?? chartArr;
        var beats = project.Beats;
        // The levels as the FILE shape says them: a level above a phrase's max
        // leaves that phrase out (the chart keeps a copy there so its hardest
        // level is the whole song — see RsLevelLadder). Every level read below
        // goes through this list, and the SNG mapper reads the same one.
        var ladder = RsLevelLadder.ForExport(arr);
        var phraseData = BuildPhraseExportData(arr, beats, ladder.Levels);
        // The SAME resolver the psarc manifest uses. A private,
        // SET-ONLY derivation into a local clone would promote an authored 0 to
        // 1 and never clear it, and the manifest (which derives
        // nothing) would disagree with the XML built from the very same chart.
        // Derived from the CHART, whose string order the deriver's string-position rules
        // (power chords, drop-D) assume. Stock Rocksmith has no seven-string standard, so
        // such a tuning is never standard (spec 2).
        // The six-string copy is a plain six-string arrangement (its stock strings, in the
        // order the deriver assumes), so it derives from itself.
        var arrangementProperties = ArrangementPropertiesDeriver.Derive(LastLoss is null ? chartArr : arr);
        if (!ReferenceEquals(arr, chartArr) && LastLoss is null) arrangementProperties.StandardTuning = 0;

        var song = new XElement(E.Song, new XAttribute(E.Version, 8));

        // ── header ──────────────────────────────────────────────────────
        song.Add(new XElement(E.Title, project.Chart.SongName));
        song.Add(new XElement(E.Arrangement, ArrangementTypeName(arr.ArrangementType)));
        song.Add(new XElement(E.Part, 1));

        // offset / centOffset / songLength / lastConversionDateTime / startBeat
        var firstBeatMs = beats.Count > 0 ? TimelineMath.ToWholeMs(beats[0].Time) : 0;
        var lastBeatMs = beats.Count > 0 ? TimelineMath.ToWholeMs(beats[^1].Time) : 0;
        song.Add(new XElement(E.Offset, Fmt.Time(-firstBeatMs)));
        song.Add(new XElement(E.CentOffset, ((int)Math.Round(arr.CentOffset)).ToString(CultureInfo.InvariantCulture)));
        song.Add(new XElement(E.SongLength, Fmt.Time(SongLengthMs(arr, beats, lastBeatMs, audioLengthMs))));
        song.Add(new XElement(E.LastConversionDateTime, Fmt.DateTime(DateTime.Now)));
        song.Add(new XElement(E.StartBeat, Fmt.Time(firstBeatMs)));

        // averageTempo
        var avgTempo = AverageTempo(beats);
        song.Add(new XElement(E.AverageTempo, Fmt.BigDecimal(avgTempo, 3)));

        // tuning
        song.Add(WriteTuning(arr));
        song.Add(new XElement(E.Capo, arr.Capo));

        // artist / album
        var artistName = FirstNonBlank(arr.ArtistName, project.Chart.Artist, "Unknown Artist");
        var artistNameSort = FirstNonBlank(arr.ArtistNameSort, project.Chart.ArtistSort, artistName);
        song.Add(new XElement(E.ArtistName, artistName));
        song.Add(new XElement(E.ArtistNameSort, artistNameSort));
        // Blank-aware like the artist path above: a raw Length check let the
        // old 19-space ChartData.AlbumName sentinel through verbatim.
        song.Add(new XElement(E.AlbumName, FirstNonBlank(arr.AlbumName, project.Chart.AlbumName)));
        // EOF always emits <albumYear>, even when empty.
        if (arr.AlbumYear.HasValue)
            song.Add(new XElement(E.AlbumYear, arr.AlbumYear.Value));
        else if (!string.IsNullOrWhiteSpace(project.Chart.AlbumYear))
            song.Add(new XElement(E.AlbumYear, project.Chart.AlbumYear));
        else
            song.Add(new XElement(E.AlbumYear, string.Empty));

        song.Add(new XElement(E.CrowdSpeed, 1));

        // arrangementProperties
        song.Add(WriteArrangementProperties(arrangementProperties));

        // Tone names (header tone slots). Resolved through the SAME helper the SNG
        // export uses, so the two artifacts built from one chart agree. Writing the
        // A-D slots raw dropped everything an importer had put in StartingTone /
        // ToneNames — EofProGuitarTrackParser fills those and the ToneChanges but
        // never the slots, so an EOF-imported arrangement exported <tone> changes
        // referencing names declared in no <tonebase>/<tonea> element at all.
        var (toneBase, toneSlots) = RsToneSlots.Resolve(arr);
        WriteNotEmpty(song, E.Tonebase, toneBase);
        WriteNotEmpty(song, E.Tonea, toneSlots.Length > 0 ? toneSlots[0] : arr.ToneA);
        WriteNotEmpty(song, E.Toneb, toneSlots.Length > 1 ? toneSlots[1] : arr.ToneB);
        WriteNotEmpty(song, E.Tonec, toneSlots.Length > 2 ? toneSlots[2] : arr.ToneC);
        WriteNotEmpty(song, E.Toned, toneSlots.Length > 3 ? toneSlots[3] : arr.ToneD);

        // phrases — the chart keeps the charter's names; Rocksmith gets a legal,
        // unique spelling of each (RsPhraseName). PhraseInfos stay keyed by the
        // AUTHORED name, so the lookup uses it and only the attribute is rewritten.
        var exportedPhraseNames = RsPhraseName.SanitizeAll(phraseData.Phrases);
        song.Add(Fmt.CountedList(E.Phrases, Enumerable.Range(0, phraseData.Phrases.Count).ToList(),
            i => WritePhrase(exportedPhraseNames[i],
                phraseData.PhraseInfos.TryGetValue(phraseData.Phrases[i], out var pi) ? pi : null)));

        // phraseIterations
        song.Add(Fmt.CountedList(E.PhraseIterations, phraseData.Iterations,
            pi => WritePhraseIteration(TimelineMath.ToWholeMs(pi.Position.GetPositionMs(beats)), pi.PhraseId)));

        // newLinkedDiffs / linkedDiffs (always empty - required by RS toolkit)
        song.Add(new XElement(E.NewLinkedDiffs, new XAttribute(E.Count, 0)));
        song.Add(new XElement(E.LinkedDiffs, new XAttribute(E.Count, 0)));

        // phraseProperties (always empty)
        song.Add(new XElement(E.PhraseProperties, new XAttribute(E.Count, 0)));

        // chordTemplates. The list that gets EMITTED, not the domain collection:
        // the RS-XML `chordId` attribute is a positional INDEX into this list
        // (RsXmlSongReader assigns `Id = id++` while walking it), so every
        // reference below is resolved through the map built here.
        var chordTemplates = BuildChordTemplateExport(arr, ladder.Levels);
        song.Add(Fmt.CountedList(E.ChordTemplates, chordTemplates.Templates,
            ct => WriteChordTemplate(ct, arr, ChordNameOptions)));

        // fretHandMuteTemplates
        var fretHandMuteTemplates = arr.FretHandMuteTemplates.ToList();
        song.Add(Fmt.CountedList(E.FretHandMuteTemplates, fretHandMuteTemplates,
            ct => WriteChordTemplate(ct, arr, ChordNameOptions)));

        // ebeats
        song.Add(WriteEbeats(beats));

        // tone changes (EOF places this AFTER ebeats)
        var toneChanges = arr.ToneChanges.ToList();
        song.Add(Fmt.CountedList(E.Tones, toneChanges,
            tc => WriteTone(TimelineMath.ToWholeMs(tc.Position.GetPositionMs(beats)), tc.ToneName)));

        // sections (EventPoints with Section != ""), plus EOF's closing noguitar
        song.Add(WriteSections(arr, beats));

        // events (EventPoints with Events != "")
        song.Add(WriteEvents(arr, beats));

        // transcriptionTrack (always empty)
        song.Add(WriteTranscriptionTrack());

        // levels
        var levels = ladder.Levels;
        var phraseStartMs = phraseData.Iterations
            .Select(pi => TimelineMath.ToWholeMs(pi.Position.GetPositionMs(beats)))
            .ToList();
        song.Add(Fmt.CountedList(E.Levels, levels,
            lvl => WriteLevel(lvl, beats, chordTemplates, phraseStartMs)));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), song);
        if (MoverPhrases) RsXmlPhraseMover.ToMoverForm(doc);
        return doc;
    }

    public void WriteTo(ChartProject project, string filePath) => WriteTo(project, filePath, null);

    /// <inheritdoc cref="Write(ChartProject, double?)"/>
    public void WriteTo(ChartProject project, string filePath, double? audioLengthMs)
    {
        var doc = Write(project, audioLengthMs);
        using var writer = XmlWriter.Create(filePath, Fmt.GetWriterSettings());
        doc.WriteTo(writer);
    }

    // ── Header helpers ───────────────────────────────────────────────────

    private static string ArrangementTypeName(ArrangementType type) => type switch
    {
        ArrangementType.Lead => "Lead",
        ArrangementType.Rhythm => "Rhythm",
        ArrangementType.Bass => "Bass",
        ArrangementType.Combo => "Combo",
        ArrangementType.Vocal => "Vocal",
        _ => "Lead"
    };

    /// <summary>
    ///     The chart-wide average tempo, DERIVED from the beat grid.
    ///     <para>
    ///         <b>Internal because the psarc manifest has to call it.</b>
    ///         <c>RsPackageMetadataFactory.Create</c> must not read
    ///         <c>ChartProject.Bpm</c> — the NOMINAL/initial tempo — into
    ///         <c>SongAverageTempo</c>: on any tempo-mapped chart the game's
    ///         song list would disagree with the <c>&lt;averageTempo&gt;</c> in the XML
    ///         built from the very same chart. Same class as the <c>songLength</c>
    ///         rule: a chart-wide value is resolved from the CHART, never read off
    ///         ambient state.
    ///     </para>
    /// </summary>
    internal static double AverageTempo(IList<BeatMarker> beats)
    {
        if (beats.Count < 2) return 120.0;
        var span = beats[beats.Count - 1].Time - beats[0].Time;
        if (span <= 0) return 120.0;
        // N beats span N-1 intervals. Dividing by N stretched the mean beat
        // length and under-reported the tempo. (EOF has the same off-by-one;
        // it is not mirrored here on purpose.)
        return 60000.0 / (span / (beats.Count - 1));
    }

    /// <summary>
    /// EOF: <c>songLength = min(chart length, audio length) - 1 ms</c>
    /// (EOF_SRC/rs.c:590-593 picks the chart length only when it is shorter
    /// than the audio or no audio is loaded; :1762 subtracts the 1 ms so the
    /// chart always reaches an end in-game). The chart's own length is the
    /// later of the last beat and the last sound that has to still fit.
    /// </summary>
    private static int SongLengthMs(Arrangement arr, IList<BeatMarker> beats, int lastBeatMs, double? audioLengthMs)
    {
        double chartLengthMs = lastBeatMs;
        foreach (var level in arr.Levels)
        {
            foreach (var note in level.Notes)
                chartLengthMs = Math.Max(chartLengthMs, note.EndPosition.GetPositionMs(beats));

            foreach (var chord in level.Chords)
                chartLengthMs = Math.Max(chartLengthMs, chord.EndPosition.GetPositionMs(beats));
        }

        var endMs = audioLengthMs is { } audio && audio > 0
            ? Math.Min(chartLengthMs, audio)
            : chartLengthMs;

        return Math.Max(0, TimelineMath.ToWholeMs(endMs) - 1);
    }

    /// <summary>
    ///     The same <see cref="RsChordTemplateExport" /> table
    ///     <c>SngFromDomainMapper.BuildChordTemplates</c> builds, so the XML and
    ///     the SNG built from one chart cannot describe different shapes: the
    ///     arrangement's own templates first (minus the ones only split chords
    ///     play), then a synthesized template per chord whose id resolves to
    ///     nothing, reusing an identical earlier template when there is one.
    /// </summary>
    private static RsChordTemplateExport BuildChordTemplateExport(Arrangement arr, IReadOnlyList<ChartLevel> levels) =>
        // Level order matches the SNG mapper's OrderedLevels, so the synthesized
        // templates land in the same relative order in both artifacts. The RAW
        // level collections are scanned (not the LinkLandingBreakup projection),
        // again because that is what the SNG mapper scans.
        RsChordTemplateExport.Build(arr.ChordTemplates, levels.OrderBy(l => l.DifficultyIndex).Select(l => l.Chords),
            levels.SelectMany(l => l.HandShapes));

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static PhraseExportData BuildPhraseExportData(Arrangement arr, IList<BeatMarker> beats,
        IReadOnlyList<ChartLevel> levels)
    {
        var explicitPhrases = arr.Phrases.ToList();
        var explicitIterations = arr.PhraseIterations.ToList();
        if (explicitPhrases.Count > 0 && explicitIterations.Count > 0)
        {
            // Both phrase stores are live at once — the rscp reader fills
            // Phrases/PhraseIterations while the timeline and song-overview editors
            // author phrases as EventPoint.Phrase. Returning the explicit lists
            // unchanged silently discarded every phrase added in the editor: it
            // never occurred in the exported song at all.
            var (mergedPhrases, mergedIterations) =
                MergeEditorPhraseMarkers(arr, explicitPhrases, explicitIterations);
            return new PhraseExportData(mergedPhrases, mergedIterations,
                BuildPhraseInfoMap(arr, levels, mergedPhrases, mergedIterations));
        }

        var markers = arr.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Phrase))
            .OrderBy(ep => ep.Position)
            .Select(ep => (ep.Position, ep.Phrase.Trim()))
            .ToList();

        if (markers.Count == 0)
            return new PhraseExportData(explicitPhrases, explicitIterations, BuildPhraseInfoMap(arr, levels, explicitPhrases, explicitIterations));

        if (!markers.Any(m => string.Equals(m.Item2, "COUNT", StringComparison.OrdinalIgnoreCase)))
            markers.Insert(0, (FractionalPosition.Zero, "COUNT"));

        if (!markers.Any(m => string.Equals(m.Item2, "END", StringComparison.OrdinalIgnoreCase)))
            markers.Add((FindEndPhrasePosition(arr, beats), "END"));

        markers = markers
            .OrderBy(m => m.Position)
            .ThenBy(m => PhraseSortKey(m.Item2))
            .ToList();

        var phrases = new List<string>();
        var phraseIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in markers)
        {
            if (phraseIds.ContainsKey(marker.Item2)) continue;
            phraseIds[marker.Item2] = phrases.Count;
            phrases.Add(marker.Item2);
        }

        var iterations = markers
            .Select(marker => new PhraseIteration(marker.Position, phraseIds[marker.Item2]))
            .ToList();

        return new PhraseExportData(phrases, iterations, BuildPhraseInfoMap(arr, levels, phrases, iterations));
    }

    /// <summary>
    ///     Adds an iteration for every <see cref="EventPoint.Phrase" /> marker that no
    ///     explicit iteration already covers, minting a phrase name only when it is new.
    ///     <para>
    ///         Deliberately ADDITIVE rather than a rebuild: when a chart came from
    ///         RS-XML both stores describe the same phrases, every marker matches an
    ///         existing iteration by position and this is a no-op — so an imported
    ///         chart round-trips byte-identically. Only a marker the explicit list does
    ///         not account for, i.e. one the user authored in the editor, adds anything.
    ///     </para>
    /// </summary>
    private static (List<string> Phrases, List<PhraseIteration> Iterations) MergeEditorPhraseMarkers(
        Arrangement arr, List<string> phrases, List<PhraseIteration> iterations)
    {
        var covered = new HashSet<FractionalPosition>(iterations.Select(i => i.Position));
        var extra = arr.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Phrase) && !covered.Contains(ep.Position))
            .OrderBy(ep => ep.Position)
            .ToList();

        if (extra.Count == 0) return (phrases, iterations);

        var mergedPhrases = new List<string>(phrases);
        var idByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mergedPhrases.Count; i++) idByName.TryAdd(mergedPhrases[i], i);

        var mergedIterations = new List<PhraseIteration>(iterations);
        foreach (var point in extra)
        {
            var name = point.Phrase.Trim();
            if (!idByName.TryGetValue(name, out var id))
            {
                id = mergedPhrases.Count;
                mergedPhrases.Add(name);
                idByName[name] = id;
            }

            mergedIterations.Add(new PhraseIteration(point.Position, id));
        }

        mergedIterations.Sort((a, b) => a.Position.CompareTo(b.Position));
        return (mergedPhrases, mergedIterations);
    }

    private static int PhraseSortKey(string phraseName)
    {
        if (string.Equals(phraseName, "COUNT", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(phraseName, "END", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    private static FractionalPosition FindEndPhrasePosition(Arrangement arr,
        IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return FractionalPosition.Zero;
        return EndNoGuitarSection.BeatAfterContent(arr, beats) ?? new FractionalPosition(beats.Count - 1);
    }

    private static Dictionary<string, PhraseInfo> BuildPhraseInfoMap(Arrangement arr, IReadOnlyList<ChartLevel> levels,
        IReadOnlyList<string> phrases, IReadOnlyList<PhraseIteration> iterations)
    {
        var infos = new Dictionary<string, PhraseInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in phrases)
        {
            if (arr.PhraseInfos.TryGetValue(phrase, out var existing))
                infos[phrase] = new PhraseInfo(existing.MaxDifficulty, existing.Solo);
            else
                infos[phrase] = new PhraseInfo();
        }

        for (var i = 0; i < iterations.Count; i++)
        {
            var phraseName = phrases[iterations[i].PhraseId];
            var start = iterations[i].Position;
            var end = i + 1 < iterations.Count ? iterations[i + 1].Position : start;
            var maxDifficulty = FindMaxDifficultyInRange(levels, start, end);
            if (!infos.TryGetValue(phraseName, out var info))
            {
                infos[phraseName] = new PhraseInfo(maxDifficulty, false);
                continue;
            }

            if (maxDifficulty > info.MaxDifficulty)
                info.MaxDifficulty = maxDifficulty;
        }

        return infos;
    }

    private static int FindMaxDifficultyInRange(IReadOnlyList<ChartLevel> levels,
        FractionalPosition start,
        FractionalPosition end)
    {
        var maxDifficulty = 0;
        foreach (var level in levels)
        {
            if (LevelHasContentInRange(level, start, end))
                maxDifficulty = Math.Max(maxDifficulty, level.DifficultyIndex);
        }

        return maxDifficulty;
    }

    private static bool LevelHasContentInRange(ChartLevel level,
        FractionalPosition start,
        FractionalPosition end)
    {
        foreach (var note in level.Notes)
        {
            if (note.Position >= start && note.Position < end)
                return true;
        }

        foreach (var chord in level.Chords)
        {
            if (chord.Position >= start && chord.Position < end)
                return true;
        }

        return false;
    }

    private sealed record PhraseExportData(
        List<string> Phrases,
        List<PhraseIteration> Iterations,
        Dictionary<string, PhraseInfo> PhraseInfos);

    private static XElement WriteTuning(Arrangement arr)
    {
        var el = new XElement(E.Tuning);
        var count = arr.StringCount > 0 ? arr.StringCount : 6;
        // EOF emits exactly string0..string5 (always 6, regardless of bass vs guitar).
        // For arrangements with more strings, emit additional stringN attrs to
        // preserve data; but the canonical 6 must always be present.
        var emit = Math.Max(6, count);
        // The reader honours <tuning strings="N"> (RsXmlSongConstants.Strings) and
        // the writer never emitted it, so a 4- or 5-string bass round-tripped as a
        // 6-string and everything derived from StringCount — tuning UI, refret, FHP
        // width — was wrong afterwards. Only written when it is not the canonical 6,
        // so a normal guitar arrangement stays byte-identical.
        if (count != 6) el.Add(new XAttribute(E.Strings, count));
        // Through TuningOffsetResolution, not TuningPitches directly: an
        // arrangement carrying only note LETTERS would export as E standard.
        var offsets = TuningOffsetResolution.Resolve(arr);
        for (var i = 0; i < emit; i++)
        {
            var step = i < offsets.Count ? offsets[i] : 0;
            el.Add(new XAttribute($"string{i}", step));
        }
        return el;
    }

    private static XElement WriteArrangementProperties(ArrangementProperties p)
    {
        var el = new XElement(E.ArrangementProperties);
        el.Add(new XAttribute(E.Represent, p.Represent));
        el.Add(new XAttribute(E.BonusArr, p.BonusArr));
        el.Add(new XAttribute(E.StandardTuning, p.StandardTuning));
        el.Add(new XAttribute(E.NonStandardChords, p.NonStandardChords));
        el.Add(new XAttribute(E.BarreChords, p.BarreChords));
        el.Add(new XAttribute(E.PowerChords, p.PowerChords));
        el.Add(new XAttribute(E.DropDPower, p.DropDPower));
        el.Add(new XAttribute(E.OpenChords, p.OpenChords));
        el.Add(new XAttribute(E.FingerPicking, p.FingerPicking));
        el.Add(new XAttribute(E.PickDirection, p.PickDirection));
        el.Add(new XAttribute(E.DoubleStops, p.DoubleStops));
        el.Add(new XAttribute(E.PalmMutes, p.PalmMutes));
        el.Add(new XAttribute(E.Harmonics, p.Harmonics));
        el.Add(new XAttribute(E.PinchHarmonics, p.PinchHarmonics));
        el.Add(new XAttribute(E.Hopo, p.Hopo));
        el.Add(new XAttribute(E.Tremolo, p.Tremolo));
        el.Add(new XAttribute(E.Slides, p.Slides));
        el.Add(new XAttribute(E.UnpitchedSlides, p.UnpitchedSlides));
        el.Add(new XAttribute(E.Bends, p.Bends));
        el.Add(new XAttribute(E.Tapping, p.Tapping));
        el.Add(new XAttribute(E.Vibrato, p.Vibrato));
        el.Add(new XAttribute(E.FretHandMutes, p.FretHandMutes));
        el.Add(new XAttribute(E.SlapPop, p.SlapPop));
        el.Add(new XAttribute(E.TwoFingerPicking, p.TwoFingerPicking));
        el.Add(new XAttribute(E.FifthsAndOctaves, p.FifthsAndOctaves));
        el.Add(new XAttribute(E.Syncopation, p.Syncopation));
        el.Add(new XAttribute(E.BassPick, p.BassPick));
        el.Add(new XAttribute(E.Sustain, p.Sustain));
        el.Add(new XAttribute(E.PathLead, p.PathLead));
        el.Add(new XAttribute(E.PathRhythm, p.PathRhythm));
        el.Add(new XAttribute(E.PathBass, p.PathBass));
        return el;
    }

    private static void WriteNotEmpty(XElement parent, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            parent.Add(new XElement(name, value));
    }

    private static XElement WriteTone(int timeMs, string name)
    {
        var el = new XElement(E.Tone);
        el.Add(new XAttribute(E.Time, Fmt.Time(timeMs)));
        el.Add(new XAttribute(E.Name, name));
        return el;
    }

    private static XElement WritePhrase(string name, PhraseInfo? info)
    {
        var el = new XElement(E.Phrase);
        el.Add(new XAttribute(E.Name, name));
        el.Add(new XAttribute(E.MaxDifficulty, info?.MaxDifficulty ?? 0));
        if (info?.Solo == true)
            el.Add(new XAttribute(E.Solo, 1));
        return el;
    }

    private static XElement WritePhraseIteration(int timeMs, int phraseId)
    {
        var el = new XElement(E.PhraseIteration);
        el.Add(new XAttribute(E.Time, Fmt.Time(timeMs)));
        el.Add(new XAttribute(E.PhraseId, phraseId));
        return el;
    }

    /// <summary>
    ///     RS2014's <c>vibrato</c> attribute is a STRENGTH (40/80/120), not a flag.
    ///     Writing a bare <c>1</c> made the XML and the SNG built from the same chart
    ///     disagree on every vibrato — the SNG export has always used this same
    ///     <c>Math.Max(strength, 80)</c>.
    /// </summary>
    private static int VibratoStrength(IGuitarSound sound) => Math.Max(sound.VibratoStrength, 80);

    private static XElement WriteChordTemplate(ChordTemplate ct, Arrangement arr, DoubleStopNameOptions options)
    {
        var el = new XElement(E.ChordTemplate);

        // A two-string shape ships unnamed when the suppression is on, however the
        // name got there. The "-arp" suffix is derived from the name below, so the
        // strip has to happen BEFORE it, or an arpeggio double stop would export as
        // the bare marker "-arp".
        // The -nop suffix comes from the NopInRs FIELD and never ships in
        // chordName, which is the name the game prints.
        var (exportName, nop) = RsChordDisplaySuffix.ForExport(ct);
        var name = ChordNameSuggester.SuppressesAutoName(arr, ct.Frets, options) ? "" : exportName;

        el.Add(new XAttribute(E.ChordName, name));
        // Both flags mean "render this as an arpeggio" and the SNG export already
        // folds them together (SngFromDomainMapper). Honouring only ForceArpeggioInRs
        // dropped every template the chord-template editor or the EOF importer marked
        // Arpeggio, and made the XML disagree with the SNG built from the same chart.
        var displayName = RsChordDisplaySuffix.DisplayName(name, ct.Arpeggio || ct.ForceArpeggioInRs, nop);
        el.Add(new XAttribute(E.DisplayName, displayName));

        // DLC Builder expects only numeric finger attributes. Missing fingers are
        // represented by omitting the attribute entirely.
        foreach (var kvp in ct.Fingers
                     .Where(kvp => ChordTemplate.FingerNames.ContainsKey(kvp.Value))
                     .OrderBy(kvp => kvp.Key))
        {
            el.Add(new XAttribute($"finger{kvp.Key}", kvp.Value));
        }

        // fret{i} for each string in Frets, sorted
        foreach (var kvp in ct.Frets.OrderBy(k => k.Key))
            el.Add(new XAttribute($"fret{kvp.Key}", kvp.Value));

        return el;
    }

    private static XElement WriteEbeats(IList<BeatMarker> beats)
    {
        var el = new XElement(E.Ebeats);
        el.Add(new XAttribute(E.Count, beats.Count));
        var measureNumber = 1;
        for (var i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            var eb = new XElement(E.Ebeat);
            eb.Add(new XAttribute(E.Time, Fmt.Time(TimelineMath.ToWholeMs(beat.Time))));
            if (beat.IsMeasure)
                eb.Add(new XAttribute(E.Measure, measureNumber++));
            el.Add(eb);
        }
        return el;
    }

    private static XElement WriteSections(Arrangement arr, IList<BeatMarker> beats)
    {
        var sectionPoints = arr.EventPoints
            .Where(ep => !string.IsNullOrEmpty(ep.Section))
            .OrderBy(ep => ep.Position)
            .Select(ep => (Name: ep.Section, Ms: ep.Position.GetPositionMs(beats)))
            .ToList();
        if (EndNoGuitarSection.StartMs(arr, beats) is { } noGuitarMs)
            sectionPoints = [.. sectionPoints.Append((EndNoGuitarSection.Name, Ms: noGuitarMs)).OrderBy(s => s.Ms)];

        var numberBySectionName = new Dictionary<string, int>();
        var sectionElements = new List<XElement>();
        foreach (var ep in sectionPoints)
        {
            var sName = ep.Name;
            numberBySectionName.TryGetValue(sName, out var num);
            num++;
            numberBySectionName[sName] = num;

            var sEl = new XElement(E.Section);
            sEl.Add(new XAttribute(E.Name, sName));
            sEl.Add(new XAttribute(E.Number, num));
            sEl.Add(new XAttribute(E.StartTime, Fmt.Time(TimelineMath.ToWholeMs(ep.Ms))));
            sectionElements.Add(sEl);
        }

        var el = new XElement(E.Sections);
        el.Add(new XAttribute(E.Count, sectionElements.Count));
        foreach (var se in sectionElements) el.Add(se);
        return el;
    }

    private static XElement WriteEvents(Arrangement arr, IList<BeatMarker> beats)
    {
        var events = new List<(int TimeMs, string Code)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static void AddEvent(List<(int TimeMs, string Code)> list, HashSet<string> seenEvents, int timeMs, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var normalizedCode = code.Trim();
            var key = $"{timeMs}:{normalizedCode}";
            if (!seenEvents.Add(key)) return;
            list.Add((timeMs, normalizedCode));
        }

        foreach (var ep in arr.EventPoints.OrderBy(ep => ep.Position))
        {
            if (string.IsNullOrEmpty(ep.Events)) continue;
            var timeMs = TimelineMath.ToWholeMs(ep.Position.GetPositionMs(beats));
            foreach (var code in ep.Events.Split(',', StringSplitOptions.RemoveEmptyEntries))
                AddEvent(events, seen, timeMs, code);
        }

        int? previousNumerator = null;
        int? previousDenominator = null;
        foreach (var beat in beats)
        {
            var numerator = beat.BeatsInMeasure > 0 ? beat.BeatsInMeasure : 4;
            var denominator = beat.NoteDenominator > 0 ? beat.NoteDenominator : 4;
            if (previousNumerator == numerator && previousDenominator == denominator)
                continue;

            AddEvent(events, seen, TimelineMath.ToWholeMs(beat.Time), $"TS:{numerator}/{denominator}");
            previousNumerator = numerator;
            previousDenominator = denominator;
        }

        var eventList = events
            .OrderBy(ev => ev.TimeMs)
            .ThenBy(ev => ev.Code, StringComparer.Ordinal)
            .Select(ev =>
            {
                var evEl = new XElement(E.Event);
                evEl.Add(new XAttribute(E.Time, Fmt.Time(ev.TimeMs)));
                evEl.Add(new XAttribute(E.Code, ev.Code));
                return evEl;
            })
            .ToList();

        var el = new XElement(E.Events);
        el.Add(new XAttribute(E.Count, eventList.Count));
        foreach (var ev in eventList) el.Add(ev);
        return el;
    }

    private static XElement WriteTranscriptionTrack()
    {
        var el = new XElement(E.TranscriptionTrack);
        el.Add(new XAttribute(E.Difficulty, -1));
        el.Add(new XElement(E.Notes, new XAttribute(E.Count, 0)));
        el.Add(new XElement(E.Chords, new XAttribute(E.Count, 0)));
        el.Add(new XElement(E.Anchors, new XAttribute(E.Count, 0)));
        el.Add(new XElement(E.HandShapes, new XAttribute(E.Count, 0)));
        return el;
    }

    // ── Level ────────────────────────────────────────────────────────────

    private XElement WriteLevel(ChartLevel level, IList<BeatMarker> beats,
        RsChordTemplateExport chordTemplates, IReadOnlyList<int> phraseStartMs)
    {
        var el = new XElement(E.Level);
        el.Add(new XAttribute(E.Difficulty, level.DifficultyIndex));

        // A chord a LinkNext sound lands on is written as loose single notes —
        // RS reads <chord> as a strum and a shape you slid into was never
        // re-picked. Linked chords stay legal to AUTHOR; only the spelling
        // changes here, exactly as EOF does it (EOF_SRC/rs.c:2010). Pure
        // projection: the domain collections are never touched.
        var (notes, chords) = LinkLandingBreakup.Project(level);

        // ── Every list below is ordered by POSITION, defensively.
        //
        // A ChartLevel's collections are NOT sorted mid-session: the highway
        // edit path runs ChartSpacingEnforcer with runCompatibilityCleanup:
        // false, which skips SortByPosition, and RefretService.RefretRange used
        // to splice regenerated anchors onto the END of the list. RS-XML is a
        // TIME-ORDERED format, and BuildAnchorExports below compares CONSECUTIVE
        // rows — on an unsorted list it collapses the wrong pair and mis-answers
        // the phrase-boundary question. SngFromDomainMapper.ConvertLevel has
        // always sorted its entities, anchors and hand shapes, and these two
        // artifacts are built from the same chart, so the XML had to as well.
        // OrderBy is STABLE, so a chart that is already in order (every imported
        // one, and every one that reached a save) is byte-identical.

        // notes
        var noteEls = notes.OrderBy(n => n.Position).Select(n => WriteNote(n, beats)).ToList();
        el.Add(Fmt.CountedList(E.Notes, noteEls, x => x));

        // chords
        var compact = RsChordDensity.Compute(level, beats, chordTemplates.IndexFor);
        // A chord whose template resolves to nothing is DROPPED, exactly as
        // SngFromDomainMapper.ConvertLevel drops it: only a chord with neither a
        // live template nor chord notes can reach this, and writing it as
        // chordId="0" shipped an unrelated shape instead of nothing.
        var chordEls = chords
            .Where(c => chordTemplates.IndexFor(c) >= 0)
            .OrderBy(c => c.Position)
            .Select(c => WriteChord(c, beats, chordTemplates, compact.Contains(c)))
            .ToList();
        el.Add(Fmt.CountedList(E.Chords, chordEls, x => x));

        // fretHandMutes
        var fhmEls = level.FretHandMutes.OrderBy(m => m.Position)
            .Select(m => WriteFretHandMute(m, beats)).ToList();
        el.Add(Fmt.CountedList(E.FretHandMutesEl, fhmEls, x => x));

        // anchors (write-time dedupe — the domain collection is never touched)
        var anchorEls = BuildAnchorExports(level.Anchors.OrderBy(a => a.Position), beats, phraseStartMs)
            .Select(WriteAnchor).ToList();
        el.Add(Fmt.CountedList(E.Anchors, anchorEls, x => x));

        // handShapes
        var hsEls = level.HandShapes.OrderBy(h => h.Position)
            .Select(h => WriteHandShape(h, beats, chordTemplates)).ToList();
        el.Add(Fmt.CountedList(E.HandShapes, hsEls, x => x));

        return el;
    }

    private static XElement WriteNote(GuitarNote note, IList<BeatMarker> beats)
    {
        var el = new XElement(E.Note);
        var timeMs = TimelineMath.ToWholeMs(note.Position.GetPositionMs(beats));
        var endMs = TimelineMath.ToWholeMs(note.EndPosition.GetPositionMs(beats));
        var sustainMs = endMs - timeMs;

        el.Add(new XAttribute(E.Time, Fmt.Time(timeMs)));
        el.Add(new XAttribute(E.String, note.StringIndex));
        el.Add(new XAttribute(E.Fret, note.Fret));
        if (sustainMs > 0) el.Add(new XAttribute(E.Sustain, Fmt.Time(sustainMs)));
        if (note.Vibrato) el.Add(new XAttribute(E.VibratoAttr, VibratoStrength(note)));
        if (note.Tremolo) el.Add(new XAttribute(E.TremoloAttr, 1));
        if (note.Accent) el.Add(new XAttribute(E.Accent, 1));

        // mute
        if (note.Mute == MuteType.Full) el.Add(new XAttribute(E.Mute, 1));
        else if (note.Mute == MuteType.Palm) el.Add(new XAttribute(E.PalmMute, 1));

        // bass picking
        if (note.BassPicking == BassPickingType.Pop) el.Add(new XAttribute(E.Pluck, 1));
        else if (note.BassPicking == BassPickingType.Slap) el.Add(new XAttribute(E.Slap, 1));

        // hopo / tap
        if (note.Hopo == HopoType.HammerOn) { el.Add(new XAttribute(E.HopoAttr, 1)); el.Add(new XAttribute(E.HammerOn, 1)); }
        else if (note.Hopo == HopoType.PullOff) { el.Add(new XAttribute(E.HopoAttr, 1)); el.Add(new XAttribute(E.PullOff, 1)); }
        else if (note.Hopo == HopoType.Tap)
        {
            el.Add(new XAttribute(E.Tap, 1));
        }

        // slide
        if (note.SlideTo.HasValue)
        {
            if (note.UnpitchedSlide) el.Add(new XAttribute(E.SlideUnpitchTo, note.SlideTo.Value));
            else el.Add(new XAttribute(E.SlideTo, note.SlideTo.Value));
        }

        // bend + bendValues
        AddBendAndValues(el, note.BendValues.ToList(), beats);

        // harmonic
        if (note.Harmonic == HarmonicType.Normal) el.Add(new XAttribute(E.Harmonic, 1));
        else if (note.Harmonic == HarmonicType.Pinch) el.Add(new XAttribute(E.HarmonicPinch, 1));

        if (note.LinkNext) el.Add(new XAttribute(E.LinkNext, 1));
        if (note.Ignore) el.Add(new XAttribute(E.Ignore, 1));

        // Absent = not stated, so a chart that never sets them is byte-identical.
        if (note.LeftHandFinger >= 0) el.Add(new XAttribute(E.LeftHand, note.LeftHandFinger));
        if (note.PickDirection != PickDirectionType.None)
            el.Add(new XAttribute(E.PickDirection, note.PickDirection == PickDirectionType.Up ? 1 : 0));

        return el;
    }

    private XElement WriteChord(Chord chord, IList<BeatMarker> beats, RsChordTemplateExport chordTemplates,
        bool isCompactRepeat)
    {
        var el = new XElement(E.Chord);
        var timeMs = TimelineMath.ToWholeMs(chord.Position.GetPositionMs(beats));
        el.Add(new XAttribute(E.Time, Fmt.Time(timeMs)));
        // The INDEX into the emitted chordTemplates list, never the domain id —
        // see RsChordTemplateExport. Callers filter out chords that resolve to -1.
        var chordIndex = chordTemplates.IndexFor(chord);
        el.Add(new XAttribute(E.ChordId, chordIndex));

        // EOF emits chord-level palmMute / fretHandMute regardless of chordNotes presence.
        if (chord.IsPalmMuted()) el.Add(new XAttribute(E.PalmMute, 1));
        if (chord.IsFullyMuted()) el.Add(new XAttribute(E.FretHandMuteAttr, 1));

        // A repeated chord, or one the charter marked "only box", ships with NO
        // <chordNote> children and no highDensity attribute — the official-file
        // spelling, and the ONLY one the game reads at XML version 8. See
        // RsChordDensity for why the attribute alone does nothing.
        var withoutChordNotes = isCompactRepeat || chord.ForceNoNotes;

        if (chord.Accent) el.Add(new XAttribute(E.Accent, 1));
        if (chord.HasLinkNext()) el.Add(new XAttribute(E.LinkNext, 1));
        if (chord.Ignore) el.Add(new XAttribute(E.Ignore, 1));
        if (chord.Strum != PickDirectionType.None)
            el.Add(new XAttribute(E.Strum, chord.Strum == PickDirectionType.Up ? "up" : "down"));

        // chord notes (with leftHand derived from chord template fingers)
        if (withoutChordNotes) return el;

        var template = chordIndex >= 0 ? chordTemplates.Templates[chordIndex] : null;
        foreach (var cn in chord.ChordNotes.OrderBy(n => n.StringIndex))
            el.Add(WriteChordNote(cn, chord.Position, timeMs, beats, template));

        return el;
    }

    private static XElement WriteChordNote(
        ChordNote cn, FractionalPosition chordPosition, int chordTimeMs, IList<BeatMarker> beats,
        ChordTemplate? template)
    {
        var el = new XElement("chordNote");
        // A FractionalPosition maps to ms ABSOLUTELY, from the grid origin — so
        // measuring the sustain as `Sustain - Zero` always resolves it at the
        // tempo of bar 1, and every chord-note sustain on a tempo-varying chart
        // exported wrong (a 60 -> 120 bpm change doubled them). Measure it where
        // the chord actually is, exactly like WriteNote does with EndPosition.
        var sustainMs = TimelineMath.ToWholeMs((chordPosition + cn.Sustain).GetPositionMs(beats)) - chordTimeMs;

        el.Add(new XAttribute(E.Time, Fmt.Time(chordTimeMs)));
        el.Add(new XAttribute(E.String, cn.StringIndex));
        el.Add(new XAttribute(E.Fret, cn.Fret));
        // Omit sustain when zero (matches EOF — it only emits when > 0).
        if (sustainMs > 0)
            el.Add(new XAttribute(E.Sustain, Fmt.Time(sustainMs)));

        // leftHand derived from chord template finger for this string. `>= 0`, not
        // `> 0`: ChordTemplate.FingerNames maps 0 to "T" (thumb) and WriteChordTemplate
        // happily emits finger{i}="0", so excluding it left the file inconsistent with
        // its own template — the thumb vanished from every chordNote that used it.
        if (template != null && template.Fingers.TryGetValue(cn.StringIndex, out var finger) && finger >= 0)
            el.Add(new XAttribute(E.LeftHand, finger));

        if (cn.Vibrato) el.Add(new XAttribute(E.VibratoAttr, VibratoStrength(cn)));
        if (cn.Tremolo) el.Add(new XAttribute(E.TremoloAttr, 1));
        if (cn.Accent) el.Add(new XAttribute(E.Accent, 1));

        AddBendAndValues(el, cn.BendValues.ToList(), beats);

        if (cn.LinkNext) el.Add(new XAttribute(E.LinkNext, 1));
        // Per-string ignore was written for notes and for the chord, but never for a
        // chordNote — so ignoring one string of a strum survived the SNG export and
        // vanished from the XML.
        if (cn.Ignore) el.Add(new XAttribute(E.Ignore, 1));

        if (cn.Mute == MuteType.Full) el.Add(new XAttribute(E.Mute, 1));
        else if (cn.Mute == MuteType.Palm) el.Add(new XAttribute(E.PalmMute, 1));

        // Per-string slap/pop, exactly as WriteNote emits it for a loose note.
        // RsXmlSongReader has always READ these off a <chordNote>, the SNG
        // mapper has always written them into the chord-note mask, and
        // RsChordDensity refuses to compact a chord that carries one — so the
        // XML was the single place a slapped or popped string of a strum
        // disappeared, silently, on every save and every DLC Builder export.
        if (cn.BassPicking == BassPickingType.Pop) el.Add(new XAttribute(E.Pluck, 1));
        else if (cn.BassPicking == BassPickingType.Slap) el.Add(new XAttribute(E.Slap, 1));

        if (cn.Hopo == HopoType.HammerOn) { el.Add(new XAttribute(E.HopoAttr, 1)); el.Add(new XAttribute(E.HammerOn, 1)); }
        else if (cn.Hopo == HopoType.PullOff) { el.Add(new XAttribute(E.HopoAttr, 1)); el.Add(new XAttribute(E.PullOff, 1)); }
        else if (cn.Hopo == HopoType.Tap)
        {
            el.Add(new XAttribute(E.Tap, 1));
        }

        if (cn.Harmonic == HarmonicType.Normal) el.Add(new XAttribute(E.Harmonic, 1));
        else if (cn.Harmonic == HarmonicType.Pinch) el.Add(new XAttribute(E.HarmonicPinch, 1));

        if (cn.SlideTo.HasValue)
        {
            if (cn.UnpitchedSlide) el.Add(new XAttribute(E.SlideUnpitchTo, cn.SlideTo.Value));
            else el.Add(new XAttribute(E.SlideTo, cn.SlideTo.Value));
        }

        return el;
    }

    private static void AddBendAndValues(XElement el, List<BendValue> bendValues, IList<BeatMarker> beats)
    {
        if (bendValues.Count == 0) return;

        // Compute bend integer (max non-zero step, clamped 1-3)
        var bendInt = 0;
        foreach (var bv in bendValues)
        {
            if (bv.Step == 0) continue;
            bendInt = Math.Max(1, Math.Min(3, Math.Max(bendInt, (int)bv.Step)));
        }
        if (bendInt == 0) return; // All zero - no bend attribute

        el.Add(new XAttribute(E.Bend, bendInt));

        var bvEls = bendValues.Select(bv =>
        {
            var bvEl = new XElement(E.BendValue);
            bvEl.Add(new XAttribute(E.Time, Fmt.Time(TimelineMath.ToWholeMs(bv.Position.GetPositionMs(beats)))));
            if (bv.Step != 0)
                bvEl.Add(new XAttribute(E.Step, Fmt.Step(Math.Min(3.0, bv.Step))));
            return bvEl;
        }).ToList();

        el.Add(Fmt.CountedList(E.BendValues, bvEls, x => x));
    }

    private static XElement WriteFretHandMute(FretHandMute m, IList<BeatMarker> beats)
    {
        var el = new XElement(E.FretHandMuteEl);
        el.Add(new XAttribute(E.Time, Fmt.Time(TimelineMath.ToWholeMs(m.Position.GetPositionMs(beats)))));
        return el;
    }

    /// <summary>A single exported <c>&lt;anchor&gt;</c> row.</summary>
    private readonly record struct AnchorExport(int TimeMs, int Fret, int Width);

    /// <summary>
    /// Projects the level's anchors to export rows, collapsing a CONSECUTIVE
    /// pair that restates the SAME fret AND the SAME width — a true duplicate,
    /// where the hand neither moved nor changed its span.
    /// <para>
    /// **The width is part of the key, and removing it corrupts the export.**
    /// Rocksmith derives WHICH FINGER plays a fret from the active anchor as
    /// <c>1 + (fret - anchor.fret) - (anchor.width - 4)</c> (Rocksmith2014.NET
    /// <c>InstrumentalChecker.accountForAnchorWidth</c>), so a same-fret anchor
    /// with a different width is a real change of hand span, not noise. Until
    /// 0.38 this collapsed on the fret alone and kept <c>Math.Max</c> of the two
    /// widths, which both DROPPED the later anchor and retroactively WIDENED the
    /// earlier one. On the owner's chart that turned an authored 12/w4 into
    /// 12/w5 and made DLC Builder report I32 "finger used changes during slide"
    /// for a slide that is clean in the editor — an error the EXPORT invented.
    /// Measured over the 98 hand-corrected EOF exports in <c>TestData/EOF</c>,
    /// EOF itself emits 60 consecutive same-fret pairs whose widths DIFFER, 42
    /// of them inside a single phrase; those are exactly the rows a merge would
    /// destroy.
    /// </para>
    /// <para>
    /// A phrase iteration boundary between the two ALWAYS breaks the collapse.
    /// RS needs a fret-hand position at the start of every phrase iteration
    /// (EOF enforces exactly that in
    /// <c>eof_enforce_rs_phrase_begin_with_fret_hand_position</c>,
    /// EOF_SRC/rs.c:5035), which is why real EOF exports are full of same-fret
    /// anchors seconds apart: of the 1,089 consecutive same-fret pairs in that
    /// corpus, 955 sit across a phrase boundary. Collapsing those would strip
    /// anchors the game requires.
    /// </para>
    /// This is also the rule <c>ChartSpacingEnforcer.RemoveRedundantAnchors</c>
    /// already applies at EDIT time (same fret and width). The two must agree,
    /// or the shipped chart has hand positions the editor never showed.
    /// <para>
    /// Pure projection: the domain <see cref="FretAnchor"/> objects are read,
    /// never mutated or reordered.
    /// </para>
    /// </summary>
    private static List<AnchorExport> BuildAnchorExports(IEnumerable<FretAnchor> anchors,
        IList<BeatMarker> beats, IReadOnlyList<int> phraseStartMs)
    {
        var exports = new List<AnchorExport>();
        foreach (var anchor in anchors)
        {
            var timeMs = TimelineMath.ToWholeMs(anchor.Position.GetPositionMs(beats));
            if (exports.Count > 0 && exports[^1].Fret == anchor.Fret && exports[^1].Width == anchor.Width &&
                !PhraseStartsBetween(phraseStartMs, exports[^1].TimeMs, timeMs))
            {
                continue;
            }

            exports.Add(new AnchorExport(timeMs, anchor.Fret, anchor.Width));
        }

        return exports;
    }

    private static bool PhraseStartsBetween(IReadOnlyList<int> phraseStartMs, int fromMs, int toMs)
    {
        for (var i = 0; i < phraseStartMs.Count; i++)
        {
            if (phraseStartMs[i] > fromMs && phraseStartMs[i] <= toMs)
                return true;
        }

        return false;
    }

    private static XElement WriteAnchor(AnchorExport anchor)
    {
        var el = new XElement(E.Anchor);
        el.Add(new XAttribute(E.Time, Fmt.Time(anchor.TimeMs)));
        el.Add(new XAttribute(E.Fret, anchor.Fret));
        el.Add(new XAttribute(E.Width, anchor.Width.ToString("F3", CultureInfo.InvariantCulture)));
        return el;
    }

    private static XElement WriteHandShape(HandShape hs, IList<BeatMarker> beats,
        RsChordTemplateExport chordTemplates)
    {
        var el = new XElement(E.HandShape);
        // Index, not id. A shape whose template is gone ships as -1 (what
        // SngFromDomainMapper.ConvertHandShape writes into its fingerprint) —
        // "no shape" rather than the silent lie chordId="0" told.
        el.Add(new XAttribute(E.ChordId, chordTemplates.IndexById.GetValueOrDefault(hs.TemplateId, -1)));
        el.Add(new XAttribute(E.StartTime, Fmt.Time(TimelineMath.ToWholeMs(hs.Position.GetPositionMs(beats)))));
        el.Add(new XAttribute(E.EndTime, Fmt.Time(TimelineMath.ToWholeMs(hs.EndPosition.GetPositionMs(beats)))));
        return el;
    }
}
