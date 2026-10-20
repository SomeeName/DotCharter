using System.Text.RegularExpressions;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Theming;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.Theming;

/// <summary>
///     #745: every surface that colours by string asks <see cref="StringColorSlots" /> — so a
///     7-string's low E is red and its low B grey on the 2D timeline, the gutter, the song map,
///     the phrase preview, the chord box/diagram, the inspector chips and the 3D highway alike.
///     Before, each indexed its palette with the raw string index (<c>i % palette.Length</c>, or
///     <c>Math.Min(i, Count - 1)</c> in the gutter), which painted the low B red and the high E grey.
/// </summary>
public sealed class StringColorIdentityTests
{
    [Fact]
    public void TheDefaultPalette_HasTheRs7StringGrey_AndADarkerEighth()
    {
        Assert.Equal(StringColorSlots.SlotCount, ChartTheme.BaseStringColors.Length);
        Assert.Equal(StringColorSlots.SlotCount, ChartTheme.StringBrushes.Count);
        Assert.Equal(StringColorSlots.SlotCount, FretboardConstants.StringColors.Length);
        Assert.Equal(StringColorSlots.SlotCount, ChartTheme.BaseStringLaneColors.Length);
        Assert.Equal(Color.Parse("#C8C8C8"), ThemePresets.Default.Colors[ThemeTokens.StringExtra]);

        var seventh = Color.Parse("#C8C8C8");
        var eighth = ChartTheme.EighthStringColorFrom(seventh);
        Assert.NotEqual(seventh, eighth);
        Assert.True(eighth.R < seventh.R && eighth.G < seventh.G && eighth.B < seventh.B);
        Assert.Equal(eighth.R, eighth.G);
    }

    [Fact]
    public void The2DPalette_ColoursASevenStringByIdentity()
    {
        // Brushes are the theme's shared instances, so compare against the palette by slot.
        Assert.Same(ChartTheme.StringBrushes[0], ChartTheme.StringBrushFor(1, 7));
        Assert.Same(ChartTheme.StringBrushes[5], ChartTheme.StringBrushFor(6, 7));
        Assert.Same(ChartTheme.StringBrushes[StringColorSlots.SeventhStringSlot], ChartTheme.StringBrushFor(0, 7));
        Assert.Same(ChartTheme.StringBrushes[StringColorSlots.EighthStringSlot], ChartTheme.StringBrushFor(0, 8));
        Assert.Same(ChartTheme.StringBrushes[0], ChartTheme.StringBrushFor(0, 6));

        var rc = new ChartRenderContext { StringCount = 7 };
        Assert.Equal(ChartTheme.BaseStringColors[0], rc.StringColorFor(1));
        Assert.Equal(ChartTheme.BaseStringColors[StringColorSlots.SeventhStringSlot], rc.StringColorFor(0));
        rc.StringCount = 6;
        Assert.Equal(ChartTheme.BaseStringColors[0], rc.StringColorFor(0));
    }

    [Fact]
    public void The3DPalette_ColoursASevenStringByIdentity()
    {
        Assert.Equal(FretboardConstants.StringColors[0], FretboardConstants.StringColorFor(1, 7));
        Assert.Equal(FretboardConstants.StringColors[5], FretboardConstants.StringColorFor(6, 7));
        Assert.Equal(FretboardConstants.StringColors[StringColorSlots.SeventhStringSlot],
            FretboardConstants.StringColorFor(0, 7));
        Assert.Equal(FretboardConstants.StringColors[3], FretboardConstants.StringColorFor(3, 6));
    }

    [Fact]
    public void TheChips_CarryTheirStringsColourSlot()
    {
        var monitor = new LiveStringConfidenceViewModel(FakeLocalization.English());
        monitor.Apply([], ["B", "E", "A", "D", "G", "B", "e"]);
        Assert.Equal([6, 0, 1, 2, 3, 4, 5], monitor.Strings.Select(s => s.ColorSlot));
        monitor.Apply([], LiveStringConfidenceViewModel.LabelsFor(null));
        Assert.Equal([0, 1, 2, 3, 4, 5], monitor.Strings.Select(s => s.ColorSlot));

        var mask = new StringMaskViewModel(new ProjectState(), FakeLocalization.KeysOnly());
        mask.SyncChips([0, 1, 6], 7, null);
        Assert.Equal([StringColorSlots.SeventhStringSlot, 0, 5], mask.Chips.Select(c => c.ColorSlot));
    }

    /// <summary>The surfaces that colour by string. Each must reach the mapping.</summary>
    private static readonly string[] Surfaces =
    [
        "DotCharter.UI/Controls/TimelineGutterControl.cs",
        "DotCharter.UI/Controls/Layers/LaneLayer.cs",
        "DotCharter.UI/Controls/Layers/Helpers/HighwayNoteHeadRenderer.cs",
        "DotCharter.UI/Controls/Layers/Helpers/HighwaySustainRenderer.cs",
        "DotCharter.UI/Controls/MinimapOverviewControl.cs",
        "DotCharter.UI/Controls/PhrasePreviewControl.cs",
        "DotCharter.UI/Controls/ChordBoxControl.cs",
        "DotCharter.UI/Controls/ChordDiagramControl.cs",
        "DotCharter.UI/ViewModels/Editor/LiveStringConfidenceViewModel.cs",
        "DotCharter.UI/ViewModels/Editor/Inspector/StringMaskViewModel.cs",
        "DotCharter.Rendering/Components/BoardGeometryComponent.cs",
        "DotCharter.Rendering/Components/EnhancedHitFeedbackRenderComponent.cs",
        "DotCharter.Rendering/Components/HitSparkRenderComponent.cs",
        "DotCharter.Rendering/Components/LivePlayFeedbackRenderComponent.cs",
        "DotCharter.Rendering/Components/NoteRendering/ChordRenderer.cs",
        "DotCharter.Rendering/Components/NoteRendering/NoteHeadRenderer.cs",
        "DotCharter.Rendering/Components/NoteRendering/SustainRenderer.cs"
    ];

    private static readonly Regex MappingCall =
        new(@"\b(StringSlot|StringColorFor|StringBrushFor|StringColorSlots\.For)\(", RegexOptions.Compiled);

    /// <summary>A string palette indexed by the raw string index — the #745 bug shape.</summary>
    private static readonly Regex RawPaletteIndex = new(
        @"%\s*[\w.]*(StringColors|StringBrushes|stringBrushes|StringPens|OpenPens|StringNotePens|brushes|tints|laneColors|laneHover)\.(Length|Count)\b" +
        @"|Math\.Min\(\s*\w+\s*,\s*\w*[bB]rushes\.Count\s*-\s*\d+\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void EverySurfaceThatColoursByString_UsesTheOneMapping()
    {
        var offenders = new List<string>();
        foreach (var relative in Surfaces)
        {
            var path = Path.Combine(TestPaths.RepoRoot(), relative);
            Assert.True(File.Exists(path), $"{relative} moved — repoint this lint at its new home");
            var text = File.ReadAllText(path);
            if (!MappingCall.IsMatch(text)) offenders.Add($"{relative}: never asks StringColorSlots");
            foreach (Match m in RawPaletteIndex.Matches(text))
                offenders.Add($"{relative}: indexes a string palette by raw string index: {m.Value}");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoOtherChartSurface_IndexesAStringPaletteByRawIndex()
    {
        // The two decorative backdrops draw six unlabelled strings, not a chart; the theme and the
        // converter (which receives a SLOT) are the palette's own plumbing.
        string[] exempt =
        [
            "MenuBackdropControl.cs", "HeroStringsBackdrop.cs", "ChartTheme.cs", "StringColorSlotBrushConverter.cs",
            "FretboardConstants.cs"
        ];
        string[] roots =
        [
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"),
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering")
        ];
        var offenders = new List<string>();
        var scanned = 0;
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var files = roots.SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(obj) && !exempt.Contains(Path.GetFileName(file)));
        foreach (var file in files)
        {
            scanned++;
            foreach (Match m in RawPaletteIndex.Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetRelativePath(TestPaths.RepoRoot(), file)}: {m.Value}");
        }

        Assert.True(scanned > 100, $"scanned only {scanned} files — the roots moved");
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
}
