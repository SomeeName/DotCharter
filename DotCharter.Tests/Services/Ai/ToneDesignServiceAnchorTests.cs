using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     Grounding stage 2 as the SERVICE drives it: the flag, the follow-up, and the
///     fact that the stage can never cost a user their design.
///
///     <para>
///         These assert on the prompt the provider was actually handed, not on the
///         retriever's return value — that is the seam where a stage gets wired up
///         and then quietly never reaches the model, which is the shape this repo
///         has shipped more than once.
///     </para>
/// </summary>
public sealed class ToneDesignServiceAnchorTests
{
    private const string ReplyJson = """
        {"name":"Test","rationale":"because","confidence":0.8,
         "modules":[{"type":"amp","params":{"gainDb":6}},{"type":"cab","params":{}}]}
        """;

    // ---- doubles --------------------------------------------------------------

    private sealed class CapturingModel : IToneDesignModel
    {
        public ToneDesignPrompt? LastPrompt;
        public AiProviderKind Kind => AiProviderKind.Claude;

        public Task<ToneDesignModelReply> CompleteAsync(ToneDesignPrompt prompt, CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult(new ToneDesignModelReply(
                ReplyJson, new AiUsage("claude-opus-5", 10, 20)));
        }

        public Task<AiProbeResult> ProbeAsync(CancellationToken ct)
            => Task.FromResult(new AiProbeResult(true, "ok", "claude-opus-5"));

        /// <summary>Every message turn joined — what the model was really told.</summary>
        public string Conversation =>
            LastPrompt is null ? "" : string.Join("\n", LastPrompt.Messages.Select(m => m.Text));
    }

    private sealed class FakeFactory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind)
            => kind is AiProviderKind.Claude or AiProviderKind.OpenAiCompatible;
    }

    private sealed class StubLibrary : IToneAnchorLibrary
    {
        public int Lookups;

        public Task<IReadOnlyList<LibrarySongTones>> LookupAsync(
            string? title, string? artist, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult<IReadOnlyList<LibrarySongTones>>(
            [
                new LibrarySongTones("Thunderstruck", "AC/DC", 1990,
                [
                    new LibraryToneCandidate("Lead", new DcRigPreset(
                    [
                        new DcRigModule("amp", new Dictionary<string, float> { ["gainDb"] = 8f }),
                        new DcRigModule("cab", new Dictionary<string, float>())
                    ]))
                ])
            ]);
        }
    }

    private static (ToneDesignService Service, CapturingModel Model, StubLibrary Library) New(
        bool useLibraryTones = true, IToneAnchorLibrary? library = null)
    {
        var stub = library as StubLibrary ?? new StubLibrary();
        var model = new CapturingModel();
        var config = new AppConfig
        {
            Ai = new AiProviderSettings(
                AiProviderKind.Claude, "claude-opus-5", null, useLibraryTones)
        };
        return (new ToneDesignService(() => config, new FakeFactory(model), FakeLocalization.English(), null, null,
            library ?? stub), model, stub);
    }

    private static ToneDesignRequest Fresh(string text = "biting lead")
        => new(text, [], null, "Thunderstruck", "AC/DC");

    // ---- the tests ------------------------------------------------------------

    [Fact]
    public async Task WithTheFlagOn_TheAnchorReachesTheModel()
    {
        var (service, model, library) = New();

        await service.DesignAsync(Fresh(), null, CancellationToken.None);

        Assert.Equal(1, library.Lookups);
        Assert.Contains("Thunderstruck", model.Conversation, StringComparison.Ordinal);
        Assert.Contains("how Rocksmith rigged", model.Conversation, StringComparison.Ordinal);
    }

    /// <summary>The flag is the whole point of the flag: off means the library is
    /// never even ASKED, so switching it off also removes the library walk from the
    /// critical path rather than merely hiding its output.</summary>
    [Fact]
    public async Task WithTheFlagOff_TheLibraryIsNeverAsked()
    {
        var (service, model, library) = New(useLibraryTones: false);

        await service.DesignAsync(Fresh(), null, CancellationToken.None);

        Assert.Equal(0, library.Lookups);
        Assert.DoesNotContain("Rocksmith rigged", model.Conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("Thunderstruck\" by", model.Conversation, StringComparison.Ordinal);
        Assert.Equal(ToneDesignPromptBuilder.SystemPrompt, model.LastPrompt!.System);
    }

    /// <summary>
    ///     A FOLLOW-UP keeps its anchor. Without this "less gain" would be measured
    ///     against whatever the last answer happened to be, and two tweaks in a row
    ///     would walk away from the song entirely — the anchor is the one thing in
    ///     the conversation that is not drifting.
    /// </summary>
    [Fact]
    public async Task AFollowUp_KeepsTheAnchor()
    {
        var (service, model, library) = New();

        await service.DesignAsync(Fresh(), null, CancellationToken.None);

        var current = new DspTonePreset(
            "ai-1", "First", DspToneCategory.Lead, "",
            new DcRigPreset([new DcRigModule("amp", new Dictionary<string, float>())]),
            DspToneOrigin.User);
        var followUp = new ToneDesignRequest(
            "less gain",
            [new ToneDesignTurn(ToneDesignRole.User, "biting lead"),
             new ToneDesignTurn(ToneDesignRole.Assistant, "First")],
            current, "Thunderstruck", "AC/DC");

        await service.DesignAsync(followUp, null, CancellationToken.None);

        Assert.Equal(2, library.Lookups);
        Assert.Contains("how Rocksmith rigged", model.Conversation, StringComparison.Ordinal);
        // …and it still carries the CURRENT chain and the edit instruction.
        Assert.Contains("This is the tone currently on the rig", model.Conversation, StringComparison.Ordinal);
        Assert.Contains("less gain", model.Conversation, StringComparison.Ordinal);
    }

    /// <summary>A build without the library slice designs exactly as it did before
    /// this stage existed.</summary>
    [Fact]
    public async Task WithNoLibraryAtAll_TheRequestIsSimplyUngrounded()
    {
        var config = new AppConfig
        {
            Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null)
        };
        var model = new CapturingModel();
        var service = new ToneDesignService(() => config, new FakeFactory(model), localization: FakeLocalization.English());

        var result = await service.DesignAsync(Fresh(), null, CancellationToken.None);

        Assert.NotNull(result.Tone);
        Assert.Equal(ToneDesignPromptBuilder.SystemPrompt, model.LastPrompt!.System);
    }

    /// <summary>A grounding stage must never cost a user their design.</summary>
    [Fact]
    public async Task AThrowingLibrary_StillProducesATone()
    {
        var (service, model, _) = New(library: new ThrowingLibrary());

        var result = await service.DesignAsync(Fresh(), null, CancellationToken.None);

        Assert.NotNull(result.Tone);
        Assert.DoesNotContain("Rocksmith rigged", model.Conversation, StringComparison.Ordinal);
    }

    private sealed class ThrowingLibrary : IToneAnchorLibrary
    {
        public Task<IReadOnlyList<LibrarySongTones>> LookupAsync(
            string? title, string? artist, CancellationToken ct)
            => throw new IOException("the library is on a disconnected drive");
    }
}
