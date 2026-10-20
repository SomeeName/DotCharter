using DotCharter.Services.Audio.StemSeparation;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     #258: a user who never chose a stem model is ASKED before the first separation
///     downloads one, and the question names the model, its size and the fallback.
///     353 MB is never fetched silently: unwired means refuse, and the separation itself
///     only ever uses an installed model.
/// </summary>
public sealed class StemDefaultDownloadConfirmTests
{
    private static readonly StemDefaultDownloadOffer Sw = new(
        "bs-roformer-sw-6s", "BS-RoFormer SW 6-stem (ONNX)", 352_778_874,
        "htdemucs-6s-onnx", "HTDemucs 6-stem (ONNX)", 114_559_139, ["bs-roformer-sw-6s", "htdemucs-6s-onnx"]);

    /// <summary>#267: a default user who already separates with the 4 s SW, offered the 13 s one.</summary>
    private static readonly StemDefaultDownloadOffer Upgrade = new(
        "bs-roformer-sw-6s-13s", "BS-RoFormer SW 6-stem 13 s (ONNX)", 357_936_365,
        "bs-roformer-sw-6s", "BS-RoFormer SW 6-stem (ONNX)", 352_778_874, ["bs-roformer-sw-6s-13s"], IsUpgrade: true);

    [Fact]
    public async Task WithNoPromptWired_TheDownloadIsREFUSED() =>
        Assert.False(await StemSeparationViewModel.ConfirmDefaultDownloadPromptAsync(Sw, confirm: null));

    [Fact]
    public async Task TheAnswerDecides_AndThePromptSeesTheOffer()
    {
        StemDefaultDownloadOffer? seen = null;
        Assert.False(await StemSeparationViewModel.ConfirmDefaultDownloadPromptAsync(Sw,
            o => { seen = o; return Task.FromResult(false); }));
        Assert.Same(Sw, seen);
        Assert.True(await StemSeparationViewModel.ConfirmDefaultDownloadPromptAsync(Sw, _ => Task.FromResult(true)));
    }

    [Fact]
    public void ThePrompt_NamesTheModelItsSizeAndTheFallback()
    {
        var message = StemSeparationViewModel.BuildDefaultDownloadMessage(Sw, FakeLocalization.English());

        Assert.Contains("BS-RoFormer SW 6-stem (ONNX) (336 MB)", message, StringComparison.Ordinal);
        Assert.Contains("HTDemucs 6-stem (ONNX) (109 MB)", message, StringComparison.Ordinal);
    }

    /// <summary>The upgrade names the new model and its size, and the model the user KEEPS on a
    /// no — never a "downloaded instead" fallback, because nothing else is downloaded.</summary>
    [Fact]
    public void TheUpgradePrompt_NamesTheNewModel_AndTheOneTheUserKeeps()
    {
        var message = StemSeparationViewModel.BuildDefaultDownloadMessage(Upgrade, FakeLocalization.English());

        Assert.Contains("BS-RoFormer SW 6-stem 13 s (ONNX) (341 MB)", message, StringComparison.Ordinal);
        Assert.Contains("than BS-RoFormer SW 6-stem (ONNX), which you use now", message, StringComparison.Ordinal);
        Assert.Contains("not asked again", message, StringComparison.Ordinal);
        Assert.DoesNotContain("instead", message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A NO to an upgrade is remembered and the installed default SEPARATES; a no to a first
    ///     download ends the verb (there is nothing to separate with). Source-pinned because the
    ///     verb needs a whole project session to drive.
    /// </summary>
    [Fact]
    public void ADeclinedUpgrade_IsRemembered_AndTheSeparationGoesOn()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "ViewModels", "Shell", "StemSeparationViewModel.cs")));

        var declined = source.IndexOf("if (!agreed)", StringComparison.Ordinal);
        var available = source.IndexOf("_stemSeparation.IsAvailableAsync()", StringComparison.Ordinal);
        Assert.True(declined >= 0 && declined < available);
        var branch = source[declined..available];
        var firstDownloadEnds = branch.IndexOf("if (!offer.IsUpgrade) return ", StringComparison.Ordinal);
        var remembered = branch.IndexOf("_defaultModels.DeclineUpgrade(offer);", StringComparison.Ordinal);
        Assert.True(firstDownloadEnds >= 0, "A declined first download must still end the verb.");
        Assert.True(remembered > firstDownloadEnds, "A declined upgrade must be remembered, after the first-download return.");

        var wiring = source.IndexOf("ConfirmDefaultDownloadAsync = offer =>", StringComparison.Ordinal);
        Assert.Contains("Dialog_StemsDefaultUpgrade_Title", source[wiring..Math.Min(source.Length, wiring + 400)],
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the Separate Stems path goes through the gate BEFORE the availability
    ///     check, with the app-wide prompt wired as a CONFIRMATION (a plain message
    ///     completes true on dismiss and would gate nothing).
    /// </summary>
    [Fact]
    public void TheSeparateStemsPath_AsksBeforeItDownloads_AndWiresTheModalPrompt()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "ViewModels", "Shell", "StemSeparationViewModel.cs")));

        var ask = source.IndexOf("ConfirmDefaultDownloadPromptAsync(offer, ConfirmDefaultDownloadAsync)", StringComparison.Ordinal);
        var download = source.IndexOf("DownloadDefaultModelAsync(offer, ", StringComparison.Ordinal);
        var available = source.IndexOf("_stemSeparation.IsAvailableAsync()", StringComparison.Ordinal);
        Assert.True(ask >= 0, "Separate Stems no longer asks before the default download.");
        Assert.True(ask < download && download < available,
            "The default download must be asked about, then made, before the availability check.");
        var declined = source[ask..download];
        Assert.Contains("if (!agreed)", declined, StringComparison.Ordinal);
        Assert.Contains("return ", declined[declined.IndexOf("if (!agreed)", StringComparison.Ordinal)..], StringComparison.Ordinal);

        var wiring = source.IndexOf("ConfirmDefaultDownloadAsync = offer =>", StringComparison.Ordinal);
        Assert.True(wiring >= 0, "Nothing wires ConfirmDefaultDownloadAsync; the download would always be refused.");
        var block = source[wiring..Math.Min(source.Length, wiring + 400)];
        Assert.Contains("_dialogs.Message.ShowAsync", block, StringComparison.Ordinal);
        Assert.Contains("confirmation: true", block, StringComparison.Ordinal);
        Assert.Contains("BuildDefaultDownloadMessage", block, StringComparison.Ordinal);
    }

    /// <summary>The provisioner is registered AND reaches the view model that asks.</summary>
    [Fact]
    public void TheProvisioner_IsRegistered_AndInjectedIntoTheSeparateStemsViewModel()
    {
        var services = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.AudioServices.cs"));
        Assert.Contains("AddSingleton<IStemDefaultModelProvisioner>", services, StringComparison.Ordinal);
        var ctor = typeof(StemSeparationViewModel).GetConstructors().Single();
        Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(IStemDefaultModelProvisioner));
    }
}
