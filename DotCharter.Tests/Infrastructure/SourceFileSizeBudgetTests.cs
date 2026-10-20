namespace DotCharter.Tests.Infrastructure;

public sealed class SourceFileSizeBudgetTests
{
    public const int DefaultBudgetBytes = 60 * 1024;

    private const int MaxSlackBytes = 8 * 1024;

    private sealed record Budget(string Path, int CeilingBytes, string Reason);

    // Every entry below was DECIDED in #297: kept, with the reason it is not split.
    // Split candidates go through a real collaborator (a type or a child view) and
    // leave the list; ChartSpacingEnforcer (LinkNextRepair) and SheetMusicView
    // (SheetMusicViewLayers, SheetHitIndex) left it that way.
    private static readonly Budget[] OverBudget =
    [
        new("DotCharter.Tone.UI/Views/PlayMode/AmpFxRigView.axaml", 136 * 1024,
            "kept: stage, gear bar, routing map and footer share ONE namescope, and 86 of its 106 x:Names are " +
            "looked up by name from tests and the shell; a child view would move them out of reach. The gear " +
            "tile, the board lane, the styles and Tones mode are already their own files"),
        new("DotCharter.Rendering/Vulkan/VulkanRenderer.cs", 76 * 1024,
            "kept: Vulkan synchronization and lifetime code, untouched until the owner asks for it by name " +
            "(.claude/rules/tool-selection.md, Simplification limits)"),
        new("DotCharter.Tone/Audio/Dsp/Presets/DspTonePresetLibrary.cs", 76 * 1024,
            "kept: data, not logic. The built-in presets written through small C# builders and loudness-gated by " +
            "DspTonePresetLibraryTests; a resource file would need its own loader for no behaviour gain"),
        new("DotCharter.Services/Parsing/GuitarPro/GpLegacyImporter.cs", 72 * 1024,
            "kept: one sequential GP3-5 reader. Every Read* method advances the same GpLegacyBinaryReader in " +
            "file order, so any split cuts the format in half"),
        new("DotCharter.Services/Export/Sheet/AlphaTexExporter.cs", 68 * 1024,
            "kept: one alphaTex writer under the export-boundary rules; its span and beat maps are what " +
            "SheetMusicView hit-tests through, pinned by the AlphaTex* export tests"),
        new("DotCharter.Services/Export/Sng/SngFromDomainMapper.cs", 68 * 1024,
            "kept: the SNG the game reads, under the export-boundary rules; it maps every chart entity to " +
            "the chord-template indices RsChordTemplateExport assigns, so it stays one reviewable unit"),
        new("DotCharter.UI/Controls/ChartRenderer.cs", 64 * 1024,
            "kept: the composed-Vulkan timeline's frame content, input and hit-testing; rendering code, split " +
            "only when the owner asks for it by name (.claude/rules/tool-selection.md)"),
    ];

    [Fact]
    public void NoProductSourceFileOutgrowsItsBudget()
    {
        var root = TestPaths.RepoRoot();
        var budgets = OverBudget.ToDictionary(b => b.Path, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var file in ProductSourceFiles(root))
        {
            var relative = Relative(root, file);
            var size = NormalizedSize(file);
            var ceiling = budgets.TryGetValue(relative, out var budget)
                ? budget.CeilingBytes
                : DefaultBudgetBytes;

            if (size > ceiling)
            {
                failures.Add($"{relative}: {size:N0} bytes, budget {ceiling:N0}");
            }
        }

        Assert.True(failures.Count == 0,
            "These files outgrew their size budget. Split them into real collaborators " +
            "(a new type or a new view), not partials and not a higher ceiling:\n  " +
            string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryOverBudgetEntryIsStillNeededAndTight()
    {
        var root = TestPaths.RepoRoot();
        var failures = new List<string>();

        foreach (var budget in OverBudget)
        {
            var full = Path.Combine(root, budget.Path);
            if (!File.Exists(full))
            {
                failures.Add($"{budget.Path}: file no longer exists, delete the entry");
                continue;
            }

            var size = NormalizedSize(full);
            if (size <= DefaultBudgetBytes)
            {
                failures.Add($"{budget.Path}: {size:N0} bytes is inside the default budget, delete the entry");
            }
            else if (budget.CeilingBytes - size > MaxSlackBytes)
            {
                failures.Add($"{budget.Path}: {size:N0} bytes against a ceiling of {budget.CeilingBytes:N0}, lower the ceiling");
            }
        }

        Assert.True(failures.Count == 0,
            "The over-budget list may only shrink. When a listed file shrinks, lower or delete its entry:\n  " +
            string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryOverBudgetEntryIsListedOnceWithAReason()
    {
        Assert.Equal(OverBudget.Length, OverBudget.Select(b => b.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(OverBudget, b => Assert.False(string.IsNullOrWhiteSpace(b.Reason), b.Path));
        Assert.All(OverBudget, b => Assert.True(b.CeilingBytes > DefaultBudgetBytes, b.Path));
    }

    [Fact]
    public void TheScanSeesTheProductTree()
    {
        var root = TestPaths.RepoRoot();
        var files = ProductSourceFiles(root).Select(f => Relative(root, f)).ToHashSet(StringComparer.Ordinal);

        Assert.True(files.Count > 1000, $"only {files.Count} product source files found; the scan is broken");
        Assert.Contains("DotCharter.UI/Views/PlayMode/PlayModeView.axaml", files);
        Assert.Contains("DotCharter.Core/Domain/Guitar/Chord.cs", files);
        Assert.DoesNotContain(files, f => f.StartsWith("DotCharter.Tests/", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Contains("/obj/", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ProductSourceFiles(string root)
    {
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (Path.GetFileName(project) == "DotCharter.Tests")
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(project, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".axaml", StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = Relative(root, file);
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static int NormalizedSize(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var carriageReturns = 0;
        foreach (var b in bytes)
        {
            if (b == (byte)'\r')
            {
                carriageReturns++;
            }
        }

        return bytes.Length - carriageReturns;
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
}
