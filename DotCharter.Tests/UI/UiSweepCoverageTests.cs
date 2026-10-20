using System.Text.RegularExpressions;
using DotCharter.Tests.Perf;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Panels;
using DotCharter.UI.Views.Shell.Menus;

namespace DotCharter.Tests.UI;

/// <summary>
///     The two contracts that decide whether the headless UI sweep
///     (<c>Perf/UiSweep.cs</c>) is looking at the product or at a stand-in.
///     <para>
///         The sweep itself needs the real Avalonia app session and is therefore
///         outside the default gate. These two facts are pure — source text and
///         reflection — so they belong IN it: if either breaks, the sweep goes on
///         passing while measuring something the user never sees, which is the
///         failure mode a guard is supposed to make impossible.
///     </para>
/// </summary>
public sealed class UiSweepCoverageTests
{
    /// <summary>
    ///     A view that does not include its own style grammar must be handed its
    ///     HOST's, or the sweep lays it out bare.
    ///     <para>
    ///         Six of the seven inspector tabs and <c>ChordTemplateEditorView</c>
    ///         carry no <c>StyleInclude</c> at all — <c>InspectorView</c> includes
    ///         <c>InspectorStyles.axaml</c> once, for all of them. Swept alone
    ///         they matched no selector, and the difference is not cosmetic: the
    ///         same tabs measured 723x135 unstyled against 587x221 styled
    ///         (<c>NotesInspectorView</c>), 335x106 against 359x164
    ///         (<c>IssuesInspectorView</c>). Every clipping verdict on those
    ///         surfaces was of a layout the product does not have — real ones
    ///         masked, invented ones reported.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(typeof(IssuesInspectorView))]
    [InlineData(typeof(LyricsInspectorView))]
    [InlineData(typeof(NotesInspectorView))]
    [InlineData(typeof(PhrasesInspectorView))]
    [InlineData(typeof(SongInspectorView))]
    [InlineData(typeof(SoundInspectorView))]
    [InlineData(typeof(SoundBulkCardView))]
    [InlineData(typeof(SoundTaskView))]
    [InlineData(typeof(SoundPlacementView))]
    [InlineData(typeof(SoundShapeView))]
    [InlineData(typeof(SoundFindingsView))]
    [InlineData(typeof(SoundNoteTechniqueView))]
    [InlineData(typeof(SoundChordTechniqueView))]
    [InlineData(typeof(SoundApplyBarView))]
    [InlineData(typeof(ChordTemplateEditorView))]
    public void EveryViewThatInheritsTheInspectorGrammar_IsGivenItByTheSweep(Type viewType)
    {
        var supplied = UiSweepCatalog.InheritedStyleIncludes(viewType);

        Assert.True(
            supplied.Any(s => s.EndsWith("InspectorStyles.axaml", StringComparison.Ordinal)),
            $"{viewType.Name} includes no style grammar of its own and the sweep resolved " +
            $"[{string.Join(", ", supplied)}] from its host views. It is laid out bare, so every " +
            "size the sweep reports for it is of an unstyled tree.");
    }

    /// <summary>
    ///     The other half, and the reason the check above cannot simply hand every
    ///     view every grammar: a view that DOES include its own must be given
    ///     nothing, or it is styled twice and the sweep stops resembling the
    ///     product in the opposite direction.
    /// </summary>
    /// <summary>
    ///     The menu bar's eight menu files (#653) include no grammar of their own:
    ///     <c>MainMenuView</c> includes <c>ShellChromeStyles.axaml</c> for all of
    ///     them, so the sweep must lay each one out under it, not bare.
    /// </summary>
    [Theory]
    [InlineData(typeof(FileMenu))]
    [InlineData(typeof(EditMenu))]
    [InlineData(typeof(TempoMapMenu))]
    [InlineData(typeof(ArrangementsMenu))]
    [InlineData(typeof(VocalsMenu))]
    [InlineData(typeof(ToolsMenu))]
    [InlineData(typeof(ViewMenu))]
    [InlineData(typeof(HelpMenu))]
    public void EveryMenuFile_IsSweptUnderTheShellChromeGrammar(Type menuType)
    {
        Assert.Contains(menuType, UiSweepCatalog.ViewTypes());
        Assert.Contains(UiSweepCatalog.InheritedStyleIncludes(menuType),
            s => s.EndsWith("/Views/Shell/ShellChromeStyles.axaml", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(MixerInspectorView))]
    [InlineData(typeof(InspectorView))]
    [InlineData(typeof(PhraseCatalogView))]
    [InlineData(typeof(StemMixerView))]
    public void AViewThatIncludesItsOwnGrammar_IsGivenNothing(Type viewType) =>
        Assert.Empty(UiSweepCatalog.InheritedStyleIncludes(viewType));

    /// <summary>
    ///     The sweep realizes hidden subtrees the way the PRODUCT does, and by the
    ///     product's own code.
    ///     <para>
    ///         Both halves matter. Reaching them at all is what took the sweep from
    ///         grading whichever Play Mode screen happened to be current (256
    ///         controls checked) to grading the whole shell (4,169) — sixteen
    ///         <c>*ScreenRoot</c>s behind 280 <c>IsVisible</c> gates were never
    ///         measured before. And reaching them by WRITING <c>IsVisible</c> is
    ///         banned for the same reasons it is banned in
    ///         <c>HiddenSubtreePrewarmer</c>: five dialogs grab keyboard focus on
    ///         the transition, <c>SheetMusicView</c> schedules a rebuild, and the
    ///         property is owned by a binding — a sweep that flipped it would
    ///         report the harness fighting the product.
    ///     </para>
    ///     <para>
    ///         Comments are stripped first: this file and the sweep both DISCUSS
    ///         <c>IsVisible</c> at length, and a lint a comment can satisfy is the
    ///         lint that let a dead co-op service look reachable.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheSweep_RealizesHiddenSurfacesThroughTheProductsOwnPrewarm_AndNeverWritesIsVisible()
    {
        var code = SourceText.StripCommentLines(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests", "Perf", "UiSweep.cs")));

        Assert.Contains("IPrewarmableSurfaces", code, StringComparison.Ordinal);
        Assert.Contains("HiddenSubtreePrewarmer.Realize", code, StringComparison.Ordinal);

        var writes = Regex.Matches(code, @"IsVisible\s*=(?!=)|IsVisibleProperty\s*,")
            .Select(m => m.Value)
            .ToList();

        Assert.True(writes.Count == 0,
            "UiSweep.cs writes IsVisible to reach a hidden surface. Realize it by applying its " +
            "template and measuring its VISIBLE children (HiddenSubtreePrewarmer.Realize), never " +
            $"by flipping the gate: {string.Join(", ", writes)}");
    }
}
