using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Setlists;
using DotCharter.Services.Gameplay.Stats;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     <b>Reachability for the rest of <c>Gameplay/</c>.</b>
///     <c>UI/CoopReachabilityTests</c> covers <c>Gameplay/Coop/</c> — the folder
///     that shipped registered-and-dead for a whole release — and stops at its
///     own directory. Everything else in this folder had NO such guard, and it is
///     the same shape of subsystem: seven services registered in the composition
///     root, resolved only through their interfaces, each with its own passing
///     unit tests whether or not anything ever calls it.
///     <para>
///         Two rules, one per level of the fault. A TYPE nobody resolves is a dead
///         feature; a VERB nobody drives is the same fault one level in — and this
///         file was written because
///         <c>ILeaderboardService.GetSongLeaderboardAnyArrangement</c> was exactly
///         that: declared, implemented, unit-tested, registered, and called by
///         nothing but its own test. It was removed in 0.58 rather than left to
///         read as shipped.
///     </para>
///     <para>
///         The composition root (<c>App.axaml.cs</c> and its <c>App.*.cs</c>
///         partials) is excluded on purpose — <b>a registration is not a use</b> —
///         and comments are stripped first, because a <c>see cref</c> is exactly
///         as inert as an unresolved registration.
///     </para>
/// </summary>
public sealed class GameplayReachabilityTests
{
    /// <summary>The DI-bound surfaces of <c>Gameplay/</c> outside <c>Coop/</c>.
    /// Listed rather than discovered so a DELETED binding is a failure here
    /// instead of a silently shorter sweep.</summary>
    private static readonly Type[] Surfaces =
    [
        typeof(IPlayerProfileService),
        typeof(IGameplayStatsService),
        typeof(ILeaderboardService),
        typeof(IPlaySetlistStore)
    ];

    /// <summary>The list above must stay complete: every public interface declared
    /// under <c>Gameplay/</c> (bar <c>Coop/</c>, which has its own file) is a
    /// binding surface and belongs in the sweep.</summary>
    [Fact]
    public void TheSweptSurfacesAreEveryGameplayInterface()
    {
        var declared = UiSourceTree
            .EnumerateFiles(GameplayFolder(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}Coop{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(SourceText.StripCommentLines(UiSourceTree.ReadAllText(f)), @"public interface (\w+)")
                .Select(m => m.Groups[1].Value))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Surfaces.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(), declared);
    }

    /// <summary>
    ///     Each surface is named by product code OUTSIDE <c>Gameplay/</c> and
    ///     outside the composition root — i.e. something resolves it and holds it,
    ///     not merely registers it.
    /// </summary>
    [Fact]
    public void EveryGameplayServiceSurface_IsResolvedByProductCodeOutsideItsOwnFolder()
    {
        var sources = ProductSourcesOutsideGameplay();

        foreach (var surface in Surfaces)
        {
            Assert.True(sources.Any(code => Regex.IsMatch(code, $@"\b{Regex.Escape(surface.Name)}\b")),
                $"{surface.Name} is named by no product code outside Services/Gameplay and the composition " +
                "root. Nothing resolves it, so every implementation behind it is dead code that still " +
                "compiles and still has passing tests.");
        }
    }

    /// <summary>
    ///     …and every VERB on those surfaces is actually called. A service the app
    ///     resolves but never drives is the same fault one level in: the type is
    ///     alive, the method is not, and nothing about the build says so.
    ///     <para>
    ///         A miss here has exactly two honest fixes — wire the verb up, or
    ///         delete it. Do not add it to an exclusion list; the whole point is
    ///         that "declared, implemented, tested" is not "shipped".
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryGameplayServiceVerb_IsDrivenByProductCode()
    {
        var sources = ProductSourcesOutsideGameplay();
        var undriven = new List<string>();

        foreach (var surface in Surfaces)
        {
            foreach (var member in surface.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                // Property getters/setters and event add/remove are reached
                // through the property/event name, which is the member below.
                if (member is MethodInfo { IsSpecialName: true }) continue;

                var pattern = member is MethodInfo
                    ? $@"\.{Regex.Escape(member.Name)}\s*(\(|<)"   // call (possibly generic)
                    : $@"\.{Regex.Escape(member.Name)}\b";          // property / event read
                if (!sources.Any(code => Regex.IsMatch(code, pattern)))
                    undriven.Add($"{surface.Name}.{member.Name}");
            }
        }

        Assert.True(undriven.Count == 0,
            "these gameplay verbs are declared, implemented and registered — and driven by nothing " +
            "outside Services/Gameplay and the composition root, which makes them dead code that reads " +
            "as shipped: " + string.Join(", ", undriven));
    }

    // ---- helpers ------------------------------------------------------------

    private static string GameplayFolder()
        => Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Gameplay");

    private static IReadOnlyList<string> ProductSourcesOutsideGameplay()
    {
        var gameplay = GameplayFolder();
        var sources = new List<string>();
        // Presentation is product code too: the settings screens drive services from there.
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone", "DotCharter.Presentation" })
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.StartsWith(gameplay, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // App is ONE class over several partial files: a registration is
                // not a use in any of them.
                if (Path.GetFileName(file).StartsWith("App.", StringComparison.OrdinalIgnoreCase)) continue;
                sources.Add(SourceText.StripCommentLines(UiSourceTree.ReadAllText(file)));
            }
        }

        Assert.NotEmpty(sources);
        return sources;
    }
}
