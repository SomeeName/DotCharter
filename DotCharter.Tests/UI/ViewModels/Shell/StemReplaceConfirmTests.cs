using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     Tools ▸ Separate Stems over a song that already HAS stems OVERWRITES
///     them, and "stems are never regenerated" is a hard rule of this app — so
///     the re-separate has to stay explicit. It was its own dialog
///     (<c>StemRegenerateDialog</c>) until 2026-09-04; a destructive verb reached
///     from a MENU takes the app-wide modal prompt instead, which is the rule in
///     <c>docs/agents/ui/dialogs.md</c>.
///     <para>
///         The rule is a static over a delegate so the default gate can drive
///         it without a dialog: a confirm nobody can drive is a confirm nobody
///         should trust.
///     </para>
/// </summary>
public sealed class StemReplaceConfirmTests
{
    private static StemSeparationViewModel.StemReplacePlan Plan(string[] existing, string[] model)
        => StemSeparationViewModel.StemReplacePlan.For(existing, model);

    private static readonly string[] FourStemModel = ["drums", "bass", "other", "vocals"];

    // ---- the plan -------------------------------------------------------------

    /// <summary>
    ///     A stem the model WILL produce is overwritten; one it cannot produce is
    ///     moved aside and put back, which is the half of the retired dialog's
    ///     behaviour worth keeping — a re-run must never silently DROP a stem.
    /// </summary>
    [Fact]
    public void ThePlan_SplitsWhatTheModelOverwritesFromWhatItCannotProduce()
    {
        var plan = Plan(["vocals", "drums", "guitar"], FourStemModel);

        Assert.Equal(["vocals", "drums"], plan.Replace);
        Assert.Equal(["guitar"], plan.Keep);
        Assert.True(plan.OverwritesAnything);
    }

    /// <summary>Stem names come off the filesystem; the model's list is written
    /// in code. Matching has to be case-insensitive or a "Vocals.wav" is kept
    /// AND overwritten at once.</summary>
    [Fact]
    public void ThePlan_MatchesNamesCaseInsensitively()
    {
        var plan = Plan(["Vocals", "DRUMS"], FourStemModel);

        Assert.Equal(["Vocals", "DRUMS"], plan.Replace);
        Assert.Empty(plan.Keep);
    }

    /// <summary>A confirm that fires when nothing is lost is the one people
    /// learn to click through without reading.</summary>
    [Fact]
    public async Task WhenNothingWouldBeOverwritten_NobodyIsAsked_AndTheRunGoesAhead()
    {
        var asked = 0;
        var plan = Plan(["guitar", "piano"], FourStemModel);

        Assert.False(plan.OverwritesAnything);

        var keep = await StemSeparationViewModel.ConfirmStemReplacementAsync(
            plan, "backend", (_, _) => { asked++; return Task.FromResult(true); });

        Assert.Equal(0, asked);
        Assert.NotNull(keep);
        Assert.Equal(["guitar", "piano"], keep!);
    }

    // ---- the gate -------------------------------------------------------------

    [Fact]
    public async Task WithNoPromptWired_TheReSeparationIsREFUSED()
    {
        var keep = await StemSeparationViewModel.ConfirmStemReplacementAsync(
            Plan(["vocals"], FourStemModel), "backend", confirm: null);

        // "No way to ask" must mean "do not do it" for a verb that overwrites
        // files — never "do it silently".
        Assert.Null(keep);
    }

    [Fact]
    public async Task WhenThePromptIsRefused_NothingIsOverwritten()
    {
        var asked = 0;

        var keep = await StemSeparationViewModel.ConfirmStemReplacementAsync(
            Plan(["vocals", "guitar"], FourStemModel), "backend",
            (_, _) => { asked++; return Task.FromResult(false); });

        Assert.Equal(1, asked);
        Assert.Null(keep);
    }

    [Fact]
    public async Task WhenThePromptIsAccepted_TheKeepSetIsHandedBack()
    {
        var seenBackend = "";

        var keep = await StemSeparationViewModel.ConfirmStemReplacementAsync(
            Plan(["vocals", "guitar"], FourStemModel), "ONNX (CUDA)",
            (_, backend) => { seenBackend = backend; return Task.FromResult(true); });

        Assert.NotNull(keep);
        Assert.Equal(["guitar"], keep!);
        // The model/backend still reaches the prompt — it was the retired
        // dialog's Dialog_StemRegenerate_ModelFmt line.
        Assert.Equal("ONNX (CUDA)", seenBackend);
    }

    /// <summary>The message NAMES what is lost, which is what makes an app-wide
    /// prompt an acceptable replacement for a per-stem picker.</summary>
    [Fact]
    public void ThePromptMessage_NamesEveryStemItWillOverwrite()
    {
        var message = StemSeparationViewModel.BuildStemReplaceMessage(
            Plan(["vocals", "drums", "guitar"], FourStemModel), FakeLocalization.English());

        Assert.Contains("vocals", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drums", message, StringComparison.OrdinalIgnoreCase);
        // …and says the one it cannot reproduce survives, rather than leaving
        // the reader to assume everything goes.
        Assert.Contains("guitar", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNothingKept_TheMessageDoesNotTrailAnEmptyKeptSentence()
    {
        var message = StemSeparationViewModel.BuildStemReplaceMessage(
            Plan(["vocals"], FourStemModel), FakeLocalization.English());

        Assert.DoesNotContain(
            FakeLocalization.English().Format("Dialog_StemRegenerate_KeptFmt", ""),
            message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the separation path actually goes through the gate. Without this
    ///     the tests above would pin a helper the shell had stopped calling —
    ///     the shape a file split makes silent in this repo.
    /// </summary>
    [Fact]
    public void TheSeparateStemsPath_GoesThroughTheGate_AndTheWorkflowWiresTheModalPrompt()
    {
        var stems = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "ViewModels", "Shell", "StemSeparationViewModel.cs")));
        var shell = stems;

        Assert.Matches(new Regex(
                @"ConfirmStemReplacementAsync\(\s*plan\s*,\s*backendDescription\s*,\s*ConfirmStemReplaceAsync\s*\)"),
            stems);

        // The constructor hands the delegate the app-wide prompt, as a CONFIRMATION —
        // a plain message dialog completes true on dismiss, which would gate
        // nothing at all.
        var wiring = shell.IndexOf("ConfirmStemReplaceAsync =", StringComparison.Ordinal);
        Assert.True(wiring >= 0,
            "Nothing wires ConfirmStemReplaceAsync — Separate Stems would refuse silently.");
        var block = shell[wiring..Math.Min(shell.Length, wiring + 700)];
        Assert.Contains("_dialogs.Message.ShowAsync", block, StringComparison.Ordinal);
        Assert.Contains("confirmation: true", block, StringComparison.Ordinal);
        Assert.Contains("BuildStemReplaceMessage", block, StringComparison.Ordinal);
    }
}
