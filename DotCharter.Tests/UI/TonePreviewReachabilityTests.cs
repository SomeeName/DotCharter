using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The chart editor's "Preview tones" toggle, hop by hop, against the REAL
///     source files.
///
///     <para>
///         This repo has three times shipped a feature that was built, tested and
///         reachable from nowhere — the 0.22 power tools, couch co-op's entry
///         point, and the whole ABI v28 per-player rig, which was
///         <c>AddSingleton</c>'d and resolved by nothing for a release. A toggle
///         is the worst version of that shape: it MOVES, so it looks alive
///         whatever it is (or is not) wired to.
///     </para>
///     <para>
///         So these lint the VERBS, not just the type: the controller must be
///         CONSTRUCTED by product code, the toolbar must drive its command, and
///         <c>RsTonePlaybackCoordinator</c>'s three preview entry points must each
///         be CALLED from outside the coordinator's own file. Comments are
///         stripped first — a <c>see cref</c> is exactly as inert as an unresolved
///         registration.
///     </para>
/// </summary>
public sealed class TonePreviewReachabilityTests
{
    /// <summary>The toggle exists on the rail the owner pointed at, is bound to
    /// the command, disables itself, and prints its reason.</summary>
    [Fact]
    public void TheToolbarQuickClusterCarriesTheToggle()
    {
        var toolbar = Read("DotCharter.UI", "Views", "Shell", "ToolbarView.axaml");

        // It is in the right-pinned quick cluster, not somewhere else on the rail.
        var cluster = toolbar[toolbar.IndexOf("x:Name=\"ToolbarQuickGroup\"", StringComparison.Ordinal)..];
        Assert.True(cluster.Length > 0, "the toolbar's quick-access cluster is gone");

        Assert.Contains("Command=\"{Binding ToneRig.ToggleTonePreviewCommand}\"", cluster);
        Assert.Contains("IsChecked=\"{Binding ToneRig.IsTonePreviewOn, Mode=OneWay}\"", cluster);
        // Disabled + the reason on the tip: never a live-looking control over
        // nothing (the CoopRigApplier rule).
        Assert.Contains("IsEnabled=\"{Binding ToneRig.IsTonePreviewAvailable}\"", cluster);
        Assert.Contains("ToolTip.Tip=\"{Binding ToneRig.TonePreviewTip}\"", cluster);
        Assert.Contains("Loc Toolbar_TonePreview}", cluster);

        // Its own glyph — the cluster's smoke test asserts every icon in it is
        // distinct, and a shared one is not a strip.
        Assert.Contains("{DynamicResource IconHeadphones}", cluster);
        Assert.Contains("x:Key=\"IconHeadphones\"", Read("DotCharter.Tone.UI", "Styles", "Icons.axaml"));
    }

    /// <summary>
    ///     The shell hop: the command exists, the controller is really
    ///     CONSTRUCTED (not merely referenced), it is given the transport as its
    ///     mixer, and it is torn down.
    /// </summary>
    [Fact]
    public void TheShellBuildsTheControllerAndDrivesIt()
    {
        // The controller, the toggle and the teardown live in ToneRigViewModel;
        // the shell disposes it and owns the tab-switch hook.
        var shell = SourceText.StripCommentLines(
            Read("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs"));
        var toneRig = SourceText.StripCommentLines(
            Read("DotCharter.UI", "ViewModels", "Shell", "ToneRigViewModel.cs"));

        Assert.Contains("new ChartTonePreviewController(", toneRig);
        // The history service is the ONLY signal a moved tone change raises — the
        // marker's position is set on the object, so no collection event fires.
        // Drop it from the ctor call and dragging a marker goes silent again.
        Assert.Contains("tones, _projectState, _localization, _transport, _logger, _history, rocksmith);", toneRig);
        Assert.Contains("TonePreview?.Toggle();", toneRig);
        Assert.Contains("_tonePreview?.Dispose();", toneRig);
        Assert.Contains("ToneRig.Dispose();", shell);

        // Leaving the editor stops it: the rig has ONE owner and Play Mode is the
        // tab most likely to be next.
        Assert.Contains("if (value != ChartEditorTabIndex) ToneRig.StopTonePreviewOutsideTheEditor();", shell);
        Assert.Contains("_tonePreview?.StopOutsideTheEditor();", toneRig);

        // #138: coming back resumes a synth that is still on, and the synth's
        // switch reaches the controller — the chart's tones without the toggle.
        Assert.Contains("else ToneRig.ResumeTonePreviewInTheEditor();", shell);
        Assert.Contains("_tonePreview?.ResumeInTheEditor();", toneRig);
        Assert.Contains("TonePreview?.SetChartSynthPlaying(true);", toneRig);
        Assert.Contains("_tonePreview?.SetChartSynthPlaying(false);", toneRig);

        // #164: the synth's tone choice reaches the controller when it is built
        // (before the synth can ask to play) and whenever the player changes it.
        Assert.Contains("controller.SetChartSynthUsesOwnRig(_transport.ChartSynthPlaysOwnRig);", toneRig);
        Assert.Contains("_tonePreview?.SetChartSynthUsesOwnRig(_transport.ChartSynthPlaysOwnRig);", toneRig);
    }

    /// <summary>
    ///     The audio hop. These four verbs are the whole feature: without a call
    ///     to <c>PrepareForProject</c> nothing is converted, without
    ///     <c>BeginPreview</c> nothing is applied, without <c>EndPreview</c> the
    ///     charter's own rig never comes back, and without
    ///     <c>RefreshPreviewSchedule</c> the rig is driven from wherever the tone
    ///     changes were when the toggle went on. A toggle can flip perfectly while
    ///     all four are dead.
    /// </summary>
    [Theory]
    [InlineData("PrepareForProject(")]
    [InlineData("BeginPreview(")]
    [InlineData("EndPreview(")]
    [InlineData("RefreshPreviewSchedule(")]
    public void EveryPreviewVerbIsCalledByProductCodeOutsideTheCoordinator(string verb)
    {
        var coordinator = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "Coordination",
            "RsTonePlaybackCoordinator.cs");

        var callers = ProductSources()
            .Where(p => !string.Equals(p, coordinator, StringComparison.OrdinalIgnoreCase))
            .Where(p => SourceText.StripCommentLines(UiSourceTree.ReadAllText(p)).Contains(verb, StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p))
            .ToList();

        Assert.True(callers.Count > 0,
            $"RsTonePlaybackCoordinator.{verb} is called by no product code outside its own file — " +
            "the tone preview is a toggle over dead methods.");
    }

    /// <summary>
    ///     …and the controller itself is reached. It is not a DI registration, so
    ///     the failure mode is one step earlier: a type nobody constructs.
    /// </summary>
    [Fact]
    public void TheControllerIsConstructedByProductCodeOutsideItsOwnFile()
    {
        var own = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "Coordination", "ChartTonePreviewController.cs");
        Assert.True(File.Exists(own), "ChartTonePreviewController.cs is gone");

        var constructed = ProductSources()
            .Where(p => !string.Equals(p, own, StringComparison.OrdinalIgnoreCase))
            .Any(p => SourceText.StripCommentLines(UiSourceTree.ReadAllText(p))
                .Contains("new ChartTonePreviewController(", StringComparison.Ordinal));

        Assert.True(constructed,
            "nothing in product code constructs ChartTonePreviewController — the toggle's whole " +
            "behaviour is unreachable, which is exactly how CoopRigApplier shipped dead for a release.");
    }

    /// <summary>
    ///     The live tone read-out on the status rail, hop by hop.
    ///     <para>
    ///         Before it existed, the only reader of the APPLIED tone was
    ///         <c>GameplayToneHud</c> → the 3D highway HUD, which in the editor
    ///         needs the 3D preview panel open AND a Play Mode setting on. So
    ///         charting in the 2D timeline with the preview running, a switch that
    ///         fired and a switch that silently found no preset looked identical —
    ///         <c>ActiveToneChanged</c> and <c>CurrentToneName</c> had zero
    ///         consumers in the whole UI project.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheStatusRailShowsTheToneTheRigIsActuallyBuiltAs()
    {
        var rail = Read("DotCharter.UI", "Views", "Shell", "StatusBar.axaml");
        var shell = SourceText.StripCommentLines(
            Read("DotCharter.UI", "ViewModels", "Shell", "ToneRigViewModel.cs"));
        var vm = SourceText.StripCommentLines(
            Read("DotCharter.UI", "ViewModels", "Application", "StatusBarViewModel.cs"));

        // The chip is present only while something drives the rig — an always-on
        // empty field is chrome, not a read-out.
        Assert.Contains("IsVisible=\"{Binding HasActiveTone}\"", rail);
        Assert.Contains("Text=\"{Binding ActiveToneName}\"", rail);
        Assert.Contains("IsVisible=\"{Binding ActiveToneApproximated}\"", rail);

        // BOTH hops. Without the stop the read-out freezes on the last tone it saw
        // and claims a rig nobody is driving.
        Assert.Contains("tones.ActiveToneChanged +=", shell);
        Assert.Contains("tones.DrivingStopped +=", shell);
        Assert.Contains("_statusBar.SetActiveTone(", shell);

        // ActiveToneChanged fires on the coordinator's apply WORKER, so the write
        // must hop to the UI thread. Raising PropertyChanged for bound state off
        // the UI thread is the repo's oldest standing rule.
        var setter = Between(vm, "public void SetActiveTone(", "ActiveToneApproximated =");
        Assert.Contains("_ui.Post(", setter);
    }

    [Fact]
    public void TheActiveToneStringsExistInBothResxFiles()
    {
        var en = ReadResxKeys("Resources.resx");
        var de = ReadResxKeys("Resources.de-DE.resx");

        foreach (var key in new[] { "StatusBar_ActiveTone_Tip", "StatusBar_ActiveTone_Approximated" })
        {
            Assert.True(en.Contains(key), $"Resources.resx is missing '{key}'.");
            Assert.True(de.Contains(key), $"Resources.de-DE.resx is missing '{key}'.");
        }
    }

    /// <summary>
    ///     Guitar audibility is not decoration: a preview that runs the guitar
    ///     through the right tone while the input is muted is a toggle that
    ///     produces silence. The transport is the app's ONE mixer contract.
    /// </summary>
    [Fact]
    public void TheTransportIsTheMixerTheControllerReadsAndRestores()
    {
        var mixer = Read("DotCharter.UI", "ViewModels", "Editor", "TransportViewModel.Mixer.cs");
        Assert.Contains("public partial class TransportViewModel : IGuitarMonitorMixer", mixer);
        Assert.Contains("public bool IsAnythingSoloed =>", mixer);
        // One definition of "any solo", shared with the mix itself.
        Assert.Contains("var anySolo = IsAnythingSoloed;", mixer);
    }

    [Fact]
    public void EveryTonePreviewStringExistsInBothResxFiles()
    {
        var en = ReadResxKeys("Resources.resx");
        var de = ReadResxKeys("Resources.de-DE.resx");

        string[] required =
        [
            "Toolbar_TonePreview",
            "Toolbar_TonePreview_Tip",
            "Toolbar_TonePreview_NoProject",
            "Toolbar_TonePreview_NoRig",
            "Toolbar_TonePreview_NoTones"
        ];

        foreach (var key in required)
        {
            Assert.True(en.Contains(key), $"Resources.resx is missing '{key}'.");
            Assert.True(de.Contains(key), $"Resources.de-DE.resx is missing '{key}'.");
        }
    }

    /// <summary>
    ///     Every reason the controller can give is worded. A reason key with no
    ///     string prints its own identifier into the tooltip — the
    ///     <c>Diagnostics_FramesFmt</c> failure, on the one control whose disabled
    ///     state has to explain itself.
    /// </summary>
    [Fact]
    public void EveryUnavailableReasonTheControllerCanReturnIsWorded()
    {
        var controller = Read("DotCharter.Services", "Audio", "Coordination", "ChartTonePreviewController.cs");
        var resolve = Between(controller, "private static string ResolveReason(", "private void OnProjectStatePropertyChanged");

        var keys = Regex.Matches(resolve, "\"(Toolbar_TonePreview_[A-Za-z0-9_]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.True(keys.Count >= 3,
            $"only {keys.Count} unavailable reasons found — the scan has gone blind and this asserts nothing");

        var en = ReadResxKeys("Resources.resx");
        var de = ReadResxKeys("Resources.de-DE.resx");
        foreach (var key in keys)
        {
            Assert.True(en.Contains(key), $"Resources.resx is missing the reason '{key}'.");
            Assert.True(de.Contains(key), $"Resources.de-DE.resx is missing the reason '{key}'.");
        }
    }

    /// <summary>
    ///     #7: "Preview in Rocksmith". The switch sits beside the toggle, disables itself with
    ///     its reason, and drives a target the shell really builds over the app's tonelab session.
    /// </summary>
    [Fact]
    public void TheRocksmithSwitchIsOnTheToolbar_AndBuiltOverTheAppSession()
    {
        var toolbar = Read("DotCharter.UI", "Views", "Shell", "ToolbarView.axaml");
        var cluster = toolbar[toolbar.IndexOf("x:Name=\"ToolbarQuickGroup\"", StringComparison.Ordinal)..];
        Assert.Contains("Command=\"{Binding ToneRig.ToggleTonePreviewRocksmithCommand}\"", cluster);
        Assert.Contains("IsChecked=\"{Binding ToneRig.IsTonePreviewInRocksmith, Mode=OneWay}\"", cluster);
        Assert.Contains("IsEnabled=\"{Binding ToneRig.IsTonePreviewRocksmithAvailable}\"", cluster);
        Assert.Contains("ToolTip.Tip=\"{Binding ToneRig.TonePreviewRocksmithTip}\"", cluster);
        Assert.Contains("{DynamicResource IconGamepad}", cluster);

        var toneRig = SourceText.StripCommentLines(Read("DotCharter.UI", "ViewModels", "Shell", "ToneRigViewModel.cs"));
        Assert.Contains("new EditorRocksmithTonePreview(tones, session, _localization, _logger, _ui)", toneRig);
        Assert.Contains("rocksmith.Choose(!rocksmith.IsChosen);", toneRig);
        Assert.Contains("_ = rocksmith.ProbeAsync();", toneRig);
        var app = SourceText.StripCommentLines(Read("DotCharter.UI", "App.ViewModelServices.cs"));
        Assert.Contains("() => sp.GetService<DotCharter.Tone.Audio.Tonelab.ITonelabSession>()", app);
    }

    /// <summary>
    ///     The Rocksmith target's verbs, each called by product code outside its own file: without
    ///     <c>SetProjectTones</c> there is no body to send, without <c>PreviewStarted</c> the tone at
    ///     the playhead never reaches the game when the preview starts, without <c>Dispose</c> the
    ///     game keeps the chart's tone after the editor closes.
    /// </summary>
    [Theory]
    [InlineData("SetProjectTones(")]
    [InlineData("EditorRocksmithTonePreview.ParseTones(")]
    [InlineData("PreviewStarted()")]
    [InlineData("Rocksmith?.ProbeAsync()")]
    [InlineData("rocksmith.StartAsync()")]
    [InlineData("Rocksmith?.Dispose()")]
    public void EveryRocksmithVerbIsCalledByProductCodeOutsideItsFile(string verb)
    {
        var own = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "Coordination", "EditorRocksmithTonePreview.cs");
        Assert.True(File.Exists(own), "EditorRocksmithTonePreview.cs is gone");
        var callers = ProductSources()
            .Where(p => !string.Equals(p, own, StringComparison.OrdinalIgnoreCase))
            .Any(p => SourceText.StripCommentLines(UiSourceTree.ReadAllText(p)).Contains(verb, StringComparison.Ordinal));
        Assert.True(callers, $"EditorRocksmithTonePreview's '{verb}' is called by no product code: the Rocksmith switch is dead.");
    }

    [Fact]
    public void TheRocksmithStringsExistInAllThreeResxFiles()
    {
        string[] keys = ["Toolbar_TonePreviewRocksmith", "Toolbar_TonePreviewRocksmith_Tip", "Toolbar_TonePreviewRocksmith_NotConnected",
            "Toolbar_TonePreviewRocksmith_Start"];
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var present = ReadResxKeys(file);
            foreach (var key in keys) Assert.True(present.Contains(key), $"{file} is missing '{key}'.");
        }
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>The UI and Services sources: the coordinator and the preview
    /// controller live in Services (Audio/Coordination), their callers in both.</summary>
    private static IEnumerable<string> ProductSources()
    {
        foreach (var project in new[] { "DotCharter.UI", "DotCharter.Services", "DotCharter.Tone" })
        {
            var root = Path.Combine(TestPaths.RepoRoot(), project);
            foreach (var file in UiSourceTree.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"end marker not found after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static HashSet<string> ReadResxKeys(string fileName)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName);
        Assert.True(File.Exists(path), $"resx not found at {path}");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
