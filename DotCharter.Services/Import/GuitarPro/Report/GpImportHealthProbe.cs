using System.Diagnostics;
using System.Globalization;
using DotCharter.Core.Linking;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Services.Import.GuitarPro.Report;

/// <summary>Options of <see cref="GpImportHealthProbe.Run" />.</summary>
/// <param name="RoundTrip">Export each part back to Guitar Pro and re-import it.</param>
/// <param name="TempDirectory">Where the round trip writes its scratch <c>.gp</c> (deleted after); the temp folder when null.</param>
public sealed record GpProbeOptions(bool RoundTrip = true, string? TempDirectory = null);

/// <summary>
///     Imports one Guitar Pro file exactly as "add as new arrangements" does (every track,
///     the score's tempo map, <see cref="ChartSpacingEnforcer" />) and measures it, read-only:
///     import health (exceptions, notes out of range, techniques dropped against
///     <see cref="GpTechniqueCensus" />, a Guitar Pro round trip) and linked-measure candidates
///     (<see cref="LinkCandidateDetector" />) checked against Guitar Pro's own repeats. The
///     engine of <c>tools/tab-corpus</c>; nothing in the app calls it.
/// </summary>
public static class GpImportHealthProbe
{
    private const int MaxFret = 24;

    /// <summary>The app's default clean-up settings, as "add as new arrangements" uses them.</summary>
    private static readonly AppConfig Config = new();

    /// <summary>The detection rule, keyed with the gap the chart was cleaned with.</summary>
    private static readonly MeasureContentOptions Detection =
        MeasureContentOptions.Detection with { Spacing = ChartSpacingEnforcer.SpacingGapOf(Config) };

    private static readonly MeasureContentOptions SoundsOnly =
        MeasureContentOptions.SoundsOnly with { Spacing = ChartSpacingEnforcer.SpacingGapOf(Config) };

    public static GpProbeReport Run(string path, GpProbeOptions? options = null)
    {
        options ??= new GpProbeOptions();
        var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var clock = Stopwatch.StartNew();
        GpImportResult imported;
        List<GpTrackInfo> tracks;
        try
        {
            var importer = new GpImporter();
            tracks = importer.GetAvailableTracks(path);
            imported = importer.ImportTracksWithMetadata(path, [.. tracks.Select(t => t.Id)]);
        }
        catch (Exception ex)
        {
            return Failed(path, format, "import", ex, clock);
        }

        try
        {
            var report = Analyse(path, format, tracks, imported, options);
            report.ElapsedMs = clock.ElapsedMilliseconds;
            return report;
        }
        catch (Exception ex)
        {
            return Failed(path, format, "analysis", ex, clock);
        }
    }

    private static GpProbeReport Failed(string path, string format, string stage, Exception ex, Stopwatch clock) => new()
    {
        Path = path,
        Format = format,
        Ok = false,
        ErrorClass = $"{stage}:{ex.GetType().Name}",
        ErrorMessage = FirstLine(ex.Message),
        ElapsedMs = clock.ElapsedMilliseconds
    };

    private static GpProbeReport Analyse(string path, string format, List<GpTrackInfo> tracks, GpImportResult imported,
        GpProbeOptions options)
    {
        var warnings = new List<string>();
        var meta = imported.Metadata;
        var beats = GpBeatMapBuilder.Build(meta, GpBeatMapBuilder.InitialBpm(meta, 120), 0);
        foreach (var arrangement in imported.Arrangements)
            ChartSpacingEnforcer.EnforceArrangement(arrangement, beats, Config);

        if (tracks.Count == 0) warnings.Add("no-tracks");
        if (imported.Arrangements.Count != tracks.Count) warnings.Add("tracks-dropped");

        // Which written bar each measure plays, and how often each written bar is played.
        var source = meta.Bars.Select(b => b.SourceBarIndex).ToArray();
        var sourceBars = source.Length == 0 ? 0 : source.Max() + 1;
        var plays = new int[Math.Max(0, sourceBars)];
        var replayed = 0;
        foreach (var bar in source.Where(b => b >= 0))
        {
            if (plays[bar]++ > 0) replayed++;
        }

        var indexes = imported.Arrangements.Select(a => MeasureContentIndex.ForHardestLevel(a, beats, Detection)).ToList();
        var measures = indexes.Count > 0 ? indexes[0].Measures.Count : MeasureGrid.Build(beats).Count;
        if (measures != meta.Bars.Count) warnings.Add("measure-count-mismatch");

        var census = format is "gp" or "gpx" ? GpTechniqueCensus.CountSource(path, plays) : null;
        if (census != null && census.Count != imported.Arrangements.Count)
        {
            warnings.Add("census-unaligned");
            census = null;
        }

        // Per-part facts first; the shared-run pass needs every part's sound count.
        var parts = new List<PartFacts>();
        for (var a = 0; a < imported.Arrangements.Count; a++)
            parts.Add(Facts(imported.Arrangements[a], indexes[a], beats));

        var shared = SharedRuns(parts, indexes, out var crossGroups, out var truncated);

        var file = new FileContext(measures, source, plays, beats, options, warnings);
        var results = new List<GpProbeArrangement>();
        for (var a = 0; a < imported.Arrangements.Count; a++)
        {
            var within = parts[a].Sounds > 0 ? LinkCandidateDetector.Detect([indexes[a]]) : new LinkDetectionResult([], false);
            truncated |= within.Truncated;
            results.Add(Describe(a, imported.Arrangements[a], parts[a], indexes[a], within, shared[a], census?[a],
                imported.SlideInsMaterialized.GetValueOrDefault(imported.Arrangements[a]), file));
        }

        return new GpProbeReport
        {
            Path = path,
            Format = format,
            Ok = true,
            Tracks = tracks.Count,
            Measures = measures,
            SourceBars = sourceBars,
            RepeatPlayedMeasures = replayed,
            CrossGroups = crossGroups,
            Truncated = truncated,
            Arrangements = results,
            Warnings = [.. warnings.Distinct()]
        };
    }

    private static GpProbeArrangement Describe(int track, Arrangement arrangement, PartFacts facts, MeasureContentIndex index,
        LinkDetectionResult within, (bool[] Covered, int SamePosition) shared, Dictionary<string, int>? census, int slideIns,
        FileContext file)
    {
        var covered = within.Covered(0, file.Measures);
        var (pairs, equal, grouped) = GpRepeatAgreement(index, within, file.Source, facts.Content);
        var (roundTrip, rtMeasures, rtEqual, rtSounds, rtDiffs) = file.Options.RoundTrip && facts.Sounds > 0
            ? RoundTrip(arrangement, facts.Level, file.Beats, file.Options)
            : ("skipped", 0, 0, 0, []);

        if (facts.Sounds == 0) file.Warnings.Add("empty-track");
        if (facts.BadString + facts.FretOver24 + facts.BelowCapo > 0) file.Warnings.Add("out-of-range");
        if (facts.OffGrid > 0) file.Warnings.Add("off-grid");
        if (roundTrip.StartsWith("error", StringComparison.Ordinal)) file.Warnings.Add("round-trip-" + roundTrip);
        else if (rtMeasures > 0 && rtEqual < rtMeasures) file.Warnings.Add("round-trip-diff");

        return new GpProbeArrangement
        {
            Track = track,
            Name = arrangement.Name,
            Type = arrangement.ArrangementType,
            IsBass = ArrangementInstrument.IsBassPart(arrangement),
            StringCount = arrangement.StringCount,
            Tuning = string.Join(" ", arrangement.TuningPitches.Select(t => t.Step.ToString(CultureInfo.InvariantCulture))),
            Capo = arrangement.Capo,
            LinkClass = facts.Sounds > 0 ? LinkCompatibility.Signature(arrangement) : null,
            Notes = facts.Level.Notes.Count,
            Chords = facts.Level.Chords.Count,
            Sounds = facts.Sounds,
            BadString = facts.BadString,
            FretOver24 = facts.FretOver24,
            BelowCapo = facts.BelowCapo,
            OffGrid = facts.OffGrid,
            Measures = file.Measures,
            ContentMeasures = facts.Content.Count(c => c),
            RepeatedMeasures = Count(covered, facts.Content),
            RepeatedBeyondGp = Enumerable.Range(0, file.Measures).Count(m => covered[m] && facts.Content[m] && file.PlayedOnce(m)),
            RepeatGroups = within.Groups.Count,
            SharedMeasures = Count(shared.Covered, facts.Content),
            SharedSamePosition = shared.SamePosition,
            GpRepeatPairs = pairs,
            GpRepeatPairsEqual = equal,
            GpRepeatPairsGrouped = grouped,
            SourceTechniques = census ?? [],
            ImportedTechniques = GpTechniqueCensus.CountImported(facts.Level, slideIns),
            RoundTrip = roundTrip,
            RoundTripMeasures = rtMeasures,
            RoundTripMeasuresEqual = rtEqual,
            RoundTripSoundsAfter = rtSounds,
            RoundTripDiffs = rtDiffs
        };
    }

    private static PartFacts Facts(Arrangement arrangement, MeasureContentIndex index, IList<BeatMarker> beats)
    {
        var level = arrangement.Levels.MaxBy(l => l.DifficultyIndex) ?? new ChartLevel();
        var (badString, over24, belowCapo, offGrid) = (0, 0, 0, 0);
        var gridStart = index.Measures.Count > 0 ? index.Measures[0].StartBeat : 0;

        void Check(int stringIndex, int fret)
        {
            if (stringIndex < 0 || stringIndex >= arrangement.StringCount) badString++;
            if (fret > MaxFret) over24++;
            if (fret != 0 && fret < arrangement.Capo) belowCapo++;
        }

        void CheckPosition(FractionalPosition position)
        {
            if (position.BeatId < gridStart || position.BeatId >= beats.Count) offGrid++;
        }

        var sounds = 0;
        foreach (var note in level.Notes)
        {
            Check(note.StringIndex, note.Fret);
            CheckPosition(note.Position);
            sounds++;
        }

        foreach (var chord in level.Chords)
        {
            CheckPosition(chord.Position);
            foreach (var chordNote in chord.ChordNotes)
            {
                Check(chordNote.StringIndex, chordNote.Fret);
                sounds++;
            }
        }

        var content = new bool[index.Measures.Count];
        for (var m = 0; m < content.Length; m++) content[m] = !index.KeyFor(m).IsEmpty;
        return new PartFacts(level, sounds, badString, over24, belowCapo, offGrid, content);
    }

    /// <summary>Runs shared between parts of one compatibility class.</summary>
    private static List<(bool[] Covered, int SamePosition)> SharedRuns(List<PartFacts> parts,
        List<MeasureContentIndex> indexes, out int crossGroups, out bool truncated)
    {
        var shared = parts.Select(p => (Covered: new bool[p.Content.Length], SamePosition: 0)).ToList();
        crossGroups = 0;
        truncated = false;
        var classes = Enumerable.Range(0, parts.Count)
            .Where(a => parts[a].Sounds > 0)
            .GroupBy(a => LinkCompatibility.Signature(indexes[a].Arrangement))
            .Where(g => g.Key != null && g.Count() > 1);

        foreach (var members in classes.Select(g => g.ToList()))
        {
            var detection = LinkCandidateDetector.Detect([.. members.Select(a => indexes[a])]);
            truncated |= detection.Truncated;
            crossGroups += detection.Groups.Count(g => g.SpansParts);
            for (var k = 0; k < members.Count; k++)
            {
                var a = members[k];
                var covered = detection.Covered(k, parts[a].Content.Length, spanningOnly: true);
                var same = 0;
                for (var m = 0; m < parts[a].Content.Length; m++)
                {
                    if (!parts[a].Content[m]) continue;
                    var key = indexes[a].KeyFor(m);
                    if (members.Any(b => b != a && m < indexes[b].Measures.Count && indexes[b].KeyFor(m).Equals(key))) same++;
                }

                shared[a] = (covered, same);
            }
        }

        return shared;
    }

    /// <summary>Pairs of measures that play one written bar: are they equal, and grouped together?</summary>
    private static (int Pairs, int Equal, int Grouped) GpRepeatAgreement(MeasureContentIndex index,
        LinkDetectionResult within, int[] source, bool[] content)
    {
        var (pairs, equal, grouped) = (0, 0, 0);
        var count = Math.Min(source.Length, content.Length);
        foreach (var bar in Enumerable.Range(0, count).Where(m => content[m] && source[m] >= 0).GroupBy(m => source[m]))
        {
            var played = bar.ToList();
            for (var i = 0; i < played.Count; i++)
            {
                for (var j = i + 1; j < played.Count; j++)
                {
                    pairs++;
                    if (index.KeyFor(played[i]).Equals(index.KeyFor(played[j]))) equal++;
                    var group = within.GroupAt(0, played[i]);
                    if (group != null && ReferenceEquals(group, within.GroupAt(0, played[j]))) grouped++;
                }
            }
        }

        return (pairs, equal, grouped);
    }

    private static (string Status, int Measures, int Equal, int SoundsAfter, Dictionary<string, int> Diffs) RoundTrip(Arrangement arrangement,
        ChartLevel level, IList<BeatMarker> beats, GpProbeOptions options)
    {
        var scratch = Path.Combine(options.TempDirectory ?? Path.GetTempPath(), $"dc-tabprobe-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(scratch, GpExporter.ExportToBytes(arrangement, level, beats));
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(scratch);
            var again = importer.ImportTracksWithMetadata(scratch, [.. tracks.Select(t => t.Id)]);
            var back = again.Arrangements.FirstOrDefault();
            if (back == null) return ("error:NoTrack", 0, 0, 0, []);
            var beatsBack = GpBeatMapBuilder.Build(again.Metadata, GpBeatMapBuilder.InitialBpm(again.Metadata, 120), 0);
            ChartSpacingEnforcer.EnforceArrangement(back, beatsBack, Config);

            var before = new MeasureContentIndex(arrangement, level, beats, SoundsOnly);
            var after = MeasureContentIndex.ForHardestLevel(back, beatsBack, SoundsOnly);
            var compared = Math.Max(before.Measures.Count, after.Measures.Count);
            var equalMeasures = 0;
            for (var m = 0; m < Math.Min(before.Measures.Count, after.Measures.Count); m++)
            {
                if (before.KeyFor(m).Equals(after.KeyFor(m))) equalMeasures++;
            }

            var backLevel = back.Levels.MaxBy(l => l.DifficultyIndex);
            var soundsAfter = backLevel == null ? 0 : backLevel.Notes.Count + backLevel.Chords.Sum(c => c.ChordNotes.Count);
            return ("ok", compared, equalMeasures, soundsAfter, GpRoundTripDiff.Classify(before, after));
        }
        catch (Exception ex)
        {
            return ("error:" + ex.GetType().Name, 0, 0, 0, []);
        }
        finally
        {
            try
            {
                File.Delete(scratch);
            }
            catch (IOException)
            {
                // Scratch file only; the temp folder is cleaned eventually.
            }
        }
    }

    private static int Count(bool[] covered, bool[] content)
    {
        var count = 0;
        for (var m = 0; m < covered.Length && m < content.Length; m++)
        {
            if (covered[m] && content[m]) count++;
        }

        return count;
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>What every part of one file shares: the grid, the written-bar map, the options and the warnings.</summary>
    private sealed record FileContext(int Measures, int[] Source, int[] Plays, IList<BeatMarker> Beats, GpProbeOptions Options,
        List<string> Warnings)
    {
        /// <summary>Measure <paramref name="m" /> plays a written bar Guitar Pro plays only once.</summary>
        public bool PlayedOnce(int m) => m < Source.Length && Source[m] >= 0 && Plays[Source[m]] == 1;
    }

    private sealed record PartFacts(ChartLevel Level, int Sounds, int BadString, int FretOver24, int BelowCapo, int OffGrid,
        bool[] Content);
}
