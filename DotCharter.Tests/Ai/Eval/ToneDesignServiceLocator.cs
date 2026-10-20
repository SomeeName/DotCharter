using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     Builds the REAL <see cref="ToneDesignService" /> for the live eval, from the
///     <c>DOTCHARTER_AI_*</c> environment rather than from the user's saved
///     settings.
///     <para>
///         <b>This was reflection until the service branch merged</b> (2026-09-12).
///         The harness was written in parallel with the service against nothing but
///         <c>ToneDesignContracts.cs</c>, so it hunted for an implementation by type
///         scan and skipped when it found none. That is gone: naming
///         <see cref="ToneDesignService" /> and <see cref="ToneDesignModelFactory" />
///         directly is a compile-time check where the reflection was a runtime
///         guess — and the guess would have failed anyway, because the service takes
///         a <c>Func&lt;AppConfig&gt;</c> and no type-directed bag can supply one.
///     </para>
///     <para>
///         It deliberately does NOT build the app's DI graph.
///         <c>App.ConfigureServices</c> would construct the audio engine, the psarc
///         index and everything else a tone eval has no business starting; the
///         service needs exactly a settings source, a model factory and a key.
///     </para>
/// </summary>
public static class ToneDesignServiceLocator
{
    /// <summary>
    ///     Constructs the live service. The settings come from
    ///     <paramref name="environment" /> through a throwaway
    ///     <see cref="AppConfig" />, so an eval run can never read — or write — the
    ///     owner's real configured provider, and the key never touches
    ///     <c>AiSecretStore</c>'s DPAPI store.
    /// </summary>
    /// <param name="environment">The resolved env vars.</param>
    /// <param name="anchors">Grounding stage 2. **Null unless
    /// <see cref="ToneEvalEnvironment.UseAnchor" /> is set**, and the settings say
    /// so as well, so an anchored run needs BOTH halves — one of them alone leaves
    /// the run unanchored rather than half-anchored, which nothing could
    /// interpret.</param>
    /// <param name="corpus">The corpus, used ONLY by the transcript provider, to
    /// name its files by CORPUS ORDER instead of call order. Ignored otherwise;
    /// null leaves the transcript naming every file by content hash.</param>
    public static IToneDesignService Create(
        ToneEvalEnvironment environment, IToneAnchorLibrary? anchors = null,
        IReadOnlyList<ToneEvalEntry>? corpus = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var config = new AppConfig { Ai = environment.Settings };

        // The ONE thing the transcript provider changes is which factory the REAL
        // service is handed. Everything past this line — the service, the prompt
        // builder, grounding, the validator, the preparation pipeline, the scorer —
        // is the shipping code in both cases.
        IToneDesignModelFactory factory = environment.TranscriptDir is { } dir
            ? new TranscriptToneDesignModelFactory(
                dir,
                corpus is null ? ToneTranscriptIndex.Empty : ToneTranscriptIndex.FromCorpus(corpus))
            : new ToneDesignModelFactory(new EnvironmentSecretStore(environment.ApiKey));

        return new ToneDesignService(
            () => config, factory, FakeLocalization.English(), null, null,
            environment.UseAnchor ? anchors : null);
    }

    /// <summary>
    ///     <see cref="Create" /> plus the one check worth making before spending
    ///     money: the service agrees it is configured. False means the env vars
    ///     produced settings the shipping service will not act on, which is a SKIP —
    ///     there is nothing to measure — rather than a failure.
    /// </summary>
    public static bool TryCreate(
        ToneEvalEnvironment environment, out IToneDesignService? service, out string reason)
        => TryCreate(environment, null, out service, out reason);

    /// <inheritdoc cref="TryCreate(ToneEvalEnvironment, out IToneDesignService, out string)" />
    /// <param name="environment">The resolved env vars.</param>
    /// <param name="anchors">Grounding stage 2, used only in anchored mode.</param>
    /// <param name="service">The live service.</param>
    /// <param name="reason">Why not, when false.</param>
    /// <param name="corpus">The corpus, for the transcript provider's file naming.</param>
    public static bool TryCreate(
        ToneEvalEnvironment environment, IToneAnchorLibrary? anchors,
        out IToneDesignService? service, out string reason,
        IReadOnlyList<ToneEvalEntry>? corpus = null)
    {
        service = Create(environment, anchors, corpus);
        if (service.IsConfigured)
        {
            reason = "";
            return true;
        }

        service = null;
        reason =
            $"{nameof(IToneDesignService)}.IsConfigured is false for " +
            $"{environment.Provider}/{environment.ModelId} — the environment produced " +
            "settings the shipping service will not act on.";
        return false;
    }

    /// <summary>An <see cref="IAiSecretStore" /> over the key the live eval was
    /// given. In memory, never persisted, never logged — and <c>Write</c> is a
    /// deliberate no-op so an eval cannot clobber the owner's stored key.</summary>
    private sealed class EnvironmentSecretStore : IAiSecretStore
    {
        private readonly string? _key;

        public EnvironmentSecretStore(string? key) => _key = key;

        /// <inheritdoc />
        public string? Read(AiProviderKind kind) => _key;

        /// <inheritdoc />
        public void Write(AiProviderKind kind, string? secret)
        {
            // The eval never persists a key.
        }

        /// <inheritdoc />
        /// <remarks>False, and honestly so: the key lives in this object for the
        /// run and nothing writes it anywhere.</remarks>
        public bool IsPersistent => false;
    }
}
