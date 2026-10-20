using System.Reflection;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     The four additive contract members (0.55.1): <see cref="IAiSecretStore" />'s
///     <c>IsPersistent</c>, <see cref="IToneDesignModelFactory" />'s
///     <c>Supports</c>, <see cref="IToneDesignService" />'s
///     <c>ConfiguredChanged</c>, and <see cref="ToneDesignResult.Elapsed" />.
///
///     <para>
///         Each of them replaced a place where the UI knew something it was not
///         told — the concrete store type, a hand-kept list of provider kinds, and
///         a settings LOAD on a property a binding reads. The assertions below are
///         written against the interfaces for exactly that reason.
///     </para>
/// </summary>
public sealed class AiContractAdditionsTests
{
    // ---- IAiSecretStore.IsPersistent -----------------------------------------

    [Fact]
    public void TheInMemoryStore_IsNotPersistent_AndTheDpapiStoreIsOnWindows()
    {
        IAiSecretStore memory = new InMemoryAiSecretStore();
        IAiSecretStore dpapi = new DpapiAiSecretStore();

        Assert.False(memory.IsPersistent);
        Assert.Equal(OperatingSystem.IsWindows(), dpapi.IsPersistent);
    }

    // ---- IToneDesignModelFactory.Supports ------------------------------------

    [Theory]
    [InlineData(AiProviderKind.Claude, true)]
    [InlineData(AiProviderKind.OpenAiCompatible, true)]
    [InlineData(AiProviderKind.BuiltIn, false)]
    [InlineData(AiProviderKind.None, false)]
    public void Supports_AnswersForEveryKind(AiProviderKind kind, bool expected)
    {
        var factory = new ToneDesignModelFactory(new InMemoryAiSecretStore());
        Assert.Equal(expected, factory.Supports(kind));
    }

    /// <summary>
    ///     The theories in this file are hand-listed, so a kind added to the enum
    ///     and to nothing else would be covered by none of them and nobody would
    ///     see it. This is the guard that notices.
    ///     <see cref="AiProviderKind.ClaudeCli" /> and <see cref="AiProviderKind.CodexCli" />
    ///     are the two exclusions and they are named, not blanket: each answer
    ///     depends on whether the MACHINE has the binary, so they are covered over
    ///     a stub locator in <c>ClaudeCliToneModelTests</c> and
    ///     <c>CodexCliToneModelTests</c> instead of by a fixed expectation here.
    /// </summary>
    [Fact]
    public void EveryProviderKind_IsCoveredBySupportsOrIsANamedException()
    {
        // Read off the ATTRIBUTE DATA rather than by running the theory: the point
        // is what the [InlineData] rows say, and asking the rows is the only way
        // this can fail for the right reason.
        var covered = typeof(AiContractAdditionsTests)
            .GetMethod(nameof(Supports_AnswersForEveryKind))!
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(InlineDataAttribute))
            .Select(a => (AiProviderKind)(int)
                ((IReadOnlyList<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!)[0].Value!)
            .Append(AiProviderKind.ClaudeCli)
            .Append(AiProviderKind.CodexCli)
            .ToHashSet();

        Assert.Equal(Enum.GetValues<AiProviderKind>().ToHashSet(), covered);
    }

    /// <summary>
    ///     The non-vacuous half: <c>Supports</c> must agree with what
    ///     <see cref="ToneDesignModelFactory.Create" /> actually does. A kind it
    ///     claims is supported, handed COMPLETE settings, must mint a model; a kind
    ///     it disclaims must answer NotConfigured however complete the settings
    ///     are. Without this the two can drift the day the reserved built-in slot
    ///     is filled in, which is the exact day nobody re-reads the UI.
    /// </summary>
    [Theory]
    [InlineData(AiProviderKind.Claude)]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    [InlineData(AiProviderKind.BuiltIn)]
    [InlineData(AiProviderKind.None)]
    public void Supports_AgreesWithCreate(AiProviderKind kind)
    {
        var secrets = new InMemoryAiSecretStore();
        secrets.Write(kind, "sk-test");
        var factory = new ToneDesignModelFactory(secrets);
        var settings = new AiProviderSettings(kind, "a-model-id", "http://localhost:1234");

        if (factory.Supports(kind))
        {
            var model = factory.Create(settings);
            Assert.Equal(kind, model.Kind);
            (model as IDisposable)?.Dispose();
            return;
        }

        var ex = Assert.Throws<ToneDesignException>(() => factory.Create(settings));
        Assert.Equal(ToneDesignFault.NotConfigured, ex.Fault);
    }

    // ---- IToneDesignService.ConfiguredChanged --------------------------------

    [Fact]
    public void NotifyConfigurationChanged_RaisesOnceWhenTheSettingsMoved()
    {
        var config = new AppConfig { Ai = AiProviderSettings.Disabled };
        var service = NewService(() => config);
        var raised = 0;
        service.ConfiguredChanged += (_, _) => raised++;

        // Seeds what the service last published, the way the panel's first read does.
        Assert.False(service.IsConfigured);

        config.Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null);
        service.NotifyConfigurationChanged();

        Assert.Equal(1, raised);
        Assert.True(service.IsConfigured);
    }

    [Fact]
    public void NotifyConfigurationChanged_IsSilentWhenNothingMoved()
    {
        var config = new AppConfig
        {
            Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null)
        };
        var service = NewService(() => config);
        var raised = 0;
        service.ConfiguredChanged += (_, _) => raised++;

        Assert.True(service.IsConfigured);
        service.NotifyConfigurationChanged();
        service.NotifyConfigurationChanged();

        Assert.Equal(0, raised);
    }

    /// <summary>
    ///     Configured→configured still counts. Switching Claude for a local server
    ///     never moves <c>IsConfigured</c>, and a service that only compared the
    ///     boolean would leave a panel describing the model that used to answer.
    /// </summary>
    [Fact]
    public void NotifyConfigurationChanged_RaisesWhenOnlyTheModelChanged()
    {
        var config = new AppConfig
        {
            Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null)
        };
        var service = NewService(() => config);
        var raised = 0;
        service.ConfiguredChanged += (_, _) => raised++;
        Assert.True(service.IsConfigured);

        config.Ai = new AiProviderSettings(AiProviderKind.OpenAiCompatible, "llama-3", "http://localhost:1234");
        service.NotifyConfigurationChanged();

        Assert.Equal(1, raised);
        Assert.True(service.IsConfigured);
    }

    private static ToneDesignService NewService(Func<AppConfig> config)
        => new(config, new UnusedFactory(), FakeLocalization.English());

    private sealed class UnusedFactory : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings)
            => throw new NotSupportedException("these tests never design.");

        public bool Supports(AiProviderKind kind)
            => kind is AiProviderKind.Claude or AiProviderKind.OpenAiCompatible;
    }

    // ---- ToneDesignResult.Elapsed --------------------------------------------

    /// <summary>The default keeps every pre-0.55.1 call site compiling, and it is
    /// <see cref="TimeSpan.Zero" /> — "not measured", which is what the UI checks
    /// before printing a duration.</summary>
    [Fact]
    public void Elapsed_DefaultsToZero_SoOlderCallSitesStillCompile()
    {
        var result = new ToneDesignResult(
            new DspTonePreset("ai-1", "T", DspToneCategory.Lead, "",
                new DcRigPreset([new DcRigModule("amp", new Dictionary<string, float>())]),
                DspToneOrigin.User),
            "", 0.5, [], "{}", new AiUsage("claude-opus-5", 1, 1));

        Assert.Equal(TimeSpan.Zero, result.Elapsed);
    }
}
