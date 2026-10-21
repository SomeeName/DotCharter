namespace DotCharter.Tone.Ai;

/// <summary>
///     The ONE place "which provider is in force right now, and can this build mint
///     it" is answered.
///     <para>
///         <see cref="ToneDesignService" /> read <c>config().Ai</c> and handed it to
///         <see cref="IToneDesignModelFactory.Create" /> inline; the narrator
///         (<c>Ai/Match/Prior/ToneNarrator.cs</c>) needs exactly the same two steps,
///         and a second copy of them is a second place that can disagree about what
///         "configured" means — which is the shape that makes one AI surface live
///         while another says it is off, for the same settings.
///     </para>
///     <para>
///         It is deliberately tiny and has no state: the settings are read through
///         the caller's <c>Func</c> on EVERY call, never cached, so switching
///         provider under Settings applies to the next request rather than the next
///         launch.
///     </para>
/// </summary>
public static class AiProviderResolution
{
    /// <summary>The provider settings in force, or
    /// <see cref="AiProviderSettings.Disabled" /> when there is no config, no
    /// <c>Ai</c> section, or no source at all. Never throws for an absent config;
    /// an exception OUT of <paramref name="config" /> is the caller's own fault and
    /// propagates.</summary>
    /// <param name="config">Live settings source — invoked per call, never cached.</param>
    public static AiProviderSettings SettingsOf(Func<RigConfig>? config)
        => config?.Invoke()?.Ai ?? AiProviderSettings.Disabled;

    /// <summary>True when a provider is selected, has a model id, and is one this
    /// build can actually mint. The factory check is what keeps a surface from
    /// reporting itself available for <see cref="AiProviderKind.BuiltIn" />, whose
    /// settings can be complete while <see cref="IToneDesignModelFactory.Create" />
    /// still throws <see cref="ToneDesignFault.NotConfigured" />.</summary>
    /// <param name="config">Live settings source.</param>
    /// <param name="factory">The factory that would mint the provider, or null to
    /// test the settings alone.</param>
    public static bool IsConfigured(Func<RigConfig>? config, IToneDesignModelFactory? factory = null)
    {
        var settings = SettingsOf(config);
        return settings.IsEnabled && (factory is null || factory.Supports(settings.Kind));
    }

    /// <summary>Resolves the model for the settings in force, <b>with web research off whatever the
    /// setting says</b>: this is the door the lanes that put project text into a prompt (the Rocksmith
    /// tone generator, the narrator) use, and a model that can fetch pages while it reads a stranger's
    /// song title can be steered into fetching an address that carries the prompt. The chart pass
    /// forces the same. Throws
    /// <see cref="ToneDesignException" /> with
    /// <see cref="ToneDesignFault.NotConfigured" /> — naming the ONE thing that is
    /// missing — exactly as <see cref="IToneDesignModelFactory.Create" /> does. The
    /// caller owns the returned model and must dispose it if it is
    /// <see cref="IDisposable" />: the OpenAI-compatible provider owns an
    /// <c>HttpClient</c> per instance.</summary>
    /// <param name="factory">Mints the provider.</param>
    /// <param name="config">Live settings source.</param>
    /// <param name="settings">The settings the model was minted from.</param>
    public static IToneDesignModel Create(
        IToneDesignModelFactory factory, Func<RigConfig>? config, out AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(factory);
        settings = SettingsOf(config) with { WebResearch = false };
        return factory.Create(settings);
    }

    /// <summary>The provider kind as it appears in a match candidate's
    /// <c>Origin</c> and in a prior's <c>Source</c>: <c>"ai:claude"</c>,
    /// <c>"ai:openaicompatible"</c>, <c>"ai:none"</c>. Lower-case and stable —
    /// it is read by a UI and written into logs, so it is not the enum's
    /// <c>ToString</c> by accident.</summary>
    /// <param name="kind">The provider kind.</param>
    public static string SourceTag(AiProviderKind kind)
        => "ai:" + kind.ToString().ToLowerInvariant();
}
