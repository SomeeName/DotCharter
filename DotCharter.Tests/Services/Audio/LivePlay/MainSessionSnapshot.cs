using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Executes a frozen copy of the live-play session source (the judging before the pass-wrap change,
///     taken from commit 034b63ac, see <c>DotCharter.Tests/Fixtures/PROVENANCE.md</c>) in isolation, with
///     shared domain/DSP types. The reference is a fixture file, not a git ref, so it cannot move and
///     needs no git history to run. The global usings come from the current services project.
/// </summary>
internal static class MainSessionSnapshot
{
    private const string ReferenceSessionPath = "DotCharter.Tests/Services/Audio/LivePlay/Fixtures/LivePlaySessionService.reference.cs.txt";

    private static readonly Lazy<Assembly> Snapshot = new(Compile);
    public static Type SessionType => Snapshot.Value.GetType("MainSnapshot.LivePlaySessionService")!;

    public static void Hit(object session, double at)
        => Snapshot.Value.GetType("MainSnapshot.Driver")!.GetMethod("Hit")!.Invoke(null, [session, at]);

    public static LivePlayEvent? Attack(object session, double at, int[] pitches, List<LivePlayEvent> extra)
        => (LivePlayEvent?)Snapshot.Value.GetType("MainSnapshot.Driver")!.GetMethod("Attack")!.Invoke(null, [session, at, pitches, extra]);

    private static string ReadRepoFile(string path) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), path));

    private static Assembly Compile()
    {
        var source = ReadRepoFile(ReferenceSessionPath)
            .Replace("namespace DotCharter.Services.Audio.LivePlay;", "namespace MainSnapshot;\nusing DotCharter.Services.Audio.LivePlay;");
        source += "\npublic static class Driver { public static void Hit(LivePlaySessionService s, double at) => s.Offer(new[] { new LivePitch(43, 0f, 1f) }, [], at, LiveTechniqueFlags.None, 0f); public static LivePlayEvent? Attack(LivePlaySessionService s, double at, int[] midi, List<LivePlayEvent> extra) => s.Offer(midi.Select(p => new LivePitch(p, 0f, 1f)).ToArray(), [], at, LiveTechniqueFlags.None, 0f, extra); }";
        var globals = ReadRepoFile("DotCharter.Services/GlobalUsing.cs") + "\nglobal using System; global using System.Collections.Generic; global using System.Linq;";
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(new[] { typeof(Arrangement).Assembly.Location, typeof(LivePlaySessionService).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("MainSessionSnapshot",
            [CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(globals)],
            paths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        output.Position = 0;
        return new AssemblyLoadContext("main-session-reference", isCollectible: true).LoadFromStream(output);
    }
}
