using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig drawer's "Design a tone" panel, against a fake
///     <see cref="IToneDesignService" />. No Avalonia: every assertion here is
///     about what the view model DRIVES — the apply seam, the save seam and the
///     request it builds — never about a control.
///
///     <para>
///         The seam assertions are the point. A designed tone that reached the
///         chain by its own private path, or landed in the library by writing a
///         file, would pass a "the result is populated" test and still be the
///         second door onto the rig that <c>docs/agents/ui/pedalboard.md</c>
///         spends a page warning about.
///     </para>
/// </summary>
public sealed class ToneDesignViewModelTests
{
    // ---- fakes ----------------------------------------------------------------

    private sealed class FakeToneDesignService : IToneDesignService
    {
        private bool _configured = true;

        public bool IsConfigured
        {
            get => _configured;
            set
            {
                if (_configured == value) return;
                _configured = value;
                ConfiguredChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
        public event EventHandler? ConfiguredChanged;

        /// <summary>How many times the Settings save path poked this service.</summary>
        public int Pokes { get; private set; }

        /// <inheritdoc />
        public void NotifyConfigurationChanged()
        {
            Pokes++;
            ConfiguredChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Every request the panel sent, in order — the tweak case
        /// asserts against the SECOND one.</summary>
        public List<ToneDesignRequest> Requests { get; } = [];

        public List<string> Progress { get; } = [];

        /// <summary>Held open to keep a run in flight while the test presses
        /// Cancel.</summary>
        public TaskCompletionSource<bool>? Block;

        public Exception? Throw;
        public Func<ToneDesignRequest, ToneDesignResult>? Answer;

        /// <summary>Reads the panel's status line. Set by the harness.</summary>
        public Func<string>? ReadStatus;

        /// <summary>What the status line said the instant <c>Report</c> RETURNED.
        /// This, not a later read, is the contract: <see cref="Progress{T}" />
        /// posts, so with it the line is applied at some unspecified point after
        /// the provider said so — a dispatcher pass later in the product, and after
        /// the assertion in a test. Sampling inside the fake is also what makes the
        /// guard deterministic without a sleep or an await that would give the
        /// posted callback the time it needs and hide the bug.</summary>
        public string? StatusAfterReport;

        public async Task<ToneDesignResult> DesignAsync(
            ToneDesignRequest request, IProgress<string>? status, CancellationToken ct)
        {
            Requests.Add(request);
            status?.Report("Asking the model…");
            StatusAfterReport = ReadStatus?.Invoke();
            Progress.Add("Asking the model…");

            if (Block != null)
            {
                // A real provider observes the token; this one has to as well or
                // the cancel case asserts nothing.
                await Block.Task.WaitAsync(ct);
            }

            if (Throw != null) throw Throw;
            return Answer?.Invoke(request) ?? ResultFor("Fuzz Face Lead");
        }

        // ---- the copy & paste route ----

        /// <summary>Every request a prompt was COPIED for.</summary>
        public List<ToneDesignRequest> ManualRequests { get; } = [];

        /// <summary>Every (request, pasted answer) an import was asked for.</summary>
        public List<(ToneDesignRequest Request, string Reply)> Imports { get; } = [];

        public Exception? ImportThrow;

        public Task<ToneDesignManualPrompt> BuildManualPromptAsync(ToneDesignRequest request, CancellationToken ct)
        {
            ManualRequests.Add(request);
            var text = $"PROMPT FOR: {request.Description} ({request.History.Count} turn(s))";
            return Task.FromResult(new ToneDesignManualPrompt(
                text, new ToneDesignPrompt("system", request.History, "{}", "tone_draft")));
        }

        public Task<ToneDesignResult> ImportReplyAsync(
            ToneDesignRequest request, string reply, IProgress<string>? status, CancellationToken ct)
        {
            Imports.Add((request, reply));
            status?.Report("Reading the pasted answer…");
            if (ImportThrow != null) throw ImportThrow;
            return Task.FromResult(ResultFor("Pasted Plexi") with { Usage = AiUsage.Manual });
        }
    }

    /// <summary>The OS clipboard, in memory.</summary>
    private sealed class FakeClipboard : ISystemClipboard
    {
        public string? Text;
        public Exception? Throw;

        public Task SetTextAsync(string text)
        {
            if (Throw != null) throw Throw;
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    // ---- helpers --------------------------------------------------------------

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    private static ToneDesignResult ResultFor(
        string name, double confidence = 0.84, params string[] warnings) =>
        new(
            new DspTonePreset("ai-1", name, DspToneCategory.Lead, "", Rig("rat", "poweramp", "cab"),
                DspToneOrigin.User),
            "A Rat into a clean power amp keeps the mids forward.",
            confidence,
            warnings,
            "{}",
            new AiUsage("claude-opus-5", 100, 200));

    private sealed class Harness
    {
        public readonly FakeToneDesignService Service = new();
        public readonly List<DspTonePreset> Applied = [];
        public readonly List<(string Name, DspToneCategory Category, DcRigPreset Rig)> Saved = [];
        /// <summary>Every snapshot the panel took, as DISTINCT objects. A
        /// harness handing back one shared instance cannot tell "the restore
        /// point is taken once" from "it is retaken on every Hear it" — the
        /// assertion below would pass either way, which is how a guard goes
        /// vacuous.</summary>
        public readonly List<DspTonePreset> Snapshots = [];
        public int SettingsOpened;
        public readonly FakeClipboard Clipboard = new();
        public readonly ToneDesignViewModel Vm;

        public Harness()
        {
            // Deferred, so the not-yet-assigned field is fine; the lambda runs
            // only once a design is in flight.
            Service.ReadStatus = () => Vm!.Status;
            Vm = new ToneDesignViewModel(
                () => Service,
                preset => Applied.Add(preset),
                () =>
                {
                    var snapshot = new DspTonePreset(
                        "live", $"What I was playing #{Snapshots.Count}", DspToneCategory.Clean, "",
                        Rig("amp"), DspToneOrigin.User);
                    Snapshots.Add(snapshot);
                    return snapshot;
                },
                (name, category, _, rig) =>
                {
                    Saved.Add((name, category, rig));
                    return new DspTonePreset("saved-1", name, category, "", rig, DspToneOrigin.User);
                }, FakeLocalization.English(),
                // The catalogue's words, so the chain summary is the one the
                // player reads on a board card.
                id => id switch
                {
                    "rat" => "Rat",
                    "poweramp" => "Power Amp",
                    "cab" => "Cab",
                    _ => id
                },
                clipboard: Clipboard)
            {
                OpenSettings = () => SettingsOpened++,
                IsExpanded = true
            };
        }
    }

    // ---- the cases ------------------------------------------------------------

    [Fact]
    public async Task Designing_populates_the_result_with_the_chain_in_the_catalogues_words()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";

        await h.Vm.DesignCommand.ExecuteAsync(null);

        Assert.NotNull(h.Vm.Result);
        Assert.Equal("Fuzz Face Lead", h.Vm.Result!.ToneName);
        Assert.Equal("Rat → Power Amp → Cab", h.Vm.Result.ChainSummary);
        Assert.Equal(84, h.Vm.Result.ConfidencePercent);
        // …and as the answer card's chips: one per unit, NAMED in the same words
        // and in the same order, so the card and the summary cannot disagree.
        Assert.Equal(["Rat", "Power Amp", "Cab"], h.Vm.Result.ChainSteps.Select(s => s.Name));
        Assert.All(h.Vm.Result.ChainSteps, s => Assert.StartsWith("FireUnit", s.TokenKey, StringComparison.Ordinal));
        Assert.False(h.Vm.HasError);
        Assert.Equal("gritty blues lead", h.Service.Requests[0].Description);
    }

    [Fact]
    public async Task The_request_carries_the_song_the_host_is_playing()
    {
        var h = new Harness();
        h.Vm.SongContextProvider = () => ("Master of Puppets", "Metallica");
        h.Vm.Description = "that tone";

        await h.Vm.DesignCommand.ExecuteAsync(null);

        Assert.Equal("Master of Puppets", h.Service.Requests[0].SongTitle);
        Assert.Equal("Metallica", h.Service.Requests[0].Artist);
    }

    [Theory]
    [InlineData(ToneDesignFault.NotConfigured, true)]
    [InlineData(ToneDesignFault.Authentication, true)]
    [InlineData(ToneDesignFault.Network, false)]
    [InlineData(ToneDesignFault.RateLimited, false)]
    [InlineData(ToneDesignFault.ModelRefused, false)]
    [InlineData(ToneDesignFault.InvalidOutput, false)]
    [InlineData(ToneDesignFault.Unknown, false)]
    public async Task A_fault_becomes_a_localized_message_and_only_two_offer_Settings(
        ToneDesignFault fault, bool offersSettings)
    {
        var h = new Harness();
        h.Service.Throw = new ToneDesignException(fault, "raw provider text", "detail");
        h.Vm.Description = "anything";

        await h.Vm.DesignCommand.ExecuteAsync(null);

        Assert.True(h.Vm.HasError);
        // Localized, not the provider's own words: the raw text goes to the log.
        Assert.NotEqual("raw provider text", h.Vm.ErrorText);
        Assert.DoesNotContain("ToneDesign_Fault", h.Vm.ErrorText, StringComparison.Ordinal);
        Assert.Equal(offersSettings, h.Vm.ErrorOffersSettings);

        h.Vm.OpenSettingsTabCommand.Execute(null);
        Assert.Equal(1, h.SettingsOpened);
    }

    [Fact]
    public async Task Cancel_ends_the_run_as_cancelled_and_not_as_a_failure()
    {
        var h = new Harness();
        h.Service.Block = new TaskCompletionSource<bool>();
        h.Vm.Description = "something slow";

        var run = h.Vm.DesignCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsRunning);
        Assert.True(h.Vm.CancelCommand.CanExecute(null));

        h.Vm.CancelCommand.Execute(null);
        await run;

        Assert.False(h.Vm.IsRunning);
        Assert.False(h.Vm.HasError);
        Assert.True(h.Vm.HasStatus);
        Assert.Null(h.Vm.Result);
    }

    [Fact]
    public async Task A_tweak_carries_the_history_and_the_current_tone()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";
        await h.Vm.DesignCommand.ExecuteAsync(null);
        var firstTone = h.Vm.Result!.Tone;

        h.Service.Answer = _ => ResultFor("Fuzz Face Lead, less gain");
        h.Vm.TweakText = "less gain";
        await h.Vm.TweakCommand.ExecuteAsync(null);

        var follow = h.Service.Requests[1];
        Assert.Equal("less gain", follow.Description);
        Assert.Same(firstTone, follow.CurrentTone);
        // user("gritty blues lead"), assistant(the tone name), user("less gain")
        Assert.Equal(3, follow.History.Count);
        Assert.Equal(ToneDesignRole.User, follow.History[0].Role);
        Assert.Equal("gritty blues lead", follow.History[0].Text);
        Assert.Equal(ToneDesignRole.Assistant, follow.History[1].Role);
        Assert.Equal("less gain", follow.History[2].Text);
        // The reply REPLACES the result rather than stacking a second one.
        Assert.Equal("Fuzz Face Lead, less gain", h.Vm.Result!.ToneName);
        Assert.Equal(string.Empty, h.Vm.TweakText);
    }

    [Fact]
    public async Task Hear_it_applies_through_the_seam_and_Discard_puts_the_old_tone_back()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";
        await h.Vm.DesignCommand.ExecuteAsync(null);

        h.Vm.HearItCommand.Execute(null);
        Assert.Single(h.Applied);
        Assert.Equal("Fuzz Face Lead", h.Applied[0].Name);

        // A second Hear it must not move the restore point — coming back means
        // the tone the player ARRIVED with, not the previous design.
        h.Service.Answer = _ => ResultFor("Second design");
        h.Vm.TweakText = "darker";
        await h.Vm.TweakCommand.ExecuteAsync(null);
        h.Vm.HearItCommand.Execute(null);

        h.Vm.DiscardCommand.Execute(null);

        // Hear it, Hear it again after the tweak, and the ONE restore.
        Assert.Equal(3, h.Applied.Count);
        // ONE snapshot, and the restore is THAT one: hearing two designs in a
        // row must still return to the tone the player arrived with, not to the
        // previous design.
        Assert.Single(h.Snapshots);
        Assert.Same(h.Snapshots[0], h.Applied[^1]);
        Assert.Null(h.Vm.Result);
    }

    [Fact]
    public async Task Discard_without_Hear_it_restores_nothing()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";
        await h.Vm.DesignCommand.ExecuteAsync(null);

        h.Vm.DiscardCommand.Execute(null);

        Assert.Empty(h.Applied);
        Assert.Null(h.Vm.Result);
    }

    [Fact]
    public async Task Save_goes_through_the_user_preset_seam_with_the_designed_rig()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";
        await h.Vm.DesignCommand.ExecuteAsync(null);

        h.Vm.SaveToneCommand.Execute(null);

        var saved = Assert.Single(h.Saved);
        Assert.Equal("Fuzz Face Lead", saved.Name);
        Assert.Equal(DspToneCategory.Lead, saved.Category);
        Assert.Equal(["rat", "poweramp", "cab"], saved.Rig.Modules.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void With_no_provider_configured_the_panel_offers_nothing_but_the_setup_line()
    {
        var h = new Harness();
        h.Service.IsConfigured = false;
        h.Vm.IsExpanded = false;
        h.Vm.IsExpanded = true; // re-reads IsConfigured on the way open

        Assert.False(h.Vm.IsConfigured);
        Assert.True(h.Vm.IsNotConfigured);
        h.Vm.Description = "anything";
        Assert.False(h.Vm.DesignCommand.CanExecute(null));
        Assert.False(h.Vm.TweakCommand.CanExecute(null));
        Assert.Null(h.Vm.Result);
    }

    [Fact]
    public async Task The_status_line_carries_the_services_own_progress()
    {
        var h = new Harness();
        h.Service.Block = new TaskCompletionSource<bool>();
        h.Vm.Description = "slow one";

        var run = h.Vm.DesignCommand.ExecuteAsync(null);

        // Sampled INSIDE the provider, the instant Report returned.
        Assert.Equal("Asking the model…", h.Service.StatusAfterReport);
        Assert.Equal("Asking the model…", h.Vm.Status);

        h.Service.Block.SetResult(true);
        await run;
        Assert.NotNull(h.Vm.Result);
    }

    [Fact]
    public async Task Warnings_from_the_validator_are_shown_verbatim()
    {
        var h = new Harness();
        h.Service.Answer = _ => ResultFor("Clamped", 0.5, "gain clamped to 10", "'flanger' dropped");
        h.Vm.Description = "impossible";

        await h.Vm.DesignCommand.ExecuteAsync(null);

        Assert.True(h.Vm.Result!.HasWarnings);
        Assert.Contains("gain clamped to 10", h.Vm.Result.WarningsText, StringComparison.Ordinal);
        Assert.Contains("'flanger' dropped", h.Vm.Result.WarningsText, StringComparison.Ordinal);
        Assert.Equal(50, h.Vm.Result.ConfidencePercent);
    }

    // ---- the DRAWER's seams, through the real browser --------------------------

    /// <summary>
    ///     The panel is constructed by <c>PresetBrowserViewModel</c> and drives
    ///     ITS seams. This is the half a delegate-level test cannot prove: that
    ///     a designed tone saved from the panel really lands in the library as a
    ///     USER preset and becomes the selected card, and that the restore point
    ///     the panel snapshots is a USER-origin preset (the factory apply path
    ///     loudness-normalizes, so a restore through it comes back re-levelled).
    /// </summary>
    [Fact]
    public void The_drawer_hands_the_panel_its_own_apply_and_save_seams()
    {
        var presets = new FakeDspTonePresetService();
        var live = Rig("amp", "cab");
        var browser = new PresetBrowserViewModel(presets, FakeLocalization.English(), () => live, () => { },
            toneDesignPanel: ToneDesignPanels.Real());

        // The panel exists and is the drawer's own, not a second construction.
        Assert.NotNull(browser.ToneDesign);

        var snapshot = browser.SnapshotLiveTone();
        Assert.Equal(DspToneOrigin.User, snapshot.Origin);
        Assert.Same(live, snapshot.Rig);

        var designed = Rig("rat", "amp", "cab");
        var saved = browser.SaveDesignedTone("AI Lead", DspToneCategory.Lead, "because", designed);

        Assert.Equal(DspToneOrigin.User, saved.Origin);
        Assert.Contains(presets.UserPresets, p => p.Id == saved.Id && p.Name == "AI Lead");
        // It is IN the library, not merely on disk: its card is in the grid (My
        // Tones) and selected.
        Assert.Equal(saved.Id, browser.SelectedCard?.Id);
        Assert.Contains(browser.Cards, c => c.Id == saved.Id);
    }

    /// <summary>
    ///     The standalone rig (DotCharter.RigLab) passes no design panel: the drawer
    ///     then has none, rather than reaching for a service locator it does not have.
    /// </summary>
    [Fact]
    public void Without_a_host_factory_the_drawer_has_no_design_panel()
    {
        var browser = new PresetBrowserViewModel(new FakeDspTonePresetService(), FakeLocalization.English(), () => Rig("amp"), () => { });

        Assert.Null(browser.ToneDesign);
    }

    // ---- the 0.55.1 additions -------------------------------------------------

    /// <summary>
    ///     The panel SUBSCRIBES. It used to re-read <c>IsConfigured</c> on the way
    ///     open, which is a whole <c>LoadAppConfig</c> on the UI thread inside the
    ///     ≤150 ms switch budget, on a property a binding touches every layout
    ///     pass. Configuring a provider in Settings now reaches a panel that is
    ///     already open, which the expand-time read never could.
    /// </summary>
    [Fact]
    public void ConfiguredChanged_republishes_without_the_panel_being_reopened()
    {
        var h = new Harness();
        h.Vm.IsExpanded = true;
        var changed = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(h.Vm.IsConfigured);

        h.Service.IsConfigured = false; // raises ConfiguredChanged

        Assert.False(h.Vm.IsConfigured);
        Assert.True(h.Vm.IsNotConfigured);
        Assert.Contains(nameof(ToneDesignViewModel.IsConfigured), changed);
        Assert.False(h.Vm.DesignCommand.CanExecute(null));
    }

    /// <summary>A poke from the Settings tab's save path is the other way in, and
    /// it must reach the panel even when the boolean did not move — the model id
    /// changing is a reconfiguration the panel prints.</summary>
    [Fact]
    public void A_settings_poke_reaches_the_panel()
    {
        var h = new Harness();
        var raised = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToneDesignViewModel.IsConfigured)) raised++;
        };

        h.Service.NotifyConfigurationChanged();

        Assert.Equal(1, h.Service.Pokes);
        Assert.Equal(1, raised);
    }

    /// <summary>Detach unhooks: the service is a singleton, and a live handler
    /// would root the whole drawer for the life of the process.</summary>
    [Fact]
    public void Detach_stops_the_panel_following_the_service()
    {
        var h = new Harness();
        Assert.True(h.Vm.IsConfigured);

        h.Vm.Detach();
        var raised = 0;
        h.Vm.PropertyChanged += (_, _) => raised++;
        h.Service.NotifyConfigurationChanged();

        Assert.Equal(0, raised);
    }

    /// <summary>"12.4 s · ~$0.04" in the line that was carrying "Asking Claude…".
    /// The cost comes from the BILLED tokens and the model id on the usage.</summary>
    [Fact]
    public async Task The_status_line_reports_elapsed_time_and_the_estimated_cost()
    {
        var h = new Harness();
        h.Service.Answer = _ => ResultFor("Fuzz Face Lead") with
        {
            Elapsed = TimeSpan.FromSeconds(12.44),
            Usage = new AiUsage("claude-opus-5", 7_500, 900)
        };
        h.Vm.Description = "gritty blues lead";

        await h.Vm.DesignCommand.ExecuteAsync(null);

        Assert.Equal("12.4 s", h.Vm.Result!.ElapsedText);
        Assert.Equal("~$0.06", h.Vm.Result.CostText);
        Assert.True(h.Vm.Result.HasMeta);
        Assert.Equal("12.4 s · ~$0.06", h.Vm.Status);
        Assert.True(h.Vm.HasStatus);
    }

    /// <summary>A local model has no list price, and a request to it really is
    /// free. It prints "local" — never "$0.00", which reads as a measured zero.</summary>
    [Fact]
    public async Task A_local_model_reports_its_time_and_no_dollar_figure()
    {
        var h = new Harness();
        h.Service.Answer = _ => ResultFor("Fuzz Face Lead") with
        {
            Elapsed = TimeSpan.FromSeconds(3.2),
            Usage = new AiUsage("llama-3.1-8b-instruct", 7_500, 900)
        };
        h.Vm.Description = "gritty blues lead";

        await h.Vm.DesignCommand.ExecuteAsync(null);

        Assert.Equal("3.2 s · local", h.Vm.Status);
        Assert.DoesNotContain("$", h.Vm.Status, StringComparison.Ordinal);
    }

    /// <summary>A result that measured nothing prints nothing, rather than
    /// "0.0 s".</summary>
    [Fact]
    public async Task An_unmeasured_result_prints_no_status_line()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";

        await h.Vm.DesignCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, h.Vm.Result!.MetaText);
        Assert.False(h.Vm.Result.HasMeta);
        Assert.Equal(string.Empty, h.Vm.Status);
    }

    // ---- the conversation's SHAPE (the panel reads like a thread, 2026-09-27) --

    /// <summary>
    ///     The newest answer has no row of its own — the answer card IS that
    ///     answer — and every answer the thread has moved past is one line. A
    ///     design and one refinement is therefore: You, one earlier answer, You,
    ///     and the card. Proven non-vacuous by making <c>IsEarlierAnswer</c> read
    ///     <c>!IsUser</c>: the newest answer then printed twice and this failed on
    ///     the last turn.
    /// </summary>
    [Fact]
    public async Task A_refined_design_reads_you_answer_you_and_the_card()
    {
        var h = new Harness();
        h.Vm.Description = "glassy clean";
        await h.Vm.DesignCommand.ExecuteAsync(null);
        h.Vm.TweakText = "less reverb";
        await h.Vm.TweakCommand.ExecuteAsync(null);

        Assert.Equal([true, false, true, false], h.Vm.Turns.Select(t => t.IsUser));
        Assert.Equal([false, true, false, false], h.Vm.Turns.Select(t => t.IsEarlierAnswer));
        Assert.True(h.Vm.Turns[^1].IsLast);
        Assert.Single(h.Vm.Turns, t => t.IsLast);
        Assert.True(h.Vm.ShowResult);
        Assert.False(h.Vm.ShowIntro);
    }

    /// <summary>
    ///     While a request is in flight the previous answer's card is NOT drawn:
    ///     it would sit under the new question it does not answer. It comes back
    ///     if the request is cancelled, and the earlier answer stays a row.
    /// </summary>
    [Fact]
    public async Task While_a_refinement_runs_the_old_card_steps_aside_and_returns_on_cancel()
    {
        var h = new Harness();
        h.Vm.Description = "glassy clean";
        await h.Vm.DesignCommand.ExecuteAsync(null);
        Assert.True(h.Vm.ShowResult);

        h.Service.Block = new TaskCompletionSource<bool>();
        h.Vm.TweakText = "less reverb";
        var run = h.Vm.TweakCommand.ExecuteAsync(null);

        Assert.True(h.Vm.IsRunning);
        Assert.False(h.Vm.ShowResult, "the old card is drawn under a question it does not answer");
        Assert.True(h.Vm.Turns[1].IsEarlierAnswer, "the first answer lost its row while the second is pending");

        h.Vm.CancelCommand.Execute(null);
        await run;

        Assert.False(h.Vm.IsRunning);
        Assert.True(h.Vm.ShowResult, "a cancelled refinement lost the answer that is still current");
    }

    /// <summary>The empty panel says what to ask for; anything in the thread — a
    /// request, a failure — replaces that line, and Discard brings it back.</summary>
    [Fact]
    public async Task The_intro_shows_only_while_the_thread_is_empty()
    {
        var h = new Harness();
        Assert.True(h.Vm.ShowIntro);

        h.Service.Throw = new ToneDesignException(ToneDesignFault.Network, "down");
        h.Vm.Description = "glassy clean";
        await h.Vm.DesignCommand.ExecuteAsync(null);
        Assert.True(h.Vm.HasError);
        Assert.False(h.Vm.ShowIntro, "the intro sits over a failed request");

        h.Service.Throw = null;
        await h.Vm.DesignCommand.ExecuteAsync(null);
        Assert.False(h.Vm.ShowIntro);

        h.Vm.DiscardCommand.Execute(null);
        Assert.True(h.Vm.ShowIntro, "Discard emptied the thread but the intro did not come back");
    }

    /// <summary>
    ///     "Describe it first" shares the slot beside the button with the status
    ///     line, so it is up only while the box is the PROMPT, it is empty and the
    ///     status line has nothing to say.
    /// </summary>
    [Fact]
    public async Task The_prompt_hint_shows_only_for_an_empty_prompt_with_nothing_else_to_say()
    {
        var h = new Harness();
        Assert.True(h.Vm.ShowPromptHint);

        h.Vm.Description = "glassy clean";
        Assert.False(h.Vm.ShowPromptHint, "the hint stayed up over a typed prompt");

        await h.Vm.DesignCommand.ExecuteAsync(null);
        h.Vm.Description = "";
        Assert.False(h.Vm.ShowPromptHint, "the hint is about Design, which is not on screen after an answer");

        h.Vm.DiscardCommand.Execute(null);
        h.Vm.Status = "";
        Assert.True(h.Vm.ShowPromptHint);
        h.Vm.Status = "Saved.";
        Assert.False(h.Vm.ShowPromptHint, "the hint and the status line drew in one slot");
    }

    // ---- the "ChatGPT (copy & paste)" route (2026-09-28) ----------------------

    /// <summary>A realistic chat answer: a friendly sentence, the fenced object, a
    /// closing sentence. What the fake does with it is not the point here — the
    /// service tests parse it; these assert what the PANEL drives.</summary>
    private const string PastedAnswer = "Here's a crunchy Plexi tone for you!\n```json\n{}\n```\nEnjoy!";

    [Fact]
    public async Task Copy_puts_the_prompt_for_the_box_on_the_clipboard_and_asks_nobody()
    {
        var h = new Harness();
        h.Vm.Description = "  crunchy Plexi rhythm  ";

        await h.Vm.CopyPromptCommand.ExecuteAsync(null);

        Assert.Equal("PROMPT FOR: crunchy Plexi rhythm (1 turn(s))", h.Clipboard.Text);
        var request = Assert.Single(h.Service.ManualRequests);
        // The request a Design would have sent: the prompt as the conversation's
        // only turn, no current tone.
        Assert.Equal("crunchy Plexi rhythm", request.Description);
        Assert.Equal(ToneDesignRole.User, Assert.Single(request.History).Role);
        Assert.Null(request.CurrentTone);
        // Copying asks nobody: no provider call, no turn, no answer.
        Assert.Empty(h.Service.Requests);
        Assert.Empty(h.Vm.Turns);
        Assert.Null(h.Vm.Result);
        Assert.False(h.Vm.IsRunning);
        // …and it says what it did, with the prompt's rough size.
        Assert.True(h.Vm.HasStatus);
        Assert.DoesNotContain("ToneDesign_", h.Vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_after_an_answer_builds_the_refinement_of_the_current_tone()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";
        await h.Vm.DesignCommand.ExecuteAsync(null);
        var current = h.Vm.Result!.Tone;

        h.Vm.TweakText = "less gain";
        await h.Vm.CopyPromptCommand.ExecuteAsync(null);

        var request = Assert.Single(h.Service.ManualRequests);
        Assert.Equal("less gain", request.Description);
        Assert.Same(current, request.CurrentTone);
        // user, assistant, user("less gain") — the same shape a Tweak sends.
        Assert.Equal(3, request.History.Count);
        Assert.Equal("less gain", request.History[2].Text);
        // Still nothing in the conversation until an answer is imported.
        Assert.Equal(2, h.Vm.Turns.Count);
    }

    [Fact]
    public async Task Import_lands_in_the_result_and_the_conversation_like_a_design()
    {
        var h = new Harness();
        h.Vm.Description = "crunchy Plexi rhythm";
        await h.Vm.CopyPromptCommand.ExecuteAsync(null);

        h.Vm.ReplyText = PastedAnswer;
        await h.Vm.ImportReplyCommand.ExecuteAsync(null);

        var import = Assert.Single(h.Service.Imports);
        // It answers the request the COPIED prompt was built for.
        Assert.Same(h.Service.ManualRequests[0], import.Request);
        Assert.Equal(PastedAnswer, import.Reply);

        Assert.Equal("Pasted Plexi", h.Vm.Result!.ToneName);
        Assert.Equal("Rat → Power Amp → Cab", h.Vm.Result.ChainSummary);
        Assert.Equal(2, h.Vm.Turns.Count);
        Assert.True(h.Vm.Turns[0].IsUser);
        Assert.Equal("crunchy Plexi rhythm", h.Vm.Turns[0].Text);
        Assert.Equal("Pasted Plexi", h.Vm.Turns[1].Text);
        Assert.Equal(string.Empty, h.Vm.ReplyText);
        Assert.False(h.Vm.HasManualError);
        // No cost and no "local": this app metered nothing. It says where the
        // answer came from instead.
        Assert.Equal(string.Empty, h.Vm.Result.CostText);
        Assert.Equal(FakeLocalization.English()["ToneDesign_Manual_Meta"], h.Vm.Result.MetaText);
        Assert.Equal(h.Vm.Result.MetaText, h.Vm.Status);
        Assert.DoesNotContain("$", h.Vm.Status, StringComparison.Ordinal);
        Assert.True(h.Vm.ShowResult);
    }

    [Fact]
    public async Task A_failed_import_says_so_under_the_paste_box_and_changes_nothing_else()
    {
        var h = new Harness();
        h.Vm.Description = "gritty blues lead";
        await h.Vm.DesignCommand.ExecuteAsync(null);
        var before = h.Vm.Result;
        var turns = h.Vm.Turns.Select(t => t.Text).ToList();

        h.Service.ImportThrow = new ToneDesignException(ToneDesignFault.InvalidOutput, "no JSON object");
        h.Vm.TweakText = "less gain";
        h.Vm.ReplyText = "Sorry, I can't help with that.";
        await h.Vm.ImportReplyCommand.ExecuteAsync(null);

        Assert.True(h.Vm.HasManualError);
        Assert.Equal(FakeLocalization.English()["ToneDesign_Manual_InvalidReply"], h.Vm.ManualErrorText);
        // Nothing else moved: the answer, the conversation, the box and the
        // conversation's own error line.
        Assert.Same(before, h.Vm.Result);
        Assert.Equal(turns, h.Vm.Turns.Select(t => t.Text));
        Assert.Equal("Sorry, I can't help with that.", h.Vm.ReplyText);
        Assert.Equal("less gain", h.Vm.TweakText);
        Assert.False(h.Vm.HasError);
        Assert.True(h.Vm.ShowResult);
        Assert.Empty(h.Applied);
    }

    [Fact]
    public async Task The_route_is_reachable_and_works_with_no_provider_configured()
    {
        var h = new Harness();
        h.Service.IsConfigured = false;

        // The setup banner is the door; the body is not on screen yet.
        Assert.True(h.Vm.ShowSetupBanner);
        Assert.False(h.Vm.ShowBody);
        Assert.True(h.Vm.IsManualAvailable);

        h.Vm.OpenManualCommand.Execute(null);
        Assert.True(h.Vm.IsManualOpen);
        Assert.True(h.Vm.ShowBody);
        Assert.False(h.Vm.ShowSetupBanner);
        // …and Design is not drawn: nobody could answer it.
        Assert.False(h.Vm.ShowDesignButton);
        Assert.False(h.Vm.ShowTweakButton);

        h.Vm.Description = "warm jazz clean";
        Assert.False(h.Vm.DesignCommand.CanExecute(null));
        Assert.True(h.Vm.CopyPromptCommand.CanExecute(null));
        await h.Vm.CopyPromptCommand.ExecuteAsync(null);
        Assert.NotNull(h.Clipboard.Text);

        h.Vm.ReplyText = PastedAnswer;
        await h.Vm.ImportReplyCommand.ExecuteAsync(null);
        Assert.Equal("Pasted Plexi", h.Vm.Result!.ToneName);
        Assert.False(h.Vm.ShowTweakButton);

        // A second round refines the imported tone, again by copy & paste.
        h.Vm.TweakText = "darker";
        await h.Vm.CopyPromptCommand.ExecuteAsync(null);
        Assert.Same(h.Vm.Result.Tone, h.Service.ManualRequests[1].CurrentTone);
    }

    [Fact]
    public async Task After_an_import_a_Tweak_can_ask_the_provider()
    {
        var h = new Harness();
        h.Vm.Description = "crunchy Plexi rhythm";
        h.Vm.ReplyText = PastedAnswer; // no copy first: the box is the request
        await h.Vm.ImportReplyCommand.ExecuteAsync(null);
        var imported = h.Vm.Result!.Tone;
        Assert.Equal("crunchy Plexi rhythm", h.Service.Imports[0].Request.Description);

        h.Vm.TweakText = "more mids";
        await h.Vm.TweakCommand.ExecuteAsync(null);

        var follow = Assert.Single(h.Service.Requests);
        Assert.Same(imported, follow.CurrentTone);
        Assert.Equal(["crunchy Plexi rhythm", "Pasted Plexi", "more mids"], follow.History.Select(t => t.Text));
    }

    [Fact]
    public void Without_a_clipboard_there_is_no_copy_step()
    {
        var service = new FakeToneDesignService();
        var vm = new ToneDesignViewModel(() => service, _ => { },
            () => new DspTonePreset("live", "Live", DspToneCategory.Clean, "", Rig("amp"), DspToneOrigin.User),
            (name, category, _, rig) => new DspTonePreset("s", name, category, "", rig, DspToneOrigin.User), FakeLocalization.English())
        {
            Description = "anything"
        };

        Assert.False(vm.CopyPromptCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_clipboard_that_refuses_is_a_localized_error_not_a_crash()
    {
        var h = new Harness();
        h.Clipboard.Throw = new InvalidOperationException("clipboard is locked by another app");
        h.Vm.Description = "anything";

        await h.Vm.CopyPromptCommand.ExecuteAsync(null);

        Assert.Equal(FakeLocalization.English()["ToneDesign_Manual_CopyFailed"], h.Vm.ManualErrorText);
        Assert.False(h.Vm.IsCopying);
        Assert.True(h.Vm.CopyPromptCommand.CanExecute(null));
    }

    /// <summary>A summary that does not name one unit per chain step falls back
    /// to the type ids rather than pairing names with the wrong units.</summary>
    [Fact]
    public void Chain_steps_fall_back_to_ids_when_the_summary_does_not_match_the_rig()
    {
        var steps = ToneChainStep.From(Rig("rat", "poweramp", "cab"), "Rat → Power Amp");
        Assert.Equal(["rat", "poweramp", "cab"], steps.Select(s => s.Name));
    }
}
