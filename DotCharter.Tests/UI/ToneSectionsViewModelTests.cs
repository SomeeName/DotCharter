using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode.ToneSections;

namespace DotCharter.Tests.UI;

/// <summary>
///     "Match by section" — one rig per part of the song — against fake section
///     services. No Avalonia and no audio device: every assertion is about what
///     the view model DRIVES.
///
///     <para>
///         The seam assertions carry the weight here for the same reason they do
///         in <see cref="ToneDesignMatchTests" />. A section's chosen rig must
///         reach the panel's own <c>Result</c> (so Hear it, Save as tone and
///         Discard are not re-implemented per section) and its tone must be saved
///         through the drawer's own save path; a private route would pass a
///         "the list has candidates" test and still be the second door onto the
///         rig this whole surface exists to avoid.
///     </para>
/// </summary>
public sealed class ToneSectionsViewModelTests
{
    // ---- fakes ----------------------------------------------------------------

    private sealed class FakeHost : IToneSectionHost
    {
        public ToneMatchSource? Source = new("C:/songs/song.wav", Array.Empty<ToneSpan>(), 0, 0);
        public MonoAudio? Di = new(new float[4800], 48_000);
        public (string? Title, string? Artist) Song = ("Master of Puppets", "Metallica");

        public readonly List<(ToneDesignResult Result, string Chain)> Published = [];
        public readonly List<(string Name, DspToneCategory Category, string Description, DcRigPreset Rig)> Saved = [];

        public ToneMatchSource? ResolveMatchSource() => Source;

        public Task<MonoAudio?> EnsureReferenceDiAsync(CancellationToken ct) => Task.FromResult(Di);

        public string ChainSummary(DcRigPreset rig) =>
            string.Join(" → ", rig.Modules.Select(m => m.ModuleTypeId));

        public (string? Title, string? Artist) SongContext() => Song;

        public void PublishResult(ToneDesignResult result, string chainSummary) =>
            Published.Add((result, chainSummary));

        public DspTonePreset SaveTone(
            string name, DspToneCategory category, string description, DcRigPreset rig)
        {
            Saved.Add((name, category, description, rig));
            return new DspTonePreset($"saved-{Saved.Count}", name, category, description, rig, DspToneOrigin.User);
        }
    }

    private sealed class FakeProposer : IToneSectionProposer
    {
        public readonly List<(string Path, string Stem, IReadOnlyList<ToneSection> Chart)> Calls = [];
        public IReadOnlyList<ToneSectionProposal>? Answer;

        public Task<IReadOnlyList<ToneSectionProposal>> ProposeAsync(
            string songAudioPath, string stem, IReadOnlyList<ToneSection> chartSpans,
            IProgress<string>? status, CancellationToken ct)
        {
            Calls.Add((songAudioPath, stem, chartSpans));
            status?.Report("scanning");
            return Task.FromResult(Answer ?? Array.Empty<ToneSectionProposal>());
        }
    }

    private sealed class FakeSetMatcher : IToneSetMatcher
    {
        public readonly List<ToneSetMatchRequest> Requests = [];
        public TaskCompletionSource<bool>? Block;
        public Func<ToneSetMatchRequest, ToneSetMatchResult>? Answer;

        public async Task<ToneSetMatchResult> MatchAsync(
            ToneSetMatchRequest request, IProgress<string>? status, CancellationToken ct)
        {
            Requests.Add(request);
            status?.Report("section 1 of 2");
            // A real matcher observes the token; one that does not would make the
            // cancel case assert nothing.
            if (Block != null) await Block.Task.WaitAsync(ct);
            return Answer?.Invoke(request) ?? AllMatched(request);
        }
    }

    private sealed class FakeNarrator : IToneNarrator
    {
        public bool IsAvailable { get; init; } = true;
        public ToneNarration? Answer = new("Crunch", "a mid-gain crunch; the amp is doing the work", new AiUsage("m", 1, 1));
        public readonly List<ToneSection> Calls = [];

        public Task<ToneNarration?> NarrateAsync(
            DcRigPreset rig, ToneSection section, string? songTitle, string? artist, CancellationToken ct)
        {
            Calls.Add(section);
            return Task.FromResult(Answer);
        }
    }

    // ---- helpers --------------------------------------------------------------

    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    private static ToneMatchResult MatchWith(params double[] distances) =>
        new(
            distances.Select((d, i) => new ToneMatchCandidate(Rig($"amp{i}"), d, $"origin{i}")).ToList(),
            0.2,
            TimeSpan.FromSeconds(3),
            ["rendered 400 rigs"]);

    private static ToneSetMatchResult AllMatched(ToneSetMatchRequest request) =>
        new(
            request.Sections.Select(s => new ToneSectionMatch(s, MatchWith(0.2, 0.6), null)).ToList(),
            TimeSpan.FromSeconds(9),
            []);

    private static readonly FakeLocalization Strings = FakeLocalization.English();

    private static string Loc(string key) => Strings[key];

    private sealed class Harness
    {
        public readonly FakeHost Host = new();
        public readonly FakeProposer Proposer = new();
        public readonly FakeSetMatcher Matcher = new();
        public readonly FakeNarrator Narrator = new();
        public readonly ToneSectionsViewModel Vm;

        /// <summary>Every entry "apply as tone set" asked the host to place, and
        /// the answer it was given. Null answer = this project has no
        /// arrangement.</summary>
        public readonly List<IReadOnlyList<ToneSetEntry>> Placed = [];

        public int? PlaceAnswer = 0;
        public double PlayheadMs;

        public Harness(
            bool withProposer = true,
            bool withMatcher = true,
            bool withNarrator = true,
            bool withPlacement = true)
        {
            Func<IReadOnlyList<ToneSetEntry>, int?> place = entries =>
            {
                Placed.Add(entries);
                return PlaceAnswer;
            };

            Vm = new ToneSectionsViewModel(Host, Strings);
            Vm.Configure(
                () => withProposer ? Proposer : null,
                () => withMatcher ? Matcher : null,
                () => withNarrator ? Narrator : null,
                () => PlayheadMs,
                withPlacement ? place : null);
        }

        public ToneSectionRowViewModel Add(string label, double fromSeconds, double toSeconds) =>
            Vm.AddForTests(
                label, ToneStems.Guitar,
                TimeSpan.FromSeconds(fromSeconds), TimeSpan.FromSeconds(toSeconds));
    }

    // ---- the section list -----------------------------------------------------

    [Fact]
    public void AddSection_TakesThePlayhead_AndTheNewRowIsAlreadyValid()
    {
        var h = new Harness { PlayheadMs = 12_000 };

        h.Vm.AddSectionCommand.Execute(null);

        var row = Assert.Single(h.Vm.Sections);
        Assert.Equal(TimeSpan.FromSeconds(12), row.From);
        Assert.Equal(TimeSpan.FromSeconds(12) + ToneSectionsViewModel.NewSectionLength, row.To);
        // A list whose Add button produces an invalid row reads as broken.
        Assert.True(row.IsValid, $"a fresh row was invalid: {row.TimeError}");
        Assert.True(h.Vm.HasSections);
    }

    [Fact]
    public void RemovingARow_TakesItOutOfTheList()
    {
        var h = new Harness();
        var row = h.Add("Clean", 10, 30);

        row.RemoveCommand.Execute(null);

        Assert.Empty(h.Vm.Sections);
        Assert.False(h.Vm.HasSections);
    }

    [Fact]
    public void SetFromPlayhead_WritesTheTransportsPosition_AsTextAUserCouldHaveTyped()
    {
        var h = new Harness { PlayheadMs = 67_000 };
        var row = h.Add("Clean", 10, 30);

        row.SetFromPlayheadCommand.Execute(null);

        Assert.Equal("1:07", row.FromText);
        Assert.Equal(TimeSpan.FromSeconds(67), row.From);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("0:75")]
    [InlineData("")]
    public void AnUnparseableTime_InvalidatesTheRow_AndSaysSo(string text)
    {
        var h = new Harness();
        var row = h.Add("Clean", 10, 30);

        row.FromText = text;

        Assert.False(row.IsValid);
        Assert.Equal(Loc("ToneSections_Fault_BadTime"), row.TimeError);
    }

    [Fact]
    public void AnEndBeforeTheStart_IsRefused()
    {
        var h = new Harness();
        var row = h.Add("Clean", 10, 30);

        row.ToText = "0:05";

        Assert.False(row.IsValid);
        Assert.Equal(Loc("ToneSections_Fault_EndBeforeStart"), row.TimeError);
    }

    [Fact]
    public void ASectionShorterThanTheContractsFloor_IsRefused()
    {
        var h = new Harness();
        var row = h.Add("Clean", 10, 30);

        row.ToText = "0:12";

        Assert.False(row.IsValid);
        Assert.Contains(
            ((int)ToneSection.MinDuration.TotalSeconds).ToString(),
            row.TimeError);
    }

    [Fact]
    public async Task AnInvalidRow_IsLeftOutOfTheRun_ButTheValidOnesStillMatch()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);
        var broken = h.Add("Broken", 40, 60);
        broken.FromText = "nonsense";

        await h.Vm.MatchAllCommand.ExecuteAsync(null);

        var request = Assert.Single(h.Matcher.Requests);
        Assert.Equal("Clean", Assert.Single(request.Sections).Label);
    }

    // ---- proposals ------------------------------------------------------------

    [Fact]
    public async Task WithNoProposerRegistered_TheChartsOwnToneChangesAreTheOffer()
    {
        var h = new Harness(withProposer: false);
        h.Host.Source = new ToneMatchSource(
            "C:/songs/song.wav",
            [new ToneSpan(0, "Clean", 0, 20_000), new ToneSpan(1, "Lead", 20_000, 45_000)],
            0,
            45_000);

        await h.Vm.ProposeCommand.ExecuteAsync(null);

        Assert.Equal(2, h.Vm.Proposals.Count);
        Assert.All(h.Vm.Proposals, p => Assert.Equal(ToneSectionOrigins.Chart, p.Proposal.Origin));
        Assert.Equal("Clean", h.Vm.Proposals[0].Title);
    }

    [Fact]
    public async Task TheProposer_IsHandedTheChartsSpans_AndItsAnswerIsTheOffer()
    {
        var h = new Harness();
        h.Host.Source = new ToneMatchSource(
            "C:/songs/song.wav", [new ToneSpan(0, "Clean", 0, 20_000)], 0, 45_000);
        h.Proposer.Answer =
        [
            new ToneSectionProposal(
                new ToneSection("p1", "Verse", ToneStems.Guitar, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(20)),
                0.9, ToneSectionOrigins.Both)
        ];

        await h.Vm.ProposeCommand.ExecuteAsync(null);

        var call = Assert.Single(h.Proposer.Calls);
        Assert.Equal("C:/songs/song.wav", call.Path);
        Assert.Equal(ToneStems.Guitar, call.Stem);
        // The chart's spans go IN — that is what lets a proposer report a
        // boundary the audio AGREES with.
        Assert.Equal("Clean", Assert.Single(call.Chart).Label);

        var offered = Assert.Single(h.Vm.Proposals);
        Assert.Equal("Verse", offered.Title);
        Assert.Equal(Loc("ToneSections_Origin_Both"), offered.OriginText);
    }

    [Fact]
    public async Task AcceptingAProposal_AddsTheSection_AndTakesTheOfferAway()
    {
        var h = new Harness();
        h.Proposer.Answer =
        [
            new ToneSectionProposal(
                new ToneSection("p1", "Verse", ToneStems.Bass, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)),
                0.8, ToneSectionOrigins.Audio)
        ];
        await h.Vm.ProposeCommand.ExecuteAsync(null);

        h.Vm.Proposals[0].AcceptCommand.Execute(null);

        var row = Assert.Single(h.Vm.Sections);
        Assert.Equal("Verse", row.Label);
        Assert.Equal(ToneStems.Bass, row.SelectedStem.Id);
        Assert.Equal("0:10", row.FromText);
        Assert.Empty(h.Vm.Proposals);
    }

    [Fact]
    public async Task RejectingAProposal_AddsNothing()
    {
        var h = new Harness();
        h.Proposer.Answer =
        [
            new ToneSectionProposal(
                new ToneSection("p1", "Verse", ToneStems.Guitar, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)),
                0.8, ToneSectionOrigins.Audio)
        ];
        await h.Vm.ProposeCommand.ExecuteAsync(null);

        h.Vm.Proposals[0].RejectCommand.Execute(null);

        Assert.Empty(h.Vm.Sections);
        Assert.Empty(h.Vm.Proposals);
        Assert.False(h.Vm.HasProposals);
    }

    [Fact]
    public async Task AProposerThatFindsNothing_IsAMessage_NotAnEmptyList()
    {
        var h = new Harness();
        h.Proposer.Answer = Array.Empty<ToneSectionProposal>();

        await h.Vm.ProposeCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneSections_Fault_NoProposals"), h.Vm.ErrorText);
    }

    // ---- match all ------------------------------------------------------------

    [Fact]
    public async Task MatchAll_SendsEverySectionInSongOrder_WithTheSongContext()
    {
        var h = new Harness();
        h.Add("Lead", 40, 60);
        h.Add("Clean", 10, 30);

        await h.Vm.MatchAllCommand.ExecuteAsync(null);

        var request = Assert.Single(h.Matcher.Requests);
        Assert.Equal(new[] { "Clean", "Lead" }, request.Sections.Select(s => s.Label).ToArray());
        Assert.Equal("Master of Puppets", request.SongTitle);
        Assert.Equal("Metallica", request.Artist);
        Assert.Same(h.Host.Di, request.ReferenceDi);
    }

    [Fact]
    public async Task MatchAll_SelectsEachSectionsBestCandidate_AndPublishesItAsThePanelsResult()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);

        await h.Vm.MatchAllCommand.ExecuteAsync(null);

        var row = Assert.Single(h.Vm.Sections);
        Assert.Equal(2, row.Candidates.Count);
        // Ranked best-first and the best one selected: a ranked list whose top row
        // the player must click is a step that only ever has one answer.
        Assert.Same(row.Candidates[0], row.SelectedCandidate);
        Assert.True(row.Candidates[0].Distance < row.Candidates[1].Distance);

        // THE seam: the chosen rig lands in the panel's own result, which is what
        // hands it to Hear it / Save as tone / Discard unchanged.
        var published = Assert.Single(h.Host.Published);
        Assert.Same(row.Candidates[0].Tone, published.Result.Tone);
    }

    [Fact]
    public async Task OneFailingSection_DoesNotTakeTheSetDown()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);
        h.Add("Lead", 40, 60);
        h.Matcher.Answer = request => new ToneSetMatchResult(
            [
                new ToneSectionMatch(request.Sections[0], MatchWith(0.2), null),
                new ToneSectionMatch(request.Sections[1], null, "the separator produced silence")
            ],
            TimeSpan.FromSeconds(9),
            []);

        await h.Vm.MatchAllCommand.ExecuteAsync(null);

        var clean = h.Vm.Sections.Single(r => r.Label == "Clean");
        var lead = h.Vm.Sections.Single(r => r.Label == "Lead");
        Assert.Single(clean.Candidates);
        Assert.False(clean.IsFailed);
        Assert.Empty(lead.Candidates);
        Assert.True(lead.IsFailed);
        // The service's own words go to the log, never to the row.
        Assert.Equal(Loc("ToneSections_StatusFailed"), lead.StatusText);
        Assert.DoesNotContain("separator", lead.StatusText);
        Assert.Empty(h.Vm.ErrorText);
    }

    [Fact]
    public async Task CancelDuringTheSearch_ReportsCancelled_AndIsNotAFault()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);
        h.Matcher.Block = new TaskCompletionSource<bool>();

        var run = h.Vm.MatchAllCommand.ExecuteAsync(null);
        Assert.True(h.Vm.IsBusy);
        h.Vm.CancelCommand.Execute(null);
        await run;

        Assert.Equal(Loc("ToneSections_Cancelled"), h.Vm.Status);
        Assert.Empty(h.Vm.ErrorText);
        Assert.False(h.Vm.IsBusy);
    }

    [Fact]
    public async Task WithNoSections_MatchAllSaysSo_RatherThanSearchingNothing()
    {
        var h = new Harness();

        await h.Vm.MatchAllCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneSections_Fault_NoSections"), h.Vm.ErrorText);
        Assert.Empty(h.Matcher.Requests);
    }

    [Fact]
    public async Task WithNoReferenceDi_MatchAllSaysWhatItNeeds()
    {
        var h = new Harness();
        h.Host.Di = null;
        h.Add("Clean", 10, 30);

        await h.Vm.MatchAllCommand.ExecuteAsync(null);

        Assert.Equal(Loc("ToneSections_Fault_NoReferenceDi"), h.Vm.ErrorText);
        Assert.Empty(h.Matcher.Requests);
    }

    // ---- explain --------------------------------------------------------------

    [Fact]
    public async Task Explain_PutsTheNarratorsWordsUnderTheSection()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);
        await h.Vm.MatchAllCommand.ExecuteAsync(null);
        var row = Assert.Single(h.Vm.Sections);

        await row.ExplainCommand.ExecuteAsync(null);

        Assert.Equal("a mid-gain crunch; the amp is doing the work", row.Explanation);
        Assert.True(row.HasExplanation);
        // One call per "Explain", never one per candidate.
        Assert.Equal("Clean", Assert.Single(h.Narrator.Calls).Label);
    }

    [Fact]
    public async Task WithNoNarrator_ExplainIsNotOffered()
    {
        var h = new Harness(withNarrator: false);
        h.Add("Clean", 10, 30);
        await h.Vm.MatchAllCommand.ExecuteAsync(null);

        var row = Assert.Single(h.Vm.Sections);
        Assert.False(row.ExplainCommand.CanExecute(null));
    }

    // ---- apply as tone set ----------------------------------------------------

    [Fact]
    public async Task Apply_SavesOnePresetPerSection_AndPlacesOneToneChangeAtEachStart()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);
        h.Add("Lead", 40, 60);
        await h.Vm.MatchAllCommand.ExecuteAsync(null);
        h.PlaceAnswer = 2;

        h.Vm.ApplyCommand.Execute(null);

        // One tone per section, named after it, through the drawer's OWN save path.
        Assert.Equal(new[] { "Clean", "Lead" }, h.Host.Saved.Select(s => s.Name).ToArray());

        var entries = Assert.Single(h.Placed);
        Assert.Equal(2, entries.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), entries[0].Section.From);
        Assert.Equal(TimeSpan.FromSeconds(40), entries[1].Section.From);
        // The tone change names the tone that was SAVED, not the candidate's
        // working title — otherwise the chart points at a name the library has
        // not got.
        Assert.Equal(h.Host.Saved[0].Name, entries[0].Tone.Name);

        Assert.Equal(
            Strings.Format("ToneSections_Applied", 2, 2),
            h.Vm.Status);
    }

    [Fact]
    public async Task Apply_OnASongWithNoArrangement_StillSavesThePresets_AndSaysSo()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);
        await h.Vm.MatchAllCommand.ExecuteAsync(null);
        // NULL is "there is no arrangement", which is a different answer from
        // "placed none" — a song that is not in the library is exactly the case
        // this feature exists for.
        h.PlaceAnswer = null;

        h.Vm.ApplyCommand.Execute(null);

        Assert.Single(h.Host.Saved);
        Assert.Equal(
            Strings.Format("ToneSections_AppliedPresetsOnly", 1),
            h.Vm.Status);
        Assert.Empty(h.Vm.ErrorText);
    }

    [Fact]
    public void Apply_WithNothingChosen_SavesNothing_AndSaysWhy()
    {
        var h = new Harness();
        h.Add("Clean", 10, 30);

        // Not merely disabled: the command refuses, and a caller that reached it
        // anyway gets a sentence rather than an empty tone set.
        Assert.False(h.Vm.ApplyCommand.CanExecute(null));
        h.Vm.ApplyCommand.Execute(null);

        Assert.Empty(h.Host.Saved);
        Assert.Empty(h.Placed);
    }

    // ---- availability ---------------------------------------------------------

    [Fact]
    public void WithNeitherAProposerNorASetMatcher_TheBlockIsNotAvailable()
    {
        var h = new Harness(withProposer: false, withMatcher: false);
        // No chart spans either, so nothing at all can be offered.
        h.Host.Source = new ToneMatchSource("C:/songs/song.wav", Array.Empty<ToneSpan>(), 0, 0);

        Assert.False(h.Vm.IsAvailable);
        Assert.False(h.Vm.CanPropose);
        Assert.False(h.Vm.CanMatchAll);
    }

    [Fact]
    public void AFreshPanel_WithNoSectionSliceConfigured_KeepsTheBlockOffScreen()
    {
        var panel = NewPanel();

        // Not merely disabled: a capability the app cannot honour must not be on
        // screen looking live. And the flow must not be built by simply reading
        // the property.
        Assert.False(panel.IsSectionsAvailable);
        Assert.False(panel.Sections.IsAvailable);
    }

    [Fact]
    public void ConfiguringTheSectionSlice_PutsTheBlockOnScreen()
    {
        var panel = NewPanel();

        panel.ConfigureSections(static () => null, () => new FakeSetMatcher());

        Assert.True(panel.IsSectionsAvailable);
    }

    [Fact]
    public void ConfiguringWithNoServices_LeavesTheBlockOffScreen()
    {
        var panel = NewPanel();

        panel.ConfigureSections(static () => null, static () => null);

        Assert.False(panel.IsSectionsAvailable);
    }

    [Fact]
    public void ASectionsToneIsSavedThroughThePanelsOwnSaveSeam()
    {
        var saved = new List<string>();
        var panel = NewPanel(name => saved.Add(name));
        panel.ConfigureMatch(static () => null, static () => null);
        panel.ConfigureSections(static () => null, () => new FakeSetMatcher());
        var host = (IToneSectionHost)panel;

        var row = panel.Sections.AddForTests(
            "Clean", ToneStems.Guitar, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        row.PublishCandidates(MatchWith(0.2), host);

        panel.Sections.ApplyCommand.Execute(null);

        Assert.Equal("Clean", Assert.Single(saved));
        // …and the chosen candidate reached the panel's own result block.
        Assert.NotNull(panel.Result);
    }

    private static ToneDesignViewModel NewPanel(Action<string>? onSave = null) =>
        new(
            static () => null,
            static _ => { },
            static () => new DspTonePreset("live", "Live", DspToneCategory.Clean, "", Rig("amp"), DspToneOrigin.User),
            (name, category, description, rig) =>
            {
                onSave?.Invoke(name);
                return new DspTonePreset("s", name, category, description, rig, DspToneOrigin.User);
            },
            FakeLocalization.English(),
            static id => id);

    // ---- the clock ------------------------------------------------------------

    [Theory]
    [InlineData("0:10", 10)]
    [InlineData("1:07", 67)]
    [InlineData("1:07.5", 67.5)]
    [InlineData("90", 90)]
    public void TimesAreParsedAsTimecodes(string text, double seconds)
    {
        Assert.True(ToneSectionTime.TryParse(text, out var value));
        Assert.Equal(seconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("0:75")]
    [InlineData("-1:00")]
    [InlineData("1:-5")]
    [InlineData("banana")]
    [InlineData(null)]
    public void ATimeThatIsNotATimecode_IsRefusedRatherThanGuessedAt(string? text) => Assert.False(ToneSectionTime.TryParse(text, out _));

    [Theory]
    [InlineData(10, "0:10")]
    [InlineData(67, "1:07")]
    [InlineData(67.5, "1:07.5")]
    [InlineData(59.96, "1:00")]
    public void TimesArePrintedAsTimecodes(double seconds, string text) => Assert.Equal(text, ToneSectionTime.Format(TimeSpan.FromSeconds(seconds)));
}
