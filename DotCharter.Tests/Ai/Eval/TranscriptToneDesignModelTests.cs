using System.Text.Json;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The transcript provider, offline and in the DEFAULT gate: it writes the
///     prompt it was given, reads the reply back, and says which file is missing
///     when there is none. Nothing here touches the network or the live eval gate.
/// </summary>
public sealed class TranscriptToneDesignModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "dc-transcript-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ToneDesignPrompt PromptFor(ToneEvalEntry entry)
        => ToneDesignPromptBuilder.Build(new ToneDesignRequest(
            entry.Prompt, Array.Empty<ToneDesignTurn>(), null, entry.SongTitle, entry.Artist));

    [Fact]
    public async Task ItWritesThePromptAndReadsTheReplyBack()
    {
        var entry = ToneEvalCorpus.Fixtures[0];
        var index = ToneTranscriptIndex.FromCorpus(ToneEvalCorpus.Fixtures);
        var model = new TranscriptToneDesignModel(_dir, index);
        var prompt = PromptFor(entry);

        // Pass one: no reply on disk. The prompt is written and the fault names the
        // file the answer belongs in.
        var missing = await Assert.ThrowsAsync<ToneDesignException>(
            () => model.CompleteAsync(prompt, TestContext.Current.CancellationToken));
        Assert.Equal(ToneDesignFault.InvalidOutput, missing.Fault);
        Assert.Contains("00.reply.json", missing.Message, StringComparison.Ordinal);

        var promptPath = Path.Combine(_dir, "00.prompt.json");
        Assert.True(File.Exists(promptPath));

        // The prompt file is the WHOLE prompt — system, messages, schema, schema
        // name — because a transcript that drops half of it is a different prompt
        // from the one the wire provider would have sent.
        using (var doc = JsonDocument.Parse(File.ReadAllText(promptPath)))
        {
            var root = doc.RootElement;
            Assert.Equal(prompt.System, root.GetProperty("system").GetString());
            Assert.Equal(prompt.JsonSchema, root.GetProperty("jsonSchema").GetString());
            Assert.Equal(prompt.SchemaName, root.GetProperty("schemaName").GetString());
            var messages = root.GetProperty("messages");
            Assert.Equal(prompt.Messages.Count, messages.GetArrayLength());
            Assert.Equal(prompt.Messages[^1].Text,
                messages[messages.GetArrayLength() - 1].GetProperty("text").GetString());
        }

        // Pass two: the answer is on disk, fenced the way a chat model writes it.
        File.WriteAllText(Path.Combine(_dir, "00.reply.json"),
            "Here you go!\n```json\n" +
            """{"name":"Test","rationale":"why","confidence":0.7,"modules":[{"type":"amp","params":{"gain":0.5},"bypassed":false}]}""" +
            "\n```\n");

        var reply = await model.CompleteAsync(prompt, TestContext.Current.CancellationToken);

        // ExtractJsonObject ran, exactly as it does for the real providers: the
        // service hands reply.Json straight to the validator and would choke on prose.
        var validation = ToneDraftValidator.Validate(reply.Json);
        Assert.NotNull(validation.Preset);
        Assert.Equal("Test", validation.Draft!.Name);
        Assert.True(reply.Usage.InputTokens > 0);
        Assert.True(reply.Usage.OutputTokens > 0);
    }

    [Fact]
    public void TheFileNameComesFromTheCORPUSPositionNotTheCallOrder()
    {
        var corpus = ToneEvalCorpus.Fixtures;
        var index = ToneTranscriptIndex.FromCorpus(corpus);
        var model = new TranscriptToneDesignModel(_dir, index);

        // Asked BACKWARDS, every entry still answers with its own ordinal. A naming
        // scheme that counted calls would number these 00,01,02,… in reverse — and a
        // transcript whose numbering moves between runs cannot be answered at all.
        for (var i = corpus.Count - 1; i >= 0; i--)
        {
            Assert.Equal(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                model.FileBaseFor(PromptFor(corpus[i])));

            // The sparse prompt is the same entry and gets the same ordinal, suffixed.
            var shortPrompt = ToneDesignPromptBuilder.Build(new ToneDesignRequest(
                corpus[i].ShortPrompt, Array.Empty<ToneDesignTurn>(), null,
                corpus[i].SongTitle, corpus[i].Artist));
            Assert.Equal(
                i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "-short",
                model.FileBaseFor(shortPrompt));
        }
    }

    [Fact]
    public void AnAnchoredPromptKeepsTheSameFileNameAsItsUnanchoredTwin()
    {
        // The anchor block is prepended to the SAME user turn, so the identity the
        // index matches on (the trailing Request:/Song:/Artist: lines) is unchanged.
        // That is what lets an anchored run live in its own directory under the same
        // numbering rather than in a re-numbered one.
        var corpus = ToneEvalCorpus.Fixtures;
        var model = new TranscriptToneDesignModel(_dir, ToneTranscriptIndex.FromCorpus(corpus));
        var entry = corpus[1];

        var anchored = ToneDesignPromptBuilder.Build(
            new ToneDesignRequest(entry.Prompt, Array.Empty<ToneDesignTurn>(), null,
                entry.SongTitle, entry.Artist),
            new ToneAnchorSet(
                new ToneAnchor(entry.SongTitle, entry.Artist, entry.ToneName,
                    new ToneDraft(entry.ToneName, "", 0.9,
                        entry.Truth.Modules
                            .Select(m => new ToneDraftModule(
                                m.ModuleTypeId,
                                new Dictionary<string, float>(m.Params, StringComparer.Ordinal),
                                m.Bypassed))
                            .ToList())),
                Array.Empty<ToneAnchor>()));

        Assert.NotEqual(
            ToneDesignPromptBuilder.SystemPrompt, anchored.System); // it really is the anchored prompt
        Assert.Equal("01", model.FileBaseFor(anchored));
    }

    [Fact]
    public void AnUnknownPromptGetsAStableHashedNameRatherThanAnOrdinal()
    {
        var model = new TranscriptToneDesignModel(_dir, ToneTranscriptIndex.Empty);
        var prompt = PromptFor(ToneEvalCorpus.Fixtures[0]);

        var first = model.FileBaseFor(prompt);
        Assert.StartsWith("unmatched-", first, StringComparison.Ordinal);
        Assert.Equal(first, model.FileBaseFor(prompt));
        Assert.NotEqual(first, model.FileBaseFor(PromptFor(ToneEvalCorpus.Fixtures[1])));
    }

    [Fact]
    public void TheEnvironmentResolvesTheTranscriptProviderWithoutAKey()
    {
        var names = new[]
        {
            ToneEvalEnvironment.EnableVariable, ToneEvalEnvironment.ProviderVariable,
            ToneEvalEnvironment.ModelVariable, ToneEvalEnvironment.KeyVariable,
            ToneEvalEnvironment.AnthropicKeyVariable, ToneEvalEnvironment.TranscriptDirVariable
        };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.EnableVariable, "1");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ProviderVariable, "transcript");
            Environment.SetEnvironmentVariable(ToneEvalEnvironment.ModelVariable, "claude-opus-5");

            // The directory is the one thing it cannot do without.
            Assert.False(ToneEvalEnvironment.TryResolve(out _, out var noDir));
            Assert.Contains(ToneEvalEnvironment.TranscriptDirVariable, noDir, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ToneEvalEnvironment.TranscriptDirVariable, _dir);
            Assert.True(ToneEvalEnvironment.TryResolve(out var resolved, out _));
            Assert.Equal(_dir, resolved!.TranscriptDir);
            Assert.Null(resolved.ApiKey); // no key, and none demanded
            Assert.Equal("transcript", resolved.ToOptions().Provider);

            // …and the locator builds the REAL service over the transcript factory.
            Assert.True(ToneDesignServiceLocator.TryCreate(
                resolved, null, out var service, out _, ToneEvalCorpus.Fixtures));
            Assert.IsType<ToneDesignService>(service);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
