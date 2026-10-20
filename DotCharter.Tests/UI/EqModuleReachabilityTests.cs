using System.Text.RegularExpressions;
using DotCharter.Tests.Services.Audio;

namespace DotCharter.Tests.UI;

/// <summary>
///     Source lints proving the EQ module is REACHED, not merely written.
/// </summary>
/// <remarks>
///     <para>
///         Modelled on <c>UI/CoopReachabilityTests</c> and
///         <c>UI/PowerToolReachabilityTests</c>, and for the same reason: a whole
///         feature in this repo once shipped registered, unit-tested and
///         constructed by nothing at all, with its visual half wired end to end —
///         which is precisely what made it invisible.
///     </para>
///     <para>
///         Comments are STRIPPED before searching, because a <c>see cref</c> is
///         exactly as inert as a dead registration.
///     </para>
/// </remarks>
public sealed class EqModuleReachabilityTests
{

    /// <summary>Reads a source file with its comments removed, so a mention
    /// inside one cannot pass for a use.</summary>
    private static string SourceWithoutComments(string relativePath)
    {
        var text = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath));
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"^\s*//.*$", " ", RegexOptions.Multiline);
        return text;
    }

    /// <summary>The audio service is ONE class over a dozen partial files, so
    /// both EQ lints read all of them: pinning a filename makes them go vacuous
    /// the moment the EQ wrappers or the rebuild replay move to a sibling
    /// partial. Ordinal order keeps the concatenation stable.</summary>
    private static string AudioServiceSourceWithoutComments() =>
        string.Concat(PlaybackServiceSourceFiles.All()
            .Select(p => SourceWithoutComments(Path.GetRelativePath(TestPaths.RepoRoot(), p))));

    [Fact]
    public void TheEqModuleViewModel_IsConstructedByProductCodeOutsideItsOwnFolder()
    {
        // TransportViewModel owns the one instance. Without this, the module is
        // a view model nothing builds, a view nothing hosts, and six ABI exports
        // nothing calls — all of which compile, and all of which keep passing
        // their own tests.
        var transport = SourceWithoutComments(
            UiSourceTree.Resolve(Path.Combine("DotCharter.UI", "ViewModels", "Editor", "TransportViewModel.cs")));

        Assert.Contains("new Inspector.EqModuleViewModel(", transport, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEqModuleView_IsHostedByTheMixerInspectorTab()
    {
        var mixer = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Views", "Editor", "Inspector", "MixerInspectorView.axaml")));

        Assert.Contains("<inspector:EqModuleView", mixer, StringComparison.Ordinal);
        Assert.Contains("Transport.Eq", mixer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServicePushPath_IsDrivenByTheViewModel()
    {
        // Lint the VERB, not the type: a service the app resolves but never
        // DRIVES is the same fault one level in.
        var vm = SourceWithoutComments(UiSourceTree.Resolve(Path.Combine(
            "DotCharter.UI", "ViewModels", "Editor", "Inspector", "EqModuleViewModel.cs")));

        Assert.Contains("_audio.ApplyEq(", vm, StringComparison.Ordinal);
        Assert.Contains("_audio.ReadEqSpectrum(", vm, StringComparison.Ordinal);
        Assert.Contains("_audio.EngineEqActiveCount", vm, StringComparison.Ordinal);
        Assert.Contains("_audio.SupportsParametricEq", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEngineEntryPoint_IsReachedFromTheAudioService()
    {
        // The seven ABI v29 exports, each through its wrapper method. An export
        // the service never calls is a native feature that does not exist as far
        // as the product is concerned.
        var service = AudioServiceSourceWithoutComments();

        foreach (var call in new[]
                 {
                     "SetEqBand(", "SetEqSolo(", "SetEqRoute(", "SetEqTrim(",
                     "SetEqEnabled(", "GetEqActiveCount(", "ReadEqSpectrum("
                 })
        {
            Assert.True(service.Contains(call, StringComparison.Ordinal),
                $"JuceAudioPlaybackService never calls {call} — that ABI v29 export is dead");
        }
    }

    [Fact]
    public void TheEqSection_IsReplayedWhenTheEngineIsRebuilt()
    {
        // A device change / NAM 48 kHz request / watchdog recovery destroys and
        // rebuilds the engine. The three fixed filters are replayed there and the
        // EQ has to be too, or the user's EQ silently vanishes the first time
        // they change their interface — which is exactly how the co-op routing
        // state was lost.
        var service = AudioServiceSourceWithoutComments();

        var reattach = service.IndexOf("engine.SetBandPass((float)Math.Max(0.0, _bpFreq)",
            StringComparison.Ordinal);
        Assert.True(reattach > 0, "the engine-rebuild filter replay block moved — re-point this lint");

        var window = service[reattach..Math.Min(service.Length, reattach + 600)];
        Assert.Contains("PushEqTo(engine", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModule_NeverWritesIsVisibleOrOpensAPopup()
    {
        // The same two rules the prewarmers are source-linted for. This module
        // lives in a pane that is re-created on every float/redock, so a view
        // that writes IsVisible fights the binding that owns it.
        var view = SourceWithoutComments(UiSourceTree.Resolve(Path.Combine(
            "DotCharter.UI", "Views", "Editor", "Inspector", "EqModuleView.axaml.cs")));

        Assert.DoesNotContain("IsVisible =", view, StringComparison.Ordinal);
        Assert.DoesNotContain(".Open()", view, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnalyserTimer_IsStoppedOnDetach()
    {
        // Inspector pane views are re-created on float/redock, so a timer that
        // outlives its view polls the audio engine forever, once per redock —
        // the leak `ViewSubscriptionLeakGuardTests` exists to prevent, in the one
        // shape it cannot see (a DispatcherTimer, not an event subscription).
        var view = SourceWithoutComments(UiSourceTree.Resolve(Path.Combine(
            "DotCharter.UI", "Views", "Editor", "Inspector", "EqModuleView.axaml.cs")));

        Assert.Contains("OnDetachedFromVisualTree", view, StringComparison.Ordinal);

        var detach = view.IndexOf("OnDetachedFromVisualTree", StringComparison.Ordinal);
        var body = view[detach..Math.Min(view.Length, detach + 400)];
        Assert.Contains("_spectrumTimer.Stop()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLocalizedString_PromisesAnAdvancedEditorTheAppDoesNotHave()
    {
        // The mockup draws an expand affordance that opens a larger, six-bands-
        // side-by-side editor. That editor is out of scope and does not exist, so
        // nothing may offer it — the capability-string rule, applied before the
        // string is written rather than a release later.
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var text = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file));
            var eqKeys = Regex.Matches(text, "<data name=\"(Inspector_Eq[^\"]*)\"[^>]*><value>([^<]*)</value>")
                .Select(m => (Key: m.Groups[1].Value, m.Groups[2].Value));

            Assert.DoesNotContain(eqKeys, entry =>
                entry.Key.Contains("Advanced", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Contains("Expand", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    ///     Every slider in the inspector's two slider-bearing tabs declares what
    ///     a double-click resets it to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The owner reported this flatly missing ("you also can't double
    ///         click sliders to reset them to default value"). It was: the mixer
    ///         had the gesture through a code-behind <c>DoubleTapped</c> handler,
    ///         and the EQ module's four sliders — added later, in their own file —
    ///         never got one. A per-view code-behind handler is exactly the shape
    ///         that lets a new view silently arrive without it, which is why the
    ///         behaviour is now an attached property and why this lint counts
    ///         SLIDERS rather than declarations.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("EqModuleView.axaml")]
    [InlineData("MixerInspectorView.axaml")]
    public void EverySliderInTheInspector_DeclaresWhatADoubleClickResetsItTo(string file)
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector", file)));

        // Each `<Slider` element up to its own closing bracket — `\b(?!\.)`
        // excludes the property-element form (`<Slider.Foreground>`), which is a
        // setter on the slider above it and not a slider of its own.
        var sliders = Regex.Matches(markup, @"<Slider\b(?!\.)[^>]*>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .ToList();

        Assert.NotEmpty(sliders);
        Assert.All(sliders, slider =>
            Assert.True(slider.Contains("ResetOnDoubleClick.", StringComparison.Ordinal),
                $"{file} has a slider with no reset declaration:\n{slider}"));
    }
}
