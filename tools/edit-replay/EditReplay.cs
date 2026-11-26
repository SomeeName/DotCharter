// .NET 10 file-based program: how many of a charter's edits would a fresh Guitar Pro import (plus
// Link all) have saved? Imports the score with the code of the tree this file sits in, through the
// app's own apply path (GpImportApplier, then LinkSuggestionFinder.LinkAll), diffs every arrangement
// the charter edited against their finished project over the bars they edited, and counts the edits
// still needed, with and without links. Usage and the counting rules: tools/edit-replay/README.md.
#:project ../../DotCharter.Services/DotCharter.Services.csproj
#:include EditReplayCore.cs
#:property PublishAot=false
#:property Nullable=enable

global using DotCharter.Core.Domain.Events;
global using DotCharter.Core.Domain.Guitar;
global using DotCharter.Core.Domain.Primitives;
global using DotCharter.Core.Domain.Project;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DotCharter.Localization;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Linking;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Import.GuitarPro;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.State;
using DotCharter.Tone.Logging;
using DotCharter.Tools.EditReplay;

var self = Process.GetCurrentProcess();
self.PriorityClass = ProcessPriorityClass.Idle;
if (OperatingSystem.IsWindows() && Environment.ProcessorCount >= 16) self.ProcessorAffinity = (IntPtr)0xFF00;

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var gpPath = Arg("--gp");
var projectPath = Arg("--project");
var logPath = Arg("--log");
if (gpPath == null || projectPath == null || logPath == null)
{
    Console.Error.WriteLine("usage: dotnet run tools/edit-replay/EditReplay.cs -- --gp <score.gp> --project <finished.dcproj> " +
                            "--log <app.log> [--bars <first>-<last>] [--arrangement <name>] [--tsv <out.tsv>]");
    return 2;
}

// 1. The fresh import, exactly as File > Import > Guitar Pro (add new) and Link all run it.
var config = new AppConfig();
var state = new ProjectState { ActiveProject = new ChartProject() };
var importer = new GpImporter();
var tracks = importer.GetAvailableTracks(gpPath);
var parsed = importer.ImportTracksWithMetadata(gpPath, [.. tracks.Select(t => t.Id)], GpImportSettings.FromConfig(config));
var applier = new GpImportApplier(state, Stub.Of<IChartEditorSurface>(), Stub.Of<IEditorTransport>(),
    Stub.Of<IConfigurationService>(config), Stub.Of<IChartMaintenanceService>(), Stub.Of<IAppLogger>(),
    Stub.Of<ILocalizationService>());
applier.Apply(GpImportMode.AddNew, gpPath, [.. tracks.Select(t => new GpTrackTarget(t.Id, t.Name, null))], parsed, null);
var fresh = state.ActiveProject!;
var score = applier.TakeLastImport()!;
var rule = LinkSuggestionFinder.RuleFor(config);
var suggestions = LinkSuggestionFinder.Find(fresh, score.Arrangements, score.SourceBars ?? [], rule);
var linked = LinkSuggestionFinder.LinkAll(fresh, suggestions, rule);
linked.Action?.Execute();
var groups = fresh.LinkGroups
    .Select(g => (IReadOnlyList<PlacementSpan>)[.. g.Placements.Where(p => p.Arrangement != null)
        .Select(p => new PlacementSpan(p.Arrangement!.Name, p.Position.DoubleValue, p.EndPosition.DoubleValue))])
    .ToList();

// 2. The charter's result and their session.
var charter = new RscpChartSerializer().Load(projectPath);
var session = LoggedSession.Parse(File.ReadLines(logPath));
var bars = BarMap.Of(fresh.Beats);
var (firstBar, lastBar) = Arg("--bars") is { } range
    ? (int.Parse(range.Split('-')[0], CultureInfo.InvariantCulture), int.Parse(range.Split('-')[1], CultureInfo.InvariantCulture))
    : session.EditedBeats.Count > 0
        ? (bars.BarOf(session.EditedBeats.Min()), bars.BarOf(session.EditedBeats.Max()))
        : (1, int.MaxValue);
var names = Arg("--arrangement") is { } one ? [one] : session.Edits.Keys.ToList();

// 3. Diff and count, per edited arrangement.
var all = new List<ReplayEdit>();
Console.WriteLine($"links: {groups.Count} groups after Link all ({linked.Skipped.Count} skipped); bars {firstBar}-{lastBar}; " +
                  $"log: {session.Undos} undo(s), {session.Redos} redo(s)");
Console.WriteLine("actions: " + string.Join(", ", session.EditsByAction.OrderByDescending(a => a.Value).Select(a => $"{a.Key}={a.Value}")));
var categories = Enum.GetValues<EditCategory>();
Console.WriteLine($"{"arrangement",-24} {"",-8} {"total",6} " + string.Join(" ", categories.Select(c => $"{c,9}")));
foreach (var name in names)
{
    var freshArrangement = fresh.Arrangements.FirstOrDefault(a => a.Name == name);
    var charterArrangement = charter.Arrangements.FirstOrDefault(a => a.Name == name);
    if (freshArrangement == null || charterArrangement == null)
    {
        Console.WriteLine($"{name,-24} not in both the import and the project; skipped");
        continue;
    }

    var edits = ReplayDiff.Diff(name, ChartFacts.Of(freshArrangement), ChartFacts.Of(charterArrangement), bars, firstBar, lastBar);
    all.AddRange(edits);
    Console.WriteLine($"{name,-24} {"logged",-8} {session.Edits.GetValueOrDefault(name),6}");
    Row(name, "needed", ReplayDiff.CountByCategory(edits));
    Row(name, "linked", ReplayDiff.CountWithLinks(edits, groups));
}

var tsv = new List<string> { "arrangement\tbar\tbeat\tcategory\tkind\tcounted\tfresh\tcharter" };
tsv.AddRange(all.Select(e =>
    $"{e.Arrangement}\t{e.Bar}\t{(e.Position - FirstBeatOf(e.Bar)).ToString("0.###", CultureInfo.InvariantCulture)}\t{e.Category}\t{e.Kind}\t{(e.Counted ? 1 : 0)}\t{e.Fresh}\t{e.Charter}"));
if (Arg("--tsv") is { } tsvPath) File.WriteAllLines(tsvPath, tsv);
else tsv.ForEach(Console.WriteLine);
return 0;

void Row(string name, string label, Dictionary<EditCategory, int> counts) =>
    Console.WriteLine($"{"",-24} {label,-8} {counts.Values.Sum(),6} " + string.Join(" ", categories.Select(c => $"{counts[c],9}")));

double FirstBeatOf(int bar)
{
    var beat = 0;
    while (bars.BarOf(beat) < bar && beat < 1_000_000) beat++;
    return beat;
}

/// <summary>The app services the apply path needs and this tool has no use for: every call returns its
/// type's default; <c>RunAsSingleUndoStep</c> runs the mutation, <c>LoadAppConfig</c> returns the config.</summary>
public class Stub : DispatchProxy
{
    private object? _config;

    public static T Of<T>(object? config = null) where T : class
    {
        var proxy = Create<T, Stub>();
        ((Stub)(object)proxy)._config = config;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method == null) return null;
        if (method.Name == "RunAsSingleUndoStep" && args?[0] is Action mutate)
        {
            mutate();
            return true;
        }

        if (method.Name == "LoadAppConfig") return _config;
        var type = method.ReturnType;
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return "";
        if (type == typeof(Task)) return Task.CompletedTask;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
