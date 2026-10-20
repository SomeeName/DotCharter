using DotCharter.Core.Diff;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     The round-trip parity gate (plan 6.9): export the representative
///     in-memory chart, run the EXISTING EofImporter over the produced bytes
///     and require the reimported domain graph to equal the original —
///     <see cref="ChartDiffEngine"/> over every level's sounds plus
///     field-wise asserts for everything the diff engine does not cover
///     (beats, tuning, capo, anchors, handshapes, tones, phrases, events).
/// </summary>
public class EofRoundTripParityTests
{
    private static (ChartProject Original, EofImportResult Reimported) RoundTrip()
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        var result = EofExporter.Export(project);
        Assert.True(result.Report.IsLossless,
            "Representative chart must export losslessly: " +
            string.Join("; ", result.Report.Notes.Select(n => $"{n.Kind} {n.Subject}: {n.Message}")));

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-roundtrip-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, result.Bytes);
        try
        {
            var importer = new EofImporter();
            var tracks = importer.GetAvailableTracks(path);
            return (project, importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Arrangement Reimported(EofImportResult result, string name)
    {
        var arrangement = result.Arrangements.FirstOrDefault(a => a.Name == name);
        Assert.NotNull(arrangement);
        return arrangement;
    }

    [Fact]
    public void RoundTrip_EveryArrangement_HasEmptyChartDiff()
    {
        var (original, reimported) = RoundTrip();
        Assert.Equal(original.Arrangements.Count, reimported.Arrangements.Count);

        foreach (var arrangement in original.Arrangements)
        {
            var back = Reimported(reimported, arrangement.Name);
            var diff = ChartDiffEngine.Diff(arrangement, back);
            var sounds = diff.Levels.SelectMany(l => l.Entries.Select(e =>
                $"d{l.DifficultyIndex} {e.Change} {e.Kind} @{e.Position} s{e.StringIndex} [{string.Join(",", e.ChangedFields)}]"));
            // The diff covers structure too since 0.35, so the message has to name
            // it — otherwise a structural mismatch reports a count over a blank list.
            var structure = diff.Structure.Select(e =>
                $"d{e.DifficultyIndex} {e.Change} {e.Kind} @{e.Position} '{e.Label}' [{string.Join(",", e.ChangedFields)}]");

            Assert.True(diff.IsEmpty,
                $"{arrangement.Name}: expected an empty diff but got {diff.TotalCount} entries: " +
                string.Join("; ", sounds.Concat(structure)));
        }
    }

    [Fact]
    public void RoundTrip_BeatGrid_Matches()
    {
        var (original, reimported) = RoundTrip();
        Assert.Equal(original.Beats.Count, reimported.Metadata.Beats.Count);

        for (var i = 0; i < original.Beats.Count; i++)
        {
            var expected = original.Beats[i];
            var actual = reimported.Metadata.Beats[i];
            Assert.Equal(expected.Time, actual.TimeMs, 9); // Exact via the FP timing block.
            Assert.Equal(expected.Bpm, actual.Bpm, 6);
            Assert.Equal(expected.BeatsInMeasure, actual.BeatsInMeasure);
            Assert.Equal(expected.NoteDenominator, actual.NoteDenominator);
            Assert.Equal(expected.IsMeasure, actual.IsMeasureStart);
            Assert.Equal(expected.IsAnchor, actual.IsAnchor);
        }
    }

    [Fact]
    public void RoundTrip_ArrangementIdentity_Matches()
    {
        var (original, reimported) = RoundTrip();
        foreach (var arrangement in original.Arrangements)
        {
            var back = Reimported(reimported, arrangement.Name);
            Assert.Equal(arrangement.ArrangementType, back.ArrangementType);
            Assert.Equal(arrangement.Capo, back.Capo);
            Assert.Equal(arrangement.StringCount, back.StringCount);
            Assert.Equal(arrangement.TuningPitches.Select(t => t.Step),
                back.TuningPitches.Select(t => t.Step));
            Assert.Equal(arrangement.Levels.Select(l => l.DifficultyIndex).OrderBy(i => i),
                back.Levels.Select(l => l.DifficultyIndex).OrderBy(i => i));
        }
    }

    [Fact]
    public void RoundTrip_AnchorsAndHandShapes_Match()
    {
        var (original, reimported) = RoundTrip();
        foreach (var arrangement in original.Arrangements)
        {
            var back = Reimported(reimported, arrangement.Name);
            foreach (var level in arrangement.Levels)
            {
                var backLevel = back.Levels.FirstOrDefault(l => l.DifficultyIndex == level.DifficultyIndex);
                Assert.NotNull(backLevel);

                Assert.Equal(
                    level.Anchors.OrderBy(a => a.Position).Select(a => (a.Position, a.Fret, a.Width)),
                    backLevel.Anchors.OrderBy(a => a.Position).Select(a => (a.Position, a.Fret, a.Width)));

                Assert.Equal(
                    level.HandShapes.OrderBy(h => h.Position)
                        .Select(h => (h.Position, h.EndPosition, h.TemplateId, h.Name)),
                    backLevel.HandShapes.OrderBy(h => h.Position)
                        .Select(h => (h.Position, h.EndPosition, h.TemplateId, h.Name)));
            }
        }
    }

    [Fact]
    public void RoundTrip_ChordTemplates_KeepFretsAndFingers()
    {
        var (original, reimported) = RoundTrip();
        foreach (var arrangement in original.Arrangements.Where(a => a.ChordTemplates.Count > 0))
        {
            var back = Reimported(reimported, arrangement.Name);
            Assert.Equal(arrangement.ChordTemplates.Count, back.ChordTemplates.Count);
            foreach (var template in arrangement.ChordTemplates)
            {
                var backTemplate = back.ChordTemplates.FirstOrDefault(t => t.Id == template.Id);
                Assert.NotNull(backTemplate);
                Assert.Equal(template.Frets.OrderBy(kv => kv.Key), backTemplate.Frets.OrderBy(kv => kv.Key));
                Assert.Equal(template.Fingers.OrderBy(kv => kv.Key), backTemplate.Fingers.OrderBy(kv => kv.Key));
            }
        }
    }

    /// <summary>
    ///     The AUTHORED chord name is written by our own exporter
    ///     (<c>EofNoteBuilder</c> emits <c>template?.Name</c>) and was read into
    ///     <c>EofRawProGuitarNote.Name</c> and then thrown away — so a save →
    ///     reload renamed every chord via <c>ChordNameSuggester</c>. The
    ///     frets/fingers parity test above never noticed because it does not
    ///     compare names.
    /// </summary>
    [Fact]
    public void RoundTrip_ChordTemplates_KeepTheAuthoredName()
    {
        var (original, reimported) = RoundTrip();
        var compared = 0;

        foreach (var arrangement in original.Arrangements.Where(a => a.ChordTemplates.Count > 0))
        {
            var back = Reimported(reimported, arrangement.Name);
            foreach (var template in arrangement.ChordTemplates.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
            {
                var backTemplate = back.ChordTemplates.FirstOrDefault(t => t.Id == template.Id);
                Assert.NotNull(backTemplate);
                Assert.Equal(template.Name, backTemplate.Name);
                compared++;
            }
        }

        // The fixture authors "E5" and "Slide"; a silent fixture change that
        // dropped both names would otherwise make this pass over nothing.
        Assert.True(compared >= 2, $"expected at least two named templates, compared {compared}");
    }

    [Fact]
    public void RoundTrip_Tones_Match()
    {
        var (original, reimported) = RoundTrip();
        foreach (var arrangement in original.Arrangements)
        {
            var back = Reimported(reimported, arrangement.Name);
            Assert.Equal(arrangement.StartingTone, back.StartingTone);
            Assert.Equal(arrangement.ToneNames, back.ToneNames);
            Assert.Equal(
                arrangement.ToneChanges.OrderBy(t => t.Position).Select(t => (t.Position, t.ToneName)),
                back.ToneChanges.OrderBy(t => t.Position).Select(t => (t.Position, t.ToneName)));
        }
    }

    [Fact]
    public void RoundTrip_EventMarkers_Match()
    {
        var (original, reimported) = RoundTrip();
        foreach (var arrangement in original.Arrangements)
        {
            var back = Reimported(reimported, arrangement.Name);

            // One domain EventPoint can split into several EOF text events
            // (section + phrase text differ), so compare the flattened
            // (position, kind, text) marker sets, not point-for-point.
            Assert.Equal(FlattenMarkers(arrangement), FlattenMarkers(back));
            Assert.Equal(arrangement.Phrases, back.Phrases);
            Assert.Equal(
                arrangement.PhraseInfos.OrderBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value.MaxDifficulty, kv.Value.Solo)),
                back.PhraseInfos.OrderBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value.MaxDifficulty, kv.Value.Solo)));
        }
    }

    private static List<(FractionalPosition Position, string Kind, string Text)> FlattenMarkers(
        Arrangement arrangement)
    {
        var markers = new List<(FractionalPosition, string, string)>();
        foreach (var ep in arrangement.EventPoints)
        {
            if (!string.IsNullOrEmpty(ep.Section)) markers.Add((ep.Position, "section", ep.Section));
            if (!string.IsNullOrEmpty(ep.Phrase)) markers.Add((ep.Position, "phrase", ep.Phrase));
            if (!string.IsNullOrEmpty(ep.Events)) markers.Add((ep.Position, "event", ep.Events));
        }

        return markers.OrderBy(m => m.Item1).ThenBy(m => m.Item2).ThenBy(m => m.Item3).ToList();
    }
}
