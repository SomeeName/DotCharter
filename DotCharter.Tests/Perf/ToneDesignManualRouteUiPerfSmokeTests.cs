using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AI designer's "ChatGPT (copy &amp; paste)" route in the rig's Tones
///     mode, in BOTH hosts (DotCharter's panel with its extras, RigLab's core panel
///     mapped the way <c>RigLabWindow</c> maps it): the block open with a long
///     prompt and a real-looking multi-paragraph answer pasted, the answer card
///     after a successful import, a failed import's error, and the NOT-CONFIGURED
///     state — where the route is the whole point — before and after its door.
///     Every case attaches to a shown window, lays out, and asserts the route's
///     controls measured NON-ZERO (a view whose styles failed still arranges, it
///     just has no size).
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, each case is also written as
///         <c>chatgpt-&lt;host&gt;-&lt;state&gt;-&lt;w&gt;x&lt;h&gt;.png</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneDesignManualRouteUiPerfSmokeTests(ITestOutputHelper output)
{
    private const string Description =
        "Stevie Ray Vaughan's Texas Flood rhythm tone: fat, a bit of hair, and it should clean up "
        + "when I roll the guitar's volume back for the verses";

    /// <summary>What a chat sends back: prose, the fenced object, prose.</summary>
    private const string PastedAnswer = """
        Great choice! For a Texas Flood rhythm tone you want a Tube Screamer pushing a loud, clean blackface amp rather than a high-gain amp doing all the work. Here's the chain for your rig:

        ```json
        {
          "name": "Texas Flood Rhythm",
          "rationale": "A Tube Screamer with low drive and high level pushes the amp's front end.",
          "confidence": 0.78,
          "modules": [
            { "type": "compressor", "params": { "ratio": 3, "mix": 0.6 } },
            { "type": "overdrive", "params": { "drive": 0.25, "level": 0.8, "tone": 0.55 } },
            { "type": "amp", "params": { "gain": 0.45, "bass": 0.55, "mid": 0.6, "treble": 0.6 } },
            { "type": "cab", "params": {} },
            { "type": "reverb", "params": { "mix": 0.18 } }
          ]
        }
        ```

        Roll your guitar's volume back to about 7 for the cleaner verses; the Tube Screamer cleans up nicely. If it's too bright on the neck pickup, pull the amp's treble down a notch.
        """;

    // ---- fakes -----------------------------------------------------------------

    /// <summary>A design service whose copy &amp; paste route answers like the real
    /// one would for <see cref="PastedAnswer" /> — and fails for an answer with no
    /// JSON in it, the way the real one does.</summary>
    private sealed class ManualDesignService(bool configured) : IToneDesignService
    {
        public bool IsConfigured => configured;
        public event EventHandler? ConfiguredChanged { add { } remove { } }
        public void NotifyConfigurationChanged() { }

        public Task<ToneDesignResult> DesignAsync(
            ToneDesignRequest request, IProgress<string>? status, CancellationToken ct) =>
            Task.FromResult(Result("Texas Flood Crunch", new AiUsage("claude-sonnet-4-5", 2400, 900),
                TimeSpan.FromSeconds(12.4)));

        public Task<ToneDesignManualPrompt> BuildManualPromptAsync(ToneDesignRequest request, CancellationToken ct)
        {
            var prompt = ToneDesignPromptBuilder.Build(request);
            return Task.FromResult(new ToneDesignManualPrompt(ToneDesignManualPrompts.Flatten(prompt, true), prompt));
        }

        public Task<ToneDesignResult> ImportReplyAsync(
            ToneDesignRequest request, string reply, IProgress<string>? status, CancellationToken ct)
        {
            status?.Report(LocalizationService.Instance["ToneDesign_Status_ReadingReply"]);
            if (ToneDraftJson.ExtractJsonObject(reply) is null)
                throw new ToneDesignException(ToneDesignFault.InvalidOutput, "The pasted answer has no JSON object in it.");
            return Task.FromResult(Result("Texas Flood Rhythm", AiUsage.Manual, TimeSpan.FromMilliseconds(40)));
        }

        private static ToneDesignResult Result(string name, AiUsage usage, TimeSpan elapsed) =>
            new(
                new DspTonePreset("ai-" + name, name, DspToneCategory.Crunch, "",
                    Rig("compressor", "overdrive", "amp", "cab", "reverb"), DspToneOrigin.User),
                "A Tube Screamer with low drive and a high level pushes the amp's front end instead of "
                + "adding its own clipping, which is what gives the mids their vocal bark. The compressor "
                + "evens out the pick attack and a small room fills the space without washing out the attack.",
                0.78,
                ["delay mix lowered to 0.30 to keep the attack"],
                "{}",
                usage,
                elapsed);
    }

    private sealed class MemoryClipboard : ISystemClipboard
    {
        public string? Text;
        public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    private sealed class StubMatcher : IToneMatcher
    {
        public Task<ToneMatchResult> MatchAsync(ToneMatchRequest request, IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubIsolator : IStemIsolator
    {
        public Task<MonoAudio> IsolateGuitarAsync(string songAudioPath, TimeSpan from, TimeSpan to,
            IProgress<string>? status, CancellationToken ct) => throw new NotSupportedException();
    }

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    // ---- the rig ---------------------------------------------------------------

    public enum Host { DotCharter, RigLab }

    /// <summary>Builds the Tones mode with the designer open over
    /// <paramref name="configured" />, runs <paramref name="body" /> against the
    /// designer's view model, and closes the window.</summary>
    private static async Task WithDesigner(
        Host host, bool configured, double width, double height,
        Func<AmpFxRigView, ToneDesignCoreViewModel, MemoryClipboard, Window, Task> body)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var service = new ManualDesignService(configured);
        var clipboard = new MemoryClipboard();
        Func<ToneDesignHostSeams, object?> panel = host == Host.DotCharter
            ? seams => new ToneDesignViewModel(() => service, seams.Apply, seams.SnapshotLive, seams.SaveUser, seams.Localization,
                seams.ModuleName, post: seams.Post, logger: seams.Logger, clipboard: clipboard)
            : seams => new ToneDesignCoreViewModel(() => service, seams.Apply, seams.SnapshotLive, seams.SaveUser, seams.Localization,
                seams.ModuleName, post: seams.Post, logger: seams.Logger, clipboard: clipboard);

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(
            chain, new FakeDspTonePresetService([.. DspTonePresetLibrary.All]), LocalizationService.Instance, toneDesignPanel: panel);
        foreach (var id in new[] { "compressor", "overdrive", "amp", "cab", "delay", "reverb" })
        {
            if (catalogue.FirstOrDefault(m => m.Id == id) is { } module)
                rig.AddGear(module);
        }

        var design = Assert.IsAssignableFrom<ToneDesignCoreViewModel>(rig.Browser.ToneDesign);
        if (design is ToneDesignViewModel full)
        {
            full.ConfigureMatch(static () => new StubMatcher(), static () => new StubIsolator(),
                audio: static () => new FakeAudioPlaybackService());
        }

        design.IsExpanded = true;

        var view = new AmpFxRigView { DataContext = new AmpFxRigViewModel(rig) };
        var window = new Window { Width = width, Height = height, Content = view };
        if (host == Host.RigLab)
            window.DataTemplates.Add(new FuncDataTemplate<ToneDesignCoreViewModel>((_, _) => new ToneDesignCorePanel()));
        window.Show();
        try
        {
            rig.IsBrowserOpen = true;
            Settle(window);
            await body(view, design, clipboard, window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Runs a command's task to completion on the headless dispatcher —
    /// its prompt build is on the pool, its clipboard write comes back here.</summary>
    private static async Task Run(Task task)
    {
        var detector = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > detector) throw new TimeoutException("the command never completed");
            await Task.Delay(5);
        }

        await task;
    }

    private static void Settle(Window window)
    {
        for (var round = 0; round < 3; round++)
        {
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
    }

    private static Control Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"'{name}' is not in the tree");

    private static void AssertMeasured(Visual root, params string[] names)
    {
        foreach (var name in names)
        {
            var control = Find(root, name);
            Assert.True(control.IsEffectivelyVisible, $"'{name}' is in the tree but not visible");
            Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
                $"'{name}' laid out as {control.Bounds} — its styles failed silently");
        }
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
    }

    private static string Name(Host host, string state, double w, double h) =>
        $"chatgpt-{host.ToString().ToLowerInvariant()}-{state}-{w:F0}x{h:F0}.png";

    // ══ the cases ════════════════════════════════════════════════════════════

    /// <summary>
    ///     The block open under a configured designer, the prompt copied and a
    ///     whole chat answer pasted: every control measures, and the paste box
    ///     stays at its cap however long the answer is — without the cap a page of
    ///     prose pushed the composer up over the whole thread. Proven non-vacuous
    ///     by dropping the <c>FireToneReply</c> MaxHeight setter: the box then grew
    ///     to fit the answer.
    /// </summary>
    [Theory]
    [InlineData(Host.DotCharter, 1600, 900)]
    [InlineData(Host.DotCharter, 1280, 800)]
    [InlineData(Host.RigLab, 1440, 900)]
    public Task TheOpenBlock_WithAPastedAnswer_Measures(Host host, double width, double height) =>
        HeadlessSessionFixture.Dispatch(() => WithDesigner(host, configured: true, width, height,
            async (view, design, clipboard, window) =>
            {
                design.Description = Description;
                design.IsManualOpen = true;
                await Run(design.CopyPromptCommand.ExecuteAsync(null));
                design.ReplyText = PastedAnswer;
                Settle(window);

                Assert.NotNull(clipboard.Text);
                AssertMeasured(view, "ToneDesignManual", "ToneDesignCopyPrompt", "ToneDesignReply",
                    "ToneDesignImport", "ToneDesignRun", "ToneDesignManualToggle", "ToneDesignScroll");
                Assert.True(((ToggleButton)Find(view, "ToneDesignManualToggle")).IsChecked == true);

                var reply = Find(view, "ToneDesignReply");
                var cap = (double)view.FindResource("FireToneReplyMaxHeight")!;
                output.WriteLine($"{host} {width}x{height}: reply {reply.Bounds.Size}, thread "
                                 + $"{Find(view, "ToneDesignScroll").Bounds.Size}, status '{design.Status}'");
                Assert.True(reply.Bounds.Height <= cap + 0.5,
                    $"the paste box grew to {reply.Bounds.Height:F0} px for a long answer (cap {cap})");
                Assert.True(reply.Bounds.Height >= 48, $"the paste box is {reply.Bounds.Height:F0} px tall");
                // The block sits in the PINNED composer, so its height comes out of
                // the thread: at 1280x800 a 120 px cap and a two-line instruction
                // left the thread 0 px tall. It must keep a readable slice.
                var thread = Find(view, "ToneDesignScroll").Bounds.Height;
                Assert.True(thread >= 64, $"the copy & paste block left the thread {thread:F0} px tall");
                Assert.False(design.HasManualError);
                Capture(window, Name(host, "open", width, height));
            }), CancellationToken.None);

    /// <summary>A successful import lands in the SAME answer card a provider's
    /// answer does, and its status line says where it came from.</summary>
    [Theory]
    [InlineData(Host.DotCharter, 1600, 900)]
    [InlineData(Host.RigLab, 1440, 900)]
    public Task AnImportedAnswer_LandsInTheAnswerCard(Host host, double width, double height) =>
        HeadlessSessionFixture.Dispatch(() => WithDesigner(host, configured: true, width, height,
            async (view, design, _, window) =>
            {
                design.Description = Description;
                design.IsManualOpen = true;
                await Run(design.CopyPromptCommand.ExecuteAsync(null));
                design.ReplyText = PastedAnswer;
                await Run(design.ImportReplyCommand.ExecuteAsync(null));
                Settle(window);

                Assert.Equal("Texas Flood Rhythm", design.Result?.ToneName);
                Assert.Equal(LocalizationService.Instance["ToneDesign_Manual_Meta"], design.Status);
                AssertMeasured(view, "ToneDesignResult", "ToneDesignHearIt", "ToneDesignChain", "ToneDesignManual");
                Capture(window, Name(host, "imported", width, height));
            }), CancellationToken.None);

    /// <summary>A paste with no tone in it: the error shows under the paste box and
    /// the previous answer stays on screen.</summary>
    [Theory]
    [InlineData(Host.DotCharter, 1600, 900)]
    [InlineData(Host.RigLab, 1440, 900)]
    public Task AFailedImport_ShowsItsError_AndKeepsTheAnswer(Host host, double width, double height) =>
        HeadlessSessionFixture.Dispatch(() => WithDesigner(host, configured: true, width, height,
            async (view, design, _, window) =>
            {
                design.Description = Description;
                await Run(design.DesignCommand.ExecuteAsync(null));
                design.IsManualOpen = true;
                design.TweakText = "tighter low end, less reverb";
                design.ReplyText = "I'm sorry, but I can't browse your rig. Could you tell me which amp models "
                                   + "you have? Then I can suggest settings for each one.";
                await Run(design.ImportReplyCommand.ExecuteAsync(null));
                Settle(window);

                Assert.True(design.HasManualError);
                Assert.Equal("Texas Flood Crunch", design.Result?.ToneName);
                AssertMeasured(view, "ToneDesignManualError", "ToneDesignResult", "ToneDesignReply");
                Capture(window, Name(host, "error", width, height));
            }), CancellationToken.None);

    /// <summary>
    ///     NO provider: the setup banner carries the route's door, and through it
    ///     the body comes up with the block open and NO Design button — nobody
    ///     could answer it. Proven non-vacuous by binding the body back to
    ///     <c>IsConfigured</c>: the block then never realized.
    /// </summary>
    [Theory]
    [InlineData(Host.DotCharter, 1600, 900)]
    [InlineData(Host.RigLab, 1440, 900)]
    public Task WithNoProvider_TheRouteIsReachableFromTheSetupBanner(Host host, double width, double height) =>
        HeadlessSessionFixture.Dispatch(() => WithDesigner(host, configured: false, width, height,
            (view, design, _, window) =>
            {
                AssertMeasured(view, "ToneDesignSetupBanner", "ToneDesignUseManual");
                Assert.False(Find(view, "ToneDesignBody").IsVisible);
                Capture(window, Name(host, "setup", width, height));

                design.OpenManualCommand.Execute(null);
                design.Description = Description;
                design.ReplyText = PastedAnswer;
                Settle(window);

                AssertMeasured(view, "ToneDesignBody", "ToneDesignManual", "ToneDesignCopyPrompt",
                    "ToneDesignReply", "ToneDesignImport", "ToneDesignPrompt");
                Assert.False(Find(view, "ToneDesignSetupBanner").IsVisible, "the banner stayed up over the route");
                Assert.False(Find(view, "ToneDesignRun").IsVisible, "Design is drawn with no provider to answer it");
                Assert.False(Find(view, "ToneDesignExtrasHost").IsEffectivelyVisible,
                    "the host's extras are up with no provider");
                Capture(window, Name(host, "setup-open", width, height));
                return Task.CompletedTask;
            }), CancellationToken.None);
}
