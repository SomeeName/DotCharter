using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Per-profile tone rigs are a service feature with NO UI of its own — the
///     pedalboard rebuilds itself off <c>NodesChanged</c> and never learns whose
///     rig it is showing. That is exactly the shape that ships dark: an
///     <c>AddSingleton</c> factory is not invoked until someone asks for the type,
///     so a registered-but-unresolved <c>ProfileToneRigCoordinator</c> would
///     compile, keep every one of its own unit tests green, and leave every
///     profile silently sharing one board. <c>CoopRigApplier</c> spent a release
///     exactly that way.
///     <para>
///         These read the REAL source files. They deliberately exclude nothing
///         from the composition root — unlike <c>CoopReachabilityTests</c>, whose
///         subject is a service the app must USE — because here the registration
///         and the resolution both live in <c>App*.cs</c> and the resolution is
///         the thing being asserted. Comments are stripped first: a
///         <c>see cref</c> is exactly as inert as no mention at all.
///     </para>
/// </summary>
public sealed class ProfileToneRigReachabilityTests
{
    [Fact]
    public void TheStoreAndTheCoordinatorAreRegistered()
    {
        var app = CompositionRootSource();

        Assert.Contains("AddSingleton<DotCharter.Tone.Audio.Dsp.IProfileToneRigStore>", app);
        Assert.Contains("new DotCharter.Tone.Audio.Dsp.ProfileToneRigStore(", app);
        Assert.Contains("AddSingleton<DotCharter.Services.Audio.Dsp.ProfileToneRigCoordinator>", app);
    }

    /// <summary>
    ///     …and a registration is not a use. The coordinator must be RESOLVED and
    ///     DRIVEN at startup, or the rig neither follows a profile switch nor
    ///     restores at all.
    /// </summary>
    [Fact]
    public void TheCoordinatorIsResolvedAndStartedAtStartup()
    {
        var app = CompositionRootSource();

        Assert.Matches(
            new Regex(@"GetRequiredService<DotCharter\.Services\.Audio\.Dsp\.ProfileToneRigCoordinator>\(\)\s*\.Start\(\)",
                RegexOptions.Singleline),
            app);
    }

    /// <summary>
    ///     Nothing else may start the tone sessions. The coordinator OWNING
    ///     <c>StartAll</c> is what makes it impossible for it to go dead quietly:
    ///     a second starter would restore a rig from whichever profile the store
    ///     happened to resolve, and the coordinator could then be deleted without
    ///     anyone noticing until a machine had two profiles on it.
    /// </summary>
    [Fact]
    public void OnlyTheCoordinatorStartsTheToneSessions()
    {
        var callers = ProductSourceFiles()
            .Where(f => !f.EndsWith("ProfileToneRigCoordinator.cs", StringComparison.Ordinal))
            .Where(f => SourceText.StripComments(UiSourceTree.ReadAllText(f)).Contains(".StartAll()", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(callers.Count == 0,
            "ToneSessionSet.StartAll must be called only by ProfileToneRigCoordinator — a second starter " +
            "restores a rig from an unpinned profile scope. Found in: " + string.Join(", ", callers));
    }

    /// <summary>
    ///     Lint the VERBS, not just the type. A coordinator that subscribes but
    ///     flushes AFTER rescoping writes the outgoing player's last few seconds
    ///     into the incoming player's file, which is the exact leak this feature
    ///     exists to prevent — and it is invisible in every other test that does
    ///     not measure the order.
    /// </summary>
    [Fact]
    public void TheCoordinatorFlushesBeforeItRescopesAndReloadsAfter()
    {
        var source = SourceText.StripComments(Read("DotCharter.Services", "Audio", "Dsp", "ProfileToneRigCoordinator.cs"));

        Assert.Contains("ActiveProfileChanged += OnActiveProfileChanged", source);

        var flush = source.IndexOf("_sessions.FlushAll()", StringComparison.Ordinal);
        var rescope = source.IndexOf("_store.Rescope()", flush + 1, StringComparison.Ordinal);
        var reload = source.IndexOf("_sessions.ReloadAll()", StringComparison.Ordinal);

        Assert.True(flush >= 0, "the coordinator never flushes the outgoing profile's rig");
        Assert.True(rescope > flush,
            "the coordinator rescopes before flushing — the outgoing player's last edits land in the " +
            "INCOMING player's file");
        Assert.True(reload > rescope,
            "the coordinator reloads before rescoping — it would re-apply the profile it just left");
    }

    /// <summary>
    ///     The rig must be READ and WRITTEN through the profile store and nowhere
    ///     else. A surviving <c>AppConfig.ActiveToneRig</c> reader would quietly
    ///     put one board back in front of every profile; the two fields are
    ///     migration input only.
    /// </summary>
    [Fact]
    public void NoProductCodeReadsOrWritesTheGlobalRigFieldsOutsideTheMigration()
    {
        var offenders = ProductSourceFiles()
            .Where(f => !f.EndsWith("ProfileToneRigStore.cs", StringComparison.Ordinal))
            // The two declarations: RigConfig (DotCharter.Tone) holds the fields,
            // AppConfig derives from it.
            .Where(f => !f.EndsWith("AppConfig.cs", StringComparison.Ordinal))
            .Where(f => !f.EndsWith("RigConfig.cs", StringComparison.Ordinal))
            .Where(f =>
            {
                var source = SourceText.StripComments(UiSourceTree.ReadAllText(f));
                return source.Contains("ActiveToneRig", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "AppConfig.ActiveToneRig / ActiveToneRigsByPlayer are migration input only — the live rig is " +
            "the active profile's tone-rig.json. Found in: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The store's own guard: it must never resolve the active profile per
    ///     access. A store that did would already be pointing at the incoming
    ///     profile when the coordinator flushes, and no ordering could save it.
    /// </summary>
    [Fact]
    public void TheStorePinsItsScopeInsteadOfResolvingItPerAccess()
    {
        var source = SourceText.StripComments(Read("DotCharter.Tone", "Audio", "Dsp", "ProfileToneRigStore.cs"));

        Assert.Contains("private Guid? _scopedProfileId;", source);
        // The active profile is read in exactly one place, behind the pin. The
        // store sees it through IToneRigProfileScope.ActiveProfileId, which
        // IPlayerProfileService implements as ActiveProfile.Id.
        var reads = Regex.Matches(source, @"\.ActiveProfileId\b").Count;
        Assert.True(reads == 1,
            $"the rig store reads IToneRigProfileScope.ActiveProfileId {reads} times; it must resolve it once " +
            "into the pinned scope, or a flush ordered before a profile switch lands on the wrong player");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>Every product `.cs` file in the two projects a rig can be reached
    /// from. Build output is excluded — a stale DLL's generated source would make
    /// any of these lints report a file nobody edits.</summary>
    private static IEnumerable<string> ProductSourceFiles()
        => new[] { "DotCharter.Services", "DotCharter.Tone", "DotCharter.UI" }
            .SelectMany(project => UiSourceTree.EnumerateFiles(
                Path.Combine(TestPaths.RepoRoot(), project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal));

    /// <summary>The composition root is ONE class spread over several partial
    /// files, so a lint over the registrations must read all of them.</summary>
    private static string CompositionRootSource() =>
        string.Concat(UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => SourceText.StripComments(UiSourceTree.ReadAllText(p))));

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
