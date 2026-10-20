using System.Collections;
using System.Reflection;
using System.Text.Json;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Tones;

/// <summary>
///     #181: a tone key is stored in five places (the definition and its body, the Base/A–D
///     slots, the starting tone, the declared names, the tone changes), and renaming or deleting
///     a tone must move ALL of them as one undo node. The seed is REFLECTIVE: every string
///     property named *Tone* on the arrangement and the tone change is set to the old key, so a
///     sixth place added later is seeded too and fails here until the verb learns it.
/// </summary>
public sealed class ToneKeyEditsTests : IDisposable
{
    private const string Old = "lead_dist";
    private const string New = "lead_crunch";

    private static readonly Type[] SeededTypes = [typeof(Arrangement), typeof(ToneChange)];

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-tonekey-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ---- rename: every store, one undo node --------------------------------------

    [Fact]
    public void ARename_LeavesTheOldKeyNowhere_AndOneUndoRestoresEveryPlace()
    {
        var project = SeededProject(out var seeded);
        Assert.True(seeded.Count >= 7, "the seed reached " + seeded.Count + " tone-key properties; the walk is vacuous");
        var before = ChartProjectCloner.Clone(project);
        var history = new HistoryService(new FakeAppLogger());

        var plan = ToneKeyEdits.PlanRename(project, Old, New);
        Assert.Equal(ToneKeyRenameOutcome.Ready, plan.Outcome);
        Assert.True(history.Do(plan.Action!));

        Assert.Empty(StringsNaming(project, Old));
        foreach (var path in seeded)
            Assert.Contains(path, StringsNaming(project, New));
        var definition = Assert.Single(project.ToneDefinitions);
        Assert.Equal((New, New), (definition.Key, definition.Name));
        using (var body = JsonDocument.Parse(definition.Json))
        {
            Assert.Equal(New, body.RootElement.GetProperty("Key").GetString());
            Assert.Equal(New, body.RootElement.GetProperty("Name").GetString());
        }

        history.Undo();
        Assert.False(history.CanUndo, "the rename must be ONE undo node");
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));

        history.Redo();
        Assert.Empty(StringsNaming(project, Old));
    }

    /// <summary>Undo gives every tone change back ITS OWN spelling, not the one the user typed.</summary>
    [Fact]
    public void ACaseInsensitiveRename_UndoesEachChangeToItsOwnSpelling()
    {
        var project = Project();
        var lead = project.Arrangements[0];
        lead.ToneChanges.Add(new ToneChange { Position = Pos(1000, project), ToneName = "Lead_Dist" });
        lead.ToneChanges.Add(new ToneChange { Position = Pos(2000, project), ToneName = " lead_dist" });
        var history = new HistoryService(new FakeAppLogger());

        Assert.True(history.Do(ToneKeyEdits.PlanRename(project, Old, New).Action!));
        Assert.All(lead.ToneChanges, t => Assert.Equal(New, t.ToneName));
        history.Undo();
        Assert.Equal([Old, "Lead_Dist", " lead_dist"], lead.ToneChanges.Select(t => t.ToneName));
    }

    [Fact]
    public void ACaseOnlyRename_IsTheSameTone_NotACollision()
    {
        var project = Project();
        var plan = ToneKeyEdits.PlanRename(project, Old, "Lead_Dist");
        Assert.Equal(ToneKeyRenameOutcome.Ready, plan.Outcome);
        plan.Action!.Execute();
        Assert.Equal("Lead_Dist", project.ToneDefinitions[0].Key);
        Assert.Equal("Lead_Dist", project.Arrangements[0].ToneBase);
    }

    // ---- a collision is refused, never merged -----------------------------------

    [Fact]
    public void ARenameOntoAnotherDefinedTone_IsRefused_AndTouchesNothing()
    {
        var project = Project();
        project.ToneDefinitions.Add(Definition("lead_clean"));
        project.Arrangements[0].ToneB = "lead_clean";
        var before = ChartProjectCloner.Clone(project);

        var plan = ToneKeyEdits.PlanRename(project, Old, "LEAD_CLEAN");

        Assert.Equal(ToneKeyRenameOutcome.Taken, plan.Outcome);
        Assert.Null(plan.Action);
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
    }

    /// <summary>Two undefined keys would fold into one, and nothing would tell which change was which.</summary>
    [Fact]
    public void AnUndefinedKey_RenamedOntoAnotherPlacedUndefinedKey_IsRefused()
    {
        var project = Project();
        project.ToneDefinitions.Clear();
        project.Arrangements[0].ToneChanges.Add(new ToneChange { Position = Pos(1000, project), ToneName = "solo" });

        Assert.Equal(ToneKeyRenameOutcome.Taken, ToneKeyEdits.PlanRename(project, Old, "solo").Outcome);
    }

    [Theory]
    [InlineData("1st tone")]
    [InlineData("")]
    [InlineData("has space")]
    public void AKeyTheGameCannotCarry_IsRefused(string key) =>
        Assert.Equal(ToneKeyRenameOutcome.InvalidKey, ToneKeyEdits.PlanRename(Project(), Old, key).Outcome);

    [Fact]
    public void AKeyTheProjectDoesNotKnow_IsNotFound() =>
        Assert.Equal(ToneKeyRenameOutcome.NotFound, ToneKeyEdits.PlanRename(Project(), "nobody", New).Outcome);

    // ---- projects the old partial renames already broke ----------------------------

    /// <summary>
    ///     The Song tab's old rename moved the tone changes and the starting tone to the new
    ///     key and left the definition (and the slots) on the old one. Renaming the DEFINED tone
    ///     onto the dangling key is the repair: it is in use, but only by references with no body.
    /// </summary>
    [Fact]
    public void ABrokenProject_IsRepairedByRenamingTheDefinedToneOntoTheDanglingKey()
    {
        var project = Project();
        var lead = project.Arrangements[0];
        lead.ToneChanges.Add(new ToneChange { Position = Pos(1000, project), ToneName = New });
        lead.StartingTone = New;
        Assert.Contains(MissingTones(project), m => m == New);

        var plan = ToneKeyEdits.PlanRename(project, Old, New);
        Assert.Equal(ToneKeyRenameOutcome.Ready, plan.Outcome);
        plan.Action!.Execute();

        Assert.Empty(MissingTones(project));
        Assert.All(ToneKeyEdits.Keys(project), k => Assert.True(k.IsDefined, k.Key + " is still undefined"));
    }

    // ---- what the export and the saved file see ------------------------------------

    [Fact]
    public void AfterARename_TheExportReportsNoMissingDefinition()
    {
        var project = Project();
        Assert.Empty(MissingTones(project));
        var halfRenamed = ChartProjectCloner.Clone(project);   // what the Song tab used to leave
        halfRenamed.Arrangements[0].ToneChanges[0].ToneName = New;
        Assert.Equal([New], MissingTones(halfRenamed));

        ToneKeyEdits.PlanRename(project, Old, New).Action!.Execute();

        Assert.Empty(MissingTones(project));
    }

    [Fact]
    public void ARenamedProject_SavedAndReloaded_NamesNoToneItDoesNotDefine()
    {
        var project = Project();
        new HistoryService(new FakeAppLogger()).Do(ToneKeyEdits.PlanRename(project, Old, New).Action!);
        var path = Path.Combine(_dir, "renamed.rscp");

        new RscpChartSerializer().Save(path, project);
        var loaded = new RscpChartSerializer().Load(path);

        Assert.Empty(StringsNaming(loaded, Old));
        Assert.All(ToneKeyEdits.Keys(loaded), k => Assert.True(k.IsDefined, k.Key + " names no definition after the reload"));
        Assert.Equal(New, loaded.Arrangements[0].ToneBase);
        Assert.Equal(New, Assert.Single(loaded.Arrangements[0].ToneChanges).ToneName);
        Assert.Empty(MissingTones(loaded));
    }

    // ---- delete: every store, one undo node ----------------------------------------

    [Fact]
    public void ADelete_RemovesTheToneEverywhere_AndOneUndoRestoresIt()
    {
        var project = SeededProject(out _);
        var before = ChartProjectCloner.Clone(project);
        var history = new HistoryService(new FakeAppLogger());

        Assert.True(history.Do(ToneKeyEdits.BuildDelete(project, Old)!));

        Assert.Empty(StringsNaming(project, Old));
        Assert.Empty(project.ToneDefinitions);
        Assert.All(project.Arrangements, a => Assert.Empty(a.ToneChanges));

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
    }

    // ---- fixtures --------------------------------------------------------------------

    /// <summary>One defined tone, named by Lead's base and slot A, its starting tone, its
    /// declared names and one tone change.</summary>
    private static ChartProject Project()
    {
        var project = new ChartProject { Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        project.ToneDefinitions.Add(Definition(Old));
        var lead = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = Old, ToneA = Old, StartingTone = Old
        };
        lead.ToneNames.Add(Old);
        lead.ToneChanges.Add(new ToneChange { Position = Pos(4000, project), ToneName = Old });
        // A note, or the export check stops at "no notes" and never reaches the tones.
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = Pos(2000, project), StringIndex = 0, Fret = 3 });
        lead.Levels.Add(level);
        project.Arrangements.Add(lead);
        return project;
    }

    /// <summary><see cref="Project" /> plus a second arrangement on which EVERY string property
    /// named *Tone* (and every string collection named so) holds the old key. Returns the paths seeded.</summary>
    private static ChartProject SeededProject(out List<string> seeded)
    {
        var project = Project();
        var rhythm = new Arrangement { ArrangementType = ArrangementType.Rhythm, Name = "Rhythm" };
        var change = new ToneChange { Position = Pos(3000, project) };
        project.Arrangements.Add(rhythm);
        seeded = [];
        Seed(rhythm, "project.Arrangements[1]", seeded);
        Seed(change, "project.Arrangements[1].ToneChanges[0]", seeded);
        rhythm.ToneChanges.Add(change);
        return project;
    }

    private static void Seed(object target, string path, List<string> seeded)
    {
        Assert.Contains(target.GetType(), SeededTypes);
        foreach (var prop in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.Name.Contains("Tone", StringComparison.Ordinal)) continue;
            if (prop.PropertyType == typeof(string) && prop.GetSetMethod() is not null)
            {
                prop.SetValue(target, Old);
                seeded.Add($"{path}.{prop.Name}");
            }
            else if (prop.GetValue(target) is ICollection<string> names)
            {
                names.Add(Old);
                seeded.Add($"{path}.{prop.Name}[{names.Count - 1}]");
            }
        }
    }

    /// <summary>Every path in the project graph whose string value is <paramref name="key" />
    /// (trim- and case-insensitive, the way tone keys match).</summary>
    private static List<string> StringsNaming(ChartProject project, string key)
    {
        var found = new List<string>();
        Walk("project", project, key, found, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return found;
    }

    private static void Walk(string path, object? value, string key, List<string> found, HashSet<object> seen)
    {
        switch (value)
        {
            case null:
                return;
            case string s:
                if (ToneKeyEdits.Same(s, key)) found.Add(path);
                return;
            case IDictionary:
                return;
            case IEnumerable items:
            {
                var i = 0;
                foreach (var item in items) Walk($"{path}[{i++}]", item, key, found, seen);
                return;
            }
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type.IsValueType || !seen.Add(value)) return;
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || prop.GetSetMethod() is null) continue;
            Walk($"{path}.{prop.Name}", prop.GetValue(value), key, found, seen);
        }
    }

    private static List<string> MissingTones(ChartProject project) =>
    [
        .. new ChartExportValidator().Validate(project)
            .Where(f => f.MessageKey == ChartExportValidator.KeyToneNotDefined)
            .Select(f => f.Detail)
    ];

    private static ToneDefinition Definition(string key) =>
        new() { Key = key, Name = key, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared)) };

    private static FractionalPosition Pos(double ms, ChartProject project) => TimelineMath.PositionFromMs(ms, project.Beats);
}
