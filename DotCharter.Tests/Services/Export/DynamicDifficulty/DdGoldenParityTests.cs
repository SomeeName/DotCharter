using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Export.DynamicDifficulty;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>
///     The C# port of DLC Builder's dynamic-difficulty generator reproduces the F# one,
///     element by element: levels, notes, chords and their chord notes, anchors, hand shapes,
///     phrases (names and <c>maxDifficulty</c>), phrase iterations with their hero levels,
///     and the linked difficulties the phrase search makes.
///     <para>
///         <b>Where the goldens come from.</b> <c>Fixtures/&lt;name&gt;.xml.gz</c> are
///         single-level RS2014 XMLs: real charts from the owner's EOF projects made
///         anonymous by a script (every fret 1..24 through a seeded per-string permutation,
///         anchor frets through another, chord names numbered, title/artist/album blanked;
///         the generator only compares frets for equality and order, so they are no less of
///         an input). <c>Fixtures/&lt;name&gt;.dd.xml.gz</c> is what the REAL F# generator wrote
///         for each: a throwaway F# console, outside the repository, referencing
///         Rocksmith2014.NET's <c>src/Rocksmith2014.DD</c> and <c>src/Rocksmith2014.XML</c>
///         at commit b87c9a3a (2026-03-13), that for each file ran
///         <c>InstrumentalArrangement.Load</c>, then
///         <c>Generator.generateForArrangement { PhraseSearchThreshold = Some 80;
///         LevelCountGeneration = LevelCountGeneration.Simple }</c> (DLC Builder's default
///         <c>DDConfig</c>), then <c>arr.Save</c>. Both are gzip-compressed as written.
///     </para>
///     <para>
///         <b>Chord IDs are compared by the template they point at.</b> The F# runs phrases
///         in parallel and appends the narrower templates to one list as each phrase needs
///         them, so the NUMBER of a new template depends on thread timing — running the tool
///         twice on lead-a numbered 314 lines differently. Which templates exist, and which
///         one each chord and hand shape uses, does not.
///     </para>
/// </summary>
public sealed class DdGoldenParityTests
{
    internal static readonly string[] Names = ["bass-a", "lead-a", "lead-b", "lead-c", "rhythm-a"];

    public static TheoryData<string> Fixtures => [.. Names];

    /// <summary>Every fixture in every mode the F# tool ran; "full" (two fixtures, the
    /// rest are the same two steps) is DLC Builder's build order: improvements, then DD.</summary>
    public static TheoryData<string, string> Runs
    {
        get
        {
            var runs = new TheoryData<string, string>();
            foreach (var name in Names)
            {
                foreach (var mode in new[] { "dd", "ddml", "improve" }) runs.Add(name, mode);
            }

            runs.Add("bass-a", "full");
            runs.Add("lead-a", "full");
            // lead-a with a "mover2" phrase, an upper-case E1 crowd event, a "w3-5" anchor
            // event and a "removebeats" event added by hand (the custom events DLC Builder reads).
            runs.Add("custom-a", "improve");
            runs.Add("custom-a", "full");
            return runs;
        }
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void The_port_writes_what_DLC_Builder_wrote(string name, string mode)
    {
        var input = LoadFixture(name + ".xml");
        var expected = DdXml.Read(LoadFixture($"{name}.{mode}.xml"));

        var arr = DdXml.Read(input);
        if (mode is "improve" or "full") DdImprover.ApplyAll(arr);
        if (mode is "dd" or "full") DdGenerator.GenerateForArrangement(DdGeneratorConfig.Default, arr);
        if (mode == "ddml") DdGenerator.GenerateForArrangement(DdGeneratorConfig.MLModel, arr);
        // Through the writer and back, as the export uses it.
        DdXml.Write(input, arr);
        var actual = DdXml.Read(input);

        AssertSame(Describe(expected), Describe(actual));
    }

    /// <summary>Names the first line that differs, not only its index.</summary>
    internal static void AssertSame(List<string> expected, List<string> actual)
    {
        var at = Enumerable.Range(0, Math.Min(expected.Count, actual.Count)).FirstOrDefault(i => expected[i] != actual[i], -1);
        if (at >= 0) Assert.Fail($"line {at}: expected '{expected[at]}' but was '{actual[at]}'");
        Assert.Equal(expected.Count, actual.Count);
    }

    [Fact]
    public void The_fixtures_are_single_level_and_the_goldens_are_not()
    {
        foreach (var name in Names)
        {
            Assert.Single(DdXml.Read(LoadFixture(name + ".xml")).Levels);
            Assert.True(DdXml.Read(LoadFixture(name + ".dd.xml")).Levels.Count > 5, name);
            Assert.True(DdXml.Read(LoadFixture(name + ".ddml.xml")).Levels.Count > 5, name);
        }
    }

    internal static XElement LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "Export", "DynamicDifficulty", "Fixtures", fileName + ".gz");
        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        return XDocument.Load(gzip).Root!;
    }

    /// <summary>A line per element, chord IDs replaced by the template's content, so a
    /// failure names the first element that differs.</summary>
    internal static List<string> Describe(DdArrangement arr)
    {
        var lines = new List<string>();
        string Template(int id) => id >= 0 && id < arr.ChordTemplates.Count
            ? TemplateKey(arr.ChordTemplates[id])
            : $"<missing {id}>";

        lines.AddRange(arr.Phrases.Select((p, i) => $"phrase {i}: {p.Name} max={p.MaxDifficulty} solo={p.IsSolo}"));
        lines.AddRange(arr.PhraseIterations.Select((pi, i) =>
            $"iteration {i}: t={pi.Time} phrase={pi.PhraseId} hero={pi.HeroLevels.Easy}/{pi.HeroLevels.Medium}/{pi.HeroLevels.Hard}"));
        lines.AddRange(arr.Sections.Select(s => $"section {s.Name} t={s.Time}"));
        lines.AddRange(arr.Events.Select(e => $"event {e.Code} t={e.Time}"));
        lines.AddRange(arr.NewLinkedDiffs.Select((d, i) => $"linked {i}: break={d.LevelBreak} phrases={string.Join(",", d.PhraseIds)}"));
        lines.AddRange(arr.ChordTemplates.Select(TemplateKey).Order(StringComparer.Ordinal).Select(k => "template " + k));
        foreach (var level in arr.Levels)
        {
            var d = level.Difficulty;
            lines.Add($"level {d}: {level.Notes.Count} notes, {level.Chords.Count} chords, {level.Anchors.Count} anchors, {level.HandShapes.Count} shapes");
            lines.AddRange(level.Notes.Select(n => $"L{d} note {Note(n)}"));
            lines.AddRange(level.Chords.Select(c =>
                $"L{d} chord t={c.Time} [{Template(c.ChordId)}] mask={c.Mask} notes=" +
                (c.ChordNotes == null ? "null" : string.Join(" | ", c.ChordNotes.Select(Note)))));
            lines.AddRange(level.Anchors.Select(a => $"L{d} anchor t={a.Time} fret={a.Fret} width={a.Width}"));
            lines.AddRange(level.HandShapes.Select(h => $"L{d} shape {h.StartTime}-{h.EndTime} [{Template(h.ChordId)}]"));
        }

        return lines;
    }

    private static string TemplateKey(DdChordTemplate t) =>
        $"{t.Name}/{t.DisplayName} frets={string.Join(",", t.Frets)} fingers={string.Join(",", t.Fingers)}";

    private static string Note(DdNote n)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"t={n.Time} s={n.String} f={n.Fret} sus={n.Sustain} mask={n.Mask}");
        sb.Append(CultureInfo.InvariantCulture, $" lh={n.LeftHand} slide={n.SlideTo} unp={n.SlideUnpitchTo} tap={n.Tap} vib={n.Vibrato}");
        sb.Append(CultureInfo.InvariantCulture, $" bend={n.MaxBend} bends={(n.BendValues is not { Count: > 0 } ? "none" : string.Join(";", n.BendValues.Select(b => $"{b.Time}:{b.Step}")))}");
        return sb.ToString();
    }
}
