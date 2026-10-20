namespace DotCharter.Tone.Ai;

/// <summary>
///     Settings + the secret store → a provider, or a
///     <see cref="ToneDesignFault.NotConfigured" /> that names the ONE thing that is
///     missing.
///
///     <para>
///         Every "not configured" message is specific, because "AI is not
///         configured" in a Settings page that looks filled in is the shape of
///         support request nobody can answer. The four reachable causes are: no
///         provider chosen, no model id, Claude without a key, OpenAI-compatible
///         without a base URL.
///     </para>
///     <para>
///         <see cref="AiProviderKind.BuiltIn" /> is a RESERVED slot (a bundled
///         downloadable model) and is not implemented: it answers NotConfigured with
///         a message saying so, rather than throwing something the UI would render
///         as a bug. See <c>docs/agents/services/ai.md</c> § adding a provider.
///     </para>
/// </summary>
public sealed class ToneDesignModelFactory : IToneDesignModelFactory
{
    private readonly IAppLogger? _logger;
    private readonly IAiSecretStore _secrets;
    private readonly IClaudeCliLocator _cliLocator;
    private readonly IClaudeCliRunner _cliRunner;
    private readonly IClaudeCliLocator _codexLocator;

    /// <summary>Creates the factory over <paramref name="secrets" />.</summary>
    /// <param name="secrets">The API-key store.</param>
    /// <param name="logger">Optional app logger.</param>
    /// <param name="cliLocator">Finds the local <c>claude</c> binary. Defaults to the
    /// real PATH-searching locator; the tests hand in a fake.</param>
    /// <param name="cliRunner">Runs that binary. Defaults to the real process runner.</param>
    /// <param name="codexLocator">Finds the local <c>codex</c> binary (the Codex CLI
    /// provider). Defaults to the real locator; the tests hand in a fake.</param>
    public ToneDesignModelFactory(
        IAiSecretStore secrets,
        IAppLogger? logger = null,
        IClaudeCliLocator? cliLocator = null,
        IClaudeCliRunner? cliRunner = null,
        IClaudeCliLocator? codexLocator = null)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger;
        _cliLocator = cliLocator ?? new ClaudeCliLocator(logger);
        _cliRunner = cliRunner ?? new ClaudeCliRunner();
        _codexLocator = codexLocator ?? new CodexCliLocator(logger);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The ONE list of kinds this build can mint, and the Settings picker's
    ///     enabled rows come from it. Written as the set of kinds
    ///     <see cref="Create" /> has a real arm for, never as a second copy of that
    ///     switch kept in the UI: <see cref="AiProviderKind.None" /> and
    ///     <see cref="AiProviderKind.BuiltIn" /> both END in a NotConfigured throw,
    ///     so neither is a provider. Fill the reserved built-in slot in and this
    ///     answers true for it without the picker being touched.
    ///     <para>
    ///         <see cref="AiProviderKind.ClaudeCli" /> is the one kind whose answer
    ///         depends on the MACHINE rather than on the build: it is supported when
    ///         a <c>claude</c> binary can be found, and the Settings picker greys the
    ///         row out on a machine without one. The lookup is cached for the process
    ///         inside <see cref="ClaudeCliLocator" />, because this is called once per
    ///         provider row every time the tab is opened.
    ///     </para>
    /// </remarks>
    public bool Supports(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Claude or AiProviderKind.OpenAiCompatible => true,
        AiProviderKind.ClaudeCli => _cliLocator.Locate(null) != null,
        AiProviderKind.CodexCli => _codexLocator.Locate(null) != null,
        _ => false
    };

    /// <inheritdoc />
    public IToneDesignModel Create(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.Kind)
        {
            case AiProviderKind.None:
                throw NotConfigured("No AI provider is selected. Choose one under Settings → AI.");

            case AiProviderKind.BuiltIn:
                throw NotConfigured(
                    "The built-in local model is not available yet. Choose Claude or a " +
                    "local OpenAI-compatible server under Settings → AI.");

            case AiProviderKind.Claude:
            {
                if (string.IsNullOrWhiteSpace(settings.ModelId))
                    throw NotConfigured("No Claude model id is set under Settings → AI.");
                var key = _secrets.Read(AiProviderKind.Claude);
                if (string.IsNullOrWhiteSpace(key))
                    throw NotConfigured("No Anthropic API key is stored. Paste one under Settings → AI.");
                return new ClaudeToneModel(settings, key, _logger);
            }

            case AiProviderKind.ClaudeCli:
            {
                if (string.IsNullOrWhiteSpace(settings.ModelId))
                    throw NotConfigured("No Claude model id is set under Settings → AI.");
                // BaseUrl is REUSED here as an optional explicit path to the binary
                // — see AiProviderSettings. A path that was given and does not exist
                // is its own message: falling back to PATH would silently run a
                // different binary than the one the user pointed at.
                var path = _cliLocator.Locate(settings.BaseUrl) ?? throw NotConfigured(string.IsNullOrWhiteSpace(settings.BaseUrl)
                        ? "The Claude Code command was not found. Install Claude Code, sign in, " +
                          "and make sure \"claude\" is on your PATH."
                        : $"There is no Claude Code executable at \"{settings.BaseUrl}\".");
                // No key is read and none is passed: the CLI answers under the
                // user's own Claude Code login.
                return new ClaudeCliToneModel(settings, path, _cliRunner, _logger);
            }

            case AiProviderKind.CodexCli:
            {
                // BaseUrl is reused as an optional explicit path, as for ClaudeCli.
                // An empty model id is VALID here: the CLI's own default answers.
                var path = _codexLocator.Locate(settings.BaseUrl) ?? throw NotConfigured(string.IsNullOrWhiteSpace(settings.BaseUrl)
                        ? "The Codex command was not found. Install the Codex CLI and run \"codex login\"."
                        : $"There is no Codex executable at \"{settings.BaseUrl}\".");
                return new CodexCliToneModel(settings, path, _cliRunner, _logger);
            }

            case AiProviderKind.OpenAiCompatible:
            {
                if (string.IsNullOrWhiteSpace(settings.BaseUrl))
                {
                    throw NotConfigured(
                        "No server address is set under Settings → AI (LM Studio is usually " +
                        $"{AiProviderPresets.LmStudioBaseUrl}, Ollama {AiProviderPresets.OllamaBaseUrl}).");
                }

                if (string.IsNullOrWhiteSpace(settings.ModelId))
                    throw NotConfigured("No model id is set under Settings → AI.");
                // The key is OPTIONAL here: LM Studio and Ollama want none.
                return new OpenAiCompatibleToneModel(
                    settings, _secrets.Read(AiProviderKind.OpenAiCompatible), _logger);
            }

            default:
                throw NotConfigured($"Unknown AI provider \"{settings.Kind}\".");
        }
    }

    private static ToneDesignException NotConfigured(string message)
        => new(ToneDesignFault.NotConfigured, message);
}
