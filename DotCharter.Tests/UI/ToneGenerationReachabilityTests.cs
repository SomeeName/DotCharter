using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The tone generator is REACHED by the app, not merely present in it.
///     <para>
///         This repo has shipped the same fault four times: a service that
///         compiles, keeps its own passing unit tests, and is resolved or invoked
///         by nobody — <c>CoopRigApplier</c> spent a whole release that way with
///         every method on it dead code. The tone generator is exactly that
///         shape: five pure types with 100+ passing tests, which prove the
///         arithmetic and prove nothing about whether a charter can press
///         anything.
///     </para>
///     <para>
///         So this pins the chain end to end — the command exists, the markup
///         binds it, and the command reaches the service. Break any link and this
///         goes red instead of the feature silently becoming a library.
///     </para>
/// </summary>
public sealed class ToneGenerationReachabilityTests
{
    /// <summary>The run the dialog's command hands the arrangement to.</summary>
    private const string CommandFile = "DotCharter.Services/Editing/Tones/ToneGenerationRun.cs";

    private const string DialogFile = "DotCharter.Presentation/Screens/Dialogs/ToneDesignerViewModel.Import.cs";

    private const string ViewFile = "DotCharter.UI/Views/Dialogs/ToneDesignerDialogView.axaml";

    private const string PaletteFile = "DotCharter.UI/ViewModels/CommandPalette/CommandCatalog.cs";

    private const string RigFile = "DotCharter.UI/ViewModels/Shell/ToneRigViewModel.cs";

    /// <summary>The generated command is bound by the dialog's markup — without
    /// this the button does not exist and nothing can start the feature.</summary>
    [Fact]
    public void TheDialogBindsTheGenerateCommand()
    {
        var markup = UiSourceTree.ReadAllText(RepoPath(ViewFile));

        Assert.Contains("GenerateTonesFromAudioCommand", markup, StringComparison.Ordinal);
        Assert.Contains("Dialog_Tones_GenerateFromAudio", markup, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The dialog's command hands the arrangement to the run: a bound button that reaches
    ///     nothing is a dead feature. And it does so OFF the UI thread (#215): the analysis decodes
    ///     the song, and it used to run inline and freeze the window once per tone change.
    /// </summary>
    [Fact]
    public void TheDialogCommandRunsTheGeneration_AndAnalysesOffTheUiThread()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(DialogFile)));

        Assert.Contains("private async Task GenerateTonesFromAudioAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() => _tools.Estimate.Analyse(", source, StringComparison.Ordinal);
        Assert.Contains("_tools.Estimate.Apply(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_tools.Estimate.Run(", source, StringComparison.Ordinal);

        var vm = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(DialogFile.Replace(".Import.cs", ".cs"))));
        Assert.Contains("GenerateTonesFromAudioCommand = new AsyncRelayCommand(GenerateTonesFromAudioAsync)", vm,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the command actually calls the service. A bound button that
    ///     reaches nothing is the same dead feature with a nicer failure: the
    ///     charter presses it and the app does not answer.
    /// </summary>
    [Fact]
    public void TheCommandReachesTheGenerationService()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(CommandFile)));

        Assert.Contains("new ToneGenerationService(", source, StringComparison.Ordinal);
        Assert.Contains(".Generate(", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The owner's rule, pinned as a lint because it is the one thing a
    ///     future change would most plausibly "improve": <i>the tone PLACEMENT is
    ///     not generated</i>. The command may read tone changes and must never
    ///     add, move or remove one.
    /// </summary>
    [Fact]
    public void TheCommandNeverWritesToneChanges()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(CommandFile)));

        Assert.DoesNotContain("ToneChanges.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToneChanges.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToneChanges.Clear", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Position =", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A generated tone is declared in a slot, or the game cannot switch to
    ///     it — the D01 fault the export check reports. Generating one and
    ///     leaving it undeclared would author that fault deliberately.
    /// </summary>
    [Fact]
    public void AGeneratedToneIsDeclaredInASlot()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(CommandFile)));

        Assert.Contains("DeclareInAFreeSlot", source, StringComparison.Ordinal);
        Assert.Contains("arrangement.ToneA", source, StringComparison.Ordinal);
        Assert.Contains("arrangement.ToneD", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Drums, bass and vocals are never analysed for a guitar tone. The
    ///     command builds the stem dictionary itself, so the filter has to be
    ///     applied HERE — <c>ToneAudioSource</c> cannot refuse a stem it was
    ///     handed under the wrong name.
    /// </summary>
    [Fact]
    public void TheCommandFiltersStemsBeforeHandingThemOver()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(CommandFile)));

        Assert.Contains("ToneAudioSource.IsGuitarish", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two commands make tones from the song, and they are different products (#216):
    ///     the Tone designer's estimate measures the audio on this computer and keeps every
    ///     tone, while the Tools menu's asks an AI provider and REPLACES the arrangement's
    ///     slots. Each surface binds exactly one of them, under its own name.
    /// </summary>
    [Fact]
    public void EachSurfaceBindsOneGenerator_TheMenuAndPaletteTheAiOne_TheDesignerTheEstimate()
    {
        var menu = MainMenuSource.ReadAllText();
        var palette = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(PaletteFile)));
        var designer = UiSourceTree.ReadAllText(RepoPath(ViewFile));

        Assert.Contains("Command=\"{Binding ToneRig.GenerateRocksmithTonesCommand}\"", menu, StringComparison.Ordinal);
        Assert.Contains("vm.ToneRig.GenerateRocksmithTonesCommand", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateTonesFromAudioCommand", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateTonesFromAudioCommand", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateRocksmithTonesCommand", designer, StringComparison.Ordinal);

        var rig = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(RigFile)));
        Assert.Contains("_aiToneGeneration.RunAsync(notes)", rig, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and a charter can tell them apart from the labels alone, in every language:
    ///     the two labels differ, and each tooltip names the OTHER command by its exact
    ///     label, so renaming one without the other goes red here.
    /// </summary>
    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void TheTwoGeneratorsAreNamedApart_AndEachTooltipNamesTheOther(string file)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(RepoPath("DotCharter.Localization/" + file)).Root!.Elements("data"))
            values[(string)data.Attribute("name")!] = data.Element("value")?.Value ?? "";

        var ai = values["Menu_GenerateRocksmithTones"];
        var estimate = values["Dialog_Tones_GenerateFromAudio"];

        Assert.NotEqual(ai, estimate);
        Assert.Contains(estimate, values["Menu_GenerateRocksmithTones_Tip"], StringComparison.Ordinal);
        Assert.Contains(ai, values["Dialog_Tones_GenerateFromAudio_Tip"], StringComparison.Ordinal);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
