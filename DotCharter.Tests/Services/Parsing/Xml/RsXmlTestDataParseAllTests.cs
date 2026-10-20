using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
/// Smoke-tests the RS-XML parser against every real-world *_RS2.xml shipped
/// in TestData/EOF. Catches regressions where an EOF element/attribute
/// breaks our parser, and verifies write-then-read roundtrip preserves
/// core structural data.
/// </summary>
public class RsXmlTestDataParseAllTests
{
    private const string MissingCorpus =
        "No RS-XML corpus: TestData/EOF/**/*_RS2.xml is absent (TestData/ is git-ignored).";

    private static IEnumerable<string> EnumerateRs2XmlFiles()
    {
        var root = FindTestDataRoot();
        if (root == null) yield break;

        var eof = Path.Combine(root, "EOF");
        if (!Directory.Exists(eof)) yield break;

        foreach (var file in Directory.EnumerateFiles(eof, "*_RS2.xml", SearchOption.AllDirectories))
        {
            if (file.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip vocal-only RS2 documents (they use the <vocals> root, not <song>).
            if (file.Contains("VOCAL", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("vocal", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    [Fact]
    public void Load_RealWorldRs2Xml_DoesNotThrow()
    {
        // TestData/ is gitignored. Without it the loop below has nothing to walk and
        // "failures.Count == 0" passes vacuously — SKIP instead, so the darkness shows.
        var files = EnumerateRs2XmlFiles().ToList();
        Assert.SkipWhen(files.Count == 0, MissingCorpus);

        var reader = new RsXmlSongReader();
        var failures = new List<string>();

        foreach (var filePath in files)
        {
            try
            {
                var project = reader.Read(filePath);
                Assert.NotNull(project);
                Assert.NotEmpty(project.Beats);
                Assert.Single(project.Arrangements);
            }
            catch (Exception ex)
            {
                failures.Add($"{filePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RoundTrip_RealWorldRs2Xml_PreservesCoreData()
    {
        var files = EnumerateRs2XmlFiles().ToList();
        Assert.SkipWhen(files.Count == 0, MissingCorpus);

        var reader = new RsXmlSongReader();
        var writer = new RsXmlSongWriter();
        var failures = new List<string>();

        foreach (var filePath in files)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"rs2_rt_{Guid.NewGuid():N}.xml");
            try
            {
                var loaded = reader.Read(filePath);
                writer.WriteTo(loaded, tempPath);
                var reloaded = reader.Read(tempPath);

                Assert.Equal(loaded.Beats.Count, reloaded.Beats.Count);
                Assert.Equal(loaded.Arrangements.Count, reloaded.Arrangements.Count);

                var a = loaded.Arrangements[0];
                var b = reloaded.Arrangements[0];
                Assert.Equal(a.ArrangementType, b.ArrangementType);
                Assert.Equal(a.Capo, b.Capo);
                Assert.Equal(a.StringCount, b.StringCount);
                Assert.Equal(a.Levels.Count, b.Levels.Count);
                Assert.Equal(a.ChordTemplates.Count, b.ChordTemplates.Count);

                // The anchors the writer's dedupe is measured against. It reads
                // the phrase iterations it is about to export (explicit ones
                // verbatim when the chart has them — every real EOF file does —
                // else the EventPoint-derived set, which only the written
                // document can report back).
                var phraseStartMs = a.Phrases.Count > 0 && a.PhraseIterations.Count > 0
                    ? PhraseStartsMs(a.PhraseIterations, loaded.Beats)
                    : PhraseStartsMs(b.PhraseIterations, reloaded.Beats);

                for (var li = 0; li < a.Levels.Count; li++)
                {
                    Assert.Equal(a.Levels[li].Notes.Count, b.Levels[li].Notes.Count);
                    Assert.Equal(a.Levels[li].Chords.Count, b.Levels[li].Chords.Count);
                    Assert.Equal(a.Levels[li].HandShapes.Count, b.Levels[li].HandShapes.Count);

                    // Anchors are deliberately NOT count-preserving: the writer
                    // collapses consecutive same-fret anchors inside a phrase
                    // iteration (RsXmlSongWriter.BuildAnchorExports), and real
                    // EOF charts are full of those redundant restatements. So
                    // assert against the RULE, not against a bound: apply the
                    // documented collapse to what was read and require the
                    // reload to match it exactly. "<= loaded" used to pass even
                    // if the writer dropped every anchor; this fails the moment
                    // the dedupe collapses one anchor too many (a phrase
                    // iteration must always keep its opening fret-hand
                    // position, EOF_SRC\rs.c:5035), keeps one too few, or stops
                    // widening the survivor to the widest span it absorbed.
                    var expected = ExpectedAnchorExports(a.Levels[li].Anchors, loaded.Beats, phraseStartMs);
                    var actual = AnchorRows(b.Levels[li].Anchors, reloaded.Beats);

                    Assert.True(expected.Count == actual.Count,
                        $"level {li}: expected {expected.Count} anchors after dedupe " +
                        $"({a.Levels[li].Anchors.Count} read), got {actual.Count}");

                    for (var ai = 0; ai < expected.Count; ai++)
                    {
                        Assert.True(expected[ai].Fret == actual[ai].Fret,
                            $"level {li} anchor {ai}: fret {expected[ai].Fret} -> {actual[ai].Fret}");
                        Assert.True(expected[ai].Width == actual[ai].Width,
                            $"level {li} anchor {ai}: width {expected[ai].Width} -> {actual[ai].Width}");
                        // ms -> FractionalPosition -> ms is not bit-exact.
                        Assert.True(Math.Abs(expected[ai].TimeMs - actual[ai].TimeMs) <= 1,
                            $"level {li} anchor {ai}: time {expected[ai].TimeMs} -> {actual[ai].TimeMs}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{filePath}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>One exported <c>&lt;anchor&gt;</c> row, as the writer would emit it.</summary>
    /// <param name="TimeMs">Anchor position in whole milliseconds.</param>
    /// <param name="Fret">Fret the hand sits on.</param>
    /// <param name="Width">Span in frets.</param>
    private readonly record struct AnchorRow(int TimeMs, int Fret, int Width);

    /// <summary>Projects domain anchors to comparable rows.</summary>
    /// <param name="anchors">The level's anchors, in order.</param>
    /// <param name="beats">The beat grid the anchors' positions resolve against.</param>
    private static List<AnchorRow> AnchorRows(IEnumerable<FretAnchor> anchors, IList<BeatMarker> beats)
        => anchors.Select(x => new AnchorRow((int)x.Position.GetPositionMs(beats), x.Fret, x.Width)).ToList();

    /// <param name="iterations">Phrase iterations to project.</param>
    /// <param name="beats">The beat grid their positions resolve against.</param>
    private static List<int> PhraseStartsMs(IEnumerable<PhraseIteration> iterations, IList<BeatMarker> beats)
        => iterations.Select(pi => (int)pi.Position.GetPositionMs(beats)).ToList();

    /// <summary>
    ///     Re-implements <c>RsXmlSongWriter.BuildAnchorExports</c>'s documented
    ///     rule so the round-trip can assert the exact expected output instead of
    ///     a mere upper bound: a consecutive pair restating the same fret AND the
    ///     same width collapses to the earliest time, and a phrase-iteration
    ///     start between two of them always breaks the collapse (RS requires a
    ///     fret-hand position at the start of every phrase iteration).
    ///     <para>
    ///         The WIDTH is part of the key. Until 0.38 the collapse keyed on the
    ///         fret alone and kept the widest of the two widths, which cost this
    ///         very corpus 60 authored anchors — 42 of them inside a phrase, over
    ///         11 of these fixtures — because a same-fret width change is a real
    ///         hand-span change that Rocksmith reads when it decides which finger
    ///         plays a fret.
    ///     </para>
    /// </summary>
    /// <param name="anchors">The anchors as they were read from the fixture.</param>
    /// <param name="beats">The beat grid the anchors' positions resolve against.</param>
    /// <param name="phraseStartMs">Phrase-iteration start times, in milliseconds.</param>
    private static List<AnchorRow> ExpectedAnchorExports(IEnumerable<FretAnchor> anchors,
        IList<BeatMarker> beats, IReadOnlyList<int> phraseStartMs)
    {
        var expected = new List<AnchorRow>();
        foreach (var anchor in anchors)
        {
            var timeMs = (int)anchor.Position.GetPositionMs(beats);
            if (expected.Count > 0 && expected[^1].Fret == anchor.Fret && expected[^1].Width == anchor.Width &&
                !PhraseStartsBetween(phraseStartMs, expected[^1].TimeMs, timeMs))
            {
                continue;
            }

            expected.Add(new AnchorRow(timeMs, anchor.Fret, anchor.Width));
        }

        return expected;
    }

    /// <param name="phraseStartMs">Phrase-iteration start times, in milliseconds.</param>
    /// <param name="fromMs">Exclusive lower bound.</param>
    /// <param name="toMs">Inclusive upper bound.</param>
    private static bool PhraseStartsBetween(IReadOnlyList<int> phraseStartMs, int fromMs, int toMs)
    {
        for (var i = 0; i < phraseStartMs.Count; i++)
        {
            if (phraseStartMs[i] > fromMs && phraseStartMs[i] <= toMs)
                return true;
        }

        return false;
    }

    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
