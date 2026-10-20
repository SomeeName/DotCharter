using System.Collections;
using System.Reflection;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     A chart states its tuning ONCE, on the ARRANGEMENT. This finishes the
///     migration <see cref="StringDigitTargetingTests" /> started for the keyboard:
///     the project-level <c>ChartData.Tuning</c> is gone, and no reader may derive
///     a lane count or a string name from anything but the arrangement.
///     <para>
///         Why it had to go rather than be populated. The copy defaulted to six
///         E-standard names, so the psarc importer's
///         <c>if (project.Chart.Tuning.Length == 0)</c> back-fill could never fire
///         and EVERY Rocksmith import reported E standard — measured on
///         <c>Galneryus - Brutal Spiral of Emotions_p.psarc</c>, whose two
///         arrangements are Eb standard with the low string at C#
///         (offsets <c>-3,-1,-1,-1,-1,-1</c>, names <c>C# G# C# F# Bb Eb</c>),
///         while the project record said <c>E A D G B e</c>. The <c>.rscp</c>
///         loader lied differently: it stamped standard-tuning NAMES sized by the
///         first arrangement's string count, so an Eb chart came back as E.
///     </para>
///     <para>
///         And populating it correctly would still not have been right, which is
///         what decided this: a project holds SEVERAL arrangements, so a 6-string
///         lead beside a 4-string bass has two right answers and one slot. Every
///         surviving reader wanted a lane count, and a lane count is a property of
///         the arrangement on screen — <c>MinimapOverviewControl</c> drew six lanes
///         over a bass, <c>InspectorViewModel</c> loaded a six-string chord
///         template into it, and the command palette's <c>StringUp</c> disagreed
///         with the identical keybind, which had already been migrated off.
///     </para>
///     <para>
///         Nothing persisted the field — <c>RscpWriter</c> never emitted it, and
///         the recovery store and the checkpoint service both serialize through
///         <c>RscpChartSerializer</c> — so removing it is not a file-format change
///         and needs no migration.
///     </para>
/// </summary>
public sealed class ProjectTuningSingleSourceTests : IDisposable
{
    /// <summary>The real Eb/drop-C# vector off the verified archive.</summary>
    private static readonly int[] GalneryusOffsets = [-3, -1, -1, -1, -1, -1];

    private static readonly string[] GalneryusNames = ["C#", "G#", "C#", "F#", "Bb", "Eb"];

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_tuning_source_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>
    ///     The behavioural guard, driven through the REAL <c>.rscp</c> save/load
    ///     path with the real tunings off the archive: after a round trip the graph
    ///     holds exactly one tuning vector PER ARRANGEMENT and not one more. A
    ///     project-level copy is a third vector that agrees with neither
    ///     arrangement, and that is precisely what this counts.
    /// </summary>
    [Fact]
    public void AfterARealRoundTrip_TheGraphHoldsOneTuningVectorPerArrangement_AndNoOther()
    {
        var project = BuildTwoArrangementProject();
        var path = Path.Combine(_dir, "two-arrangements.rscp");
        new RscpChartSerializer().Save(path, project);
        var loaded = new RscpChartSerializer().Load(path);

        Assert.Equal(2, loaded.Arrangements.Count);

        var vectors = TuningVectorsIn(loaded);
        Assert.True(vectors.Count == loaded.Arrangements.Count,
            $"A chart states its tuning once per ARRANGEMENT. Found {vectors.Count} tuning vector(s) for " +
            $"{loaded.Arrangements.Count} arrangement(s):\n  " +
            string.Join("\n  ", vectors.Select(v => $"{v.Path} = [{string.Join(' ', v.Names)}]")) +
            "\nAn extra one is a project-level copy, and it cannot be right for both a 6-string lead and a " +
            "4-string bass at the same time.");

        // …and the surviving copies are the RIGHT ones, not defaults that merely
        // happen to be the correct length.
        var lead = loaded.Arrangements[0];
        Assert.Equal(GalneryusNames, lead.Tuning);
        Assert.Equal(GalneryusOffsets, lead.TuningPitches.Select(t => t.Step));

        var bass = loaded.Arrangements[1];
        Assert.Equal(4, bass.Tuning.Length);
        Assert.Equal([-2, 0, 0, 0], bass.TuningPitches.Select(t => t.Step));
    }

    /// <summary>
    ///     The same project, asked the question the READERS ask. Before the
    ///     migration every one of them answered from the project record, so the
    ///     bass row here read 6 — six lanes drawn over a four-string instrument,
    ///     a six-string chord template in the inspector, and a <c>StringUp</c>
    ///     ceiling two lanes above anything the renderer draws.
    /// </summary>
    [Fact]
    public void TheLaneCountIsAskedOfTheARRANGEMENT_SoEachOneAnswersForItself()
    {
        var project = BuildTwoArrangementProject();
        var path = Path.Combine(_dir, "lane-counts.rscp");
        new RscpChartSerializer().Save(path, project);
        var loaded = new RscpChartSerializer().Load(path);

        Assert.Equal(6, StringDigitTargeting.StringCountOf(loaded.Arrangements[0]));
        Assert.Equal(4, StringDigitTargeting.StringCountOf(loaded.Arrangements[1]));

        // There is no second place to ask. (Reflection rather than a compile-time
        // reference so this test states the rule rather than merely obeying it.)
        Assert.Empty(TuningMembersOf(typeof(ChartData)));
    }

    /// <summary>
    ///     Source lint, the same shape as
    ///     <see cref="StringDigitTargetingTests.TheThreeStringAddressingCallSitesRouteThroughTheGuard" />:
    ///     the tests above can only prove that the arrangement is the one source —
    ///     this proves no production file goes looking for a second one. A reader
    ///     that resurrects a project-level tuning (or a new field on
    ///     <c>ChartData</c>) fails here rather than shipping six lanes over a bass.
    /// </summary>
    [Fact]
    public void NoProductionCodeDerivesATuningOrALaneCountFromTheProjectRECORD()
    {
        string[] projects =
        [
            "DotCharter.Core", "DotCharter.Services", "DotCharter.Tone", "DotCharter.UI", "DotCharter.Rendering", "DotCharter.Analysis"
        ];

        // "Chart" here is always the PROJECT record (ChartProject.Chart /
        // ChartEditorViewModel.CurrentChart). An Arrangement is never called that.
        string[] forbidden = ["Chart.Tuning", "Chart?.Tuning", "CurrentChart.Tuning", "CurrentChart?.Tuning"];

        var hits = new List<string>();
        foreach (var project in projects)
        {
            foreach (var file in UiSourceTree.EnumerateFiles(RepoPath(project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = UiSourceTree.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    // Comments are stripped first: a `see cref` or a note explaining
                    // the removed field is exactly as inert as a dead registration,
                    // and this file's own siblings carry several on purpose.
                    var code = StripComment(lines[i]);
                    if (forbidden.Any(f => code.Contains(f, StringComparison.Ordinal)))
                        hits.Add($"{Path.GetRelativePath(TestPaths.RepoRoot(), file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(hits.Count == 0,
            "A chart states its tuning once, on the ARRANGEMENT — these read it off the project record " +
            "instead, which is stale for every arrangement but the first and simply absent for the rest:\n  " +
            string.Join("\n  ", hits) +
            "\nAsk StringDigitTargeting.StringCountOf(<the active arrangement>) for a lane count.");
    }

    // ── fixture ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     The shape that makes a single project-level tuning impossible: the two
    ///     real Eb/drop-C# guitar arrangements off
    ///     <c>Galneryus - Brutal Spiral of Emotions_p.psarc</c> reduced to one, plus
    ///     a drop-D bass beside it. Notes and beats are present so the writer has a
    ///     real chart to serialize rather than an empty husk.
    /// </summary>
    private static ChartProject BuildTwoArrangementProject()
    {
        var project = new ChartProject { Bpm = 150 };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 400.0, Bpm = 150, IsMeasure = i % 4 == 0 });

        project.Arrangements.Add(MakeArrangement("Lead", ArrangementType.Lead, GalneryusOffsets));
        project.Arrangements.Add(MakeArrangement("Bass", ArrangementType.Bass, [-2, 0, 0, 0]));
        return project;
    }

    private static Arrangement MakeArrangement(string name, ArrangementType type, int[] offsets)
    {
        var arrangement = new Arrangement
        {
            Name = name,
            ArrangementType = type,
            StringCount = offsets.Length,
            TuningType = "CUSTOM", // what the PSARC importer stamps on every Rocksmith chart
            Tuning = TuningUtilities.GetTuningNames(offsets.Length, offsets)
        };
        foreach (var offset in offsets) arrangement.TuningPitches.Add(new TuningOffset(offset));

        var level = new ChartLevel { DifficultyIndex = 0 };
        for (var i = 0; i < offsets.Length; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i, Fraction.Zero),
                StringIndex = i,
                Fret = 3
            });
        }

        arrangement.Levels.Add(level);
        return arrangement;
    }

    // ── reflection ──────────────────────────────────────────────────────────

    private static IEnumerable<string> TuningMembersOf(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.Contains("Tuning", StringComparison.Ordinal))
            .Select(m => m.Name);

    /// <summary>Every <c>string[] Tuning</c> reachable from the project graph.</summary>
    private static List<(string Path, string[] Names)> TuningVectorsIn(ChartProject project)
    {
        var found = new List<(string, string[])>();
        Walk("project", project, found, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return found;
    }

    private static void Walk(string path, object? node, List<(string, string[])> found, HashSet<object> seen,
        int depth)
    {
        if (node is null || depth > 6) return;
        var type = node.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return;
        if (!seen.Add(node)) return;

        if (node is IEnumerable enumerable and not string)
        {
            var index = 0;
            foreach (var item in enumerable) Walk($"{path}[{index++}]", item, found, seen, depth + 1);
            return;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || prop.GetMethod is null) continue;
            object? value;
            try { value = prop.GetValue(node); }
            catch (TargetInvocationException) { continue; }

            if (prop.Name == "Tuning" && value is string[] names)
            {
                found.Add(($"{path}.{prop.Name}", names));
                continue;
            }

            Walk($"{path}.{prop.Name}", value, found, seen, depth + 1);
        }
    }

    // ── paths ───────────────────────────────────────────────────────────────

    private static string StripComment(string line)
    {
        var i = line.IndexOf("//", StringComparison.Ordinal);
        return i >= 0 ? line[..i] : line;
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
