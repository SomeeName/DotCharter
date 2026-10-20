using System.Reflection;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio;
using DotCharter.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The focused seams of <see cref="IAudioPlaybackService" />
///     (docs/agents/services/audio-engine.md, "Focused interfaces for view
///     models"): every playback implementation serves all of them, and every
///     seam the composition root registers is the SAME object as the service —
///     a seam that resolved to a second instance would be a second engine, or a
///     view model driving a transport nobody hears.
/// </summary>
public sealed class AudioSeamRegistrationTests
{
    private static readonly Type[] Seams =
    [
        typeof(IPlaybackTransport),
        typeof(IAudioDeviceControl),
        typeof(IStemMixer),
        typeof(IVstChainHost),
        typeof(ILiveInputCapture)
    ];

    /// <summary>The seams with a consumer today. <c>IVstChainHost</c> is
    /// deliberately absent: its only caller is the editor's
    /// <c>TransportViewModel</c>, which needs the whole service.</summary>
    private static readonly Type[] RegisteredSeams =
    [
        typeof(IPlaybackTransport),
        typeof(IAudioDeviceControl),
        typeof(IStemMixer),
        typeof(ILiveInputCapture)
    ];

    [Fact]
    public void The_service_is_exactly_the_seams_plus_disposal()
    {
        // Pins the shape the owner approves: a seam added, dropped or re-parented
        // shows up here rather than in a view model's constructor.
        var direct = typeof(IAudioPlaybackService).GetInterfaces()
            .Except(typeof(IAudioPlaybackService).GetInterfaces().SelectMany(i => i.GetInterfaces()))
            .OrderBy(t => t.Name)
            .ToArray();

        Assert.Equal(Seams.Append(typeof(IDisposable)).OrderBy(t => t.Name), direct);
    }

    [Fact]
    public void Every_playback_implementation_implements_every_seam_member()
    {
        var implementers = new[]
            {
                typeof(JuceAudioPlaybackService).Assembly,
                typeof(App).Assembly,
                typeof(AudioSeamRegistrationTests).Assembly
            }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAudioPlaybackService).IsAssignableFrom(t))
            .ToArray();

        // Not vacuous: the three implementations the app and its tests run on.
        Assert.Contains(typeof(JuceAudioPlaybackService), implementers);
        Assert.Contains(typeof(NullAudioPlaybackService), implementers);
        Assert.Contains(typeof(FakeAudioPlaybackService), implementers);

        var missing = (
            from impl in implementers
            from seam in Seams
            let map = impl.GetInterfaceMap(seam)
            from i in Enumerable.Range(0, map.InterfaceMethods.Length)
            let target = map.TargetMethods[i]
            where target is null || target.IsAbstract
            select $"{impl.Name}: {seam.Name}.{map.InterfaceMethods[i].Name}").ToArray();

        Assert.True(missing.Length == 0, "Unimplemented seam members: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_registered_seams_are_the_ones_with_a_consumer()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());

        var registered = services.Select(d => d.ServiceType).Where(Seams.Contains).OrderBy(t => t.Name);

        Assert.Equal(RegisteredSeams.OrderBy(t => t.Name), registered);
    }

    [Fact]
    public void Every_registered_seam_resolves_to_the_one_playback_service()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService());
        // The real factory would open a JUCE engine; the forwarding registrations
        // under test are left exactly as the composition root wrote them.
        var audio = new FakeAudioPlaybackService();
        services.Replace(ServiceDescriptor.Singleton<IAudioPlaybackService>(_ => audio));

        using var provider = services.BuildServiceProvider();

        Assert.Same(audio, provider.GetRequiredService<IAudioPlaybackService>());
        foreach (var seam in RegisteredSeams)
        {
            Assert.True(ReferenceEquals(audio, provider.GetRequiredService(seam)),
                $"{seam.Name} resolved to a different object than IAudioPlaybackService.");
        }
    }

    [Fact]
    public void The_seams_live_in_services_so_presentation_can_name_them()
    {
        // BannedSymbols.Presentation.txt bans the service and the engine class
        // for the Presentation projects; the seams must stay out of that list.
        var banned = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "BannedSymbols.Presentation.txt"));

        Assert.Contains("T:DotCharter.Tone.Audio.Abstractions.IAudioPlaybackService;", banned);
        Assert.Contains("T:DotCharter.Tone.Audio.JuceAudioPlaybackService;", banned);
        foreach (var seam in Seams)
        {
            Assert.Equal(typeof(IAudioPlaybackService).Assembly, seam.Assembly);
            Assert.DoesNotContain(seam.FullName!, banned);
        }
    }

    /// <summary>
    ///     The banned-API analyzer does NOT see a field, property or parameter
    ///     TYPED as the whole service, and it does not see a call to a member the
    ///     service inherits from a seam (measured 2026-09-19 with a probe in
    ///     DotCharter.Presentation: <c>_audio.Volume</c> and <c>typeof</c> raised
    ///     RS0030; the field, the constructor parameter and <c>_audio.Play()</c>
    ///     compiled clean). Before the split every member was declared on the
    ///     service itself, so holding one was useless; now it would be a working
    ///     back door, and this closes it by reflection.
    /// </summary>
    [Fact]
    public void No_presentation_type_holds_the_whole_playback_service()
    {
        var types = typeof(IStartScreen).Assembly.GetTypes()
            .Concat(typeof(FakeCountdownScreen).Assembly.GetTypes());

        var offenders = types.SelectMany(WholeServiceMentions).ToArray();

        Assert.True(offenders.Length == 0,
            "Presentation must take a focused seam, not the whole playback service: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Probe_the_whole_service_rule_finds_a_field_a_parameter_and_a_generic_argument()
    {
        var found = WholeServiceMentions(typeof(HoldsTheWholeService)).ToArray();

        Assert.Contains(found, f => f.Contains("field _audio"));
        Assert.Contains(found, f => f.Contains("parameter audio"));
        Assert.Contains(found, f => f.Contains("property Engines"));
        Assert.Empty(WholeServiceMentions(typeof(HoldsOnlyASeam)));
    }

    private static readonly Type[] WholeService = [typeof(IAudioPlaybackService), typeof(JuceAudioPlaybackService)];

    private static IEnumerable<string> WholeServiceMentions(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var f in type.GetFields(all).Where(f => Names(f.FieldType)))
            yield return $"{type.Name} field {f.Name}";
        foreach (var p in type.GetProperties(all).Where(p => Names(p.PropertyType)))
            yield return $"{type.Name} property {p.Name}";
        foreach (var m in type.GetConstructors(all).Cast<MethodBase>().Concat(type.GetMethods(all)))
        {
            foreach (var p in m.GetParameters().Where(p => Names(p.ParameterType)))
                yield return $"{type.Name}.{m.Name} parameter {p.Name}";
            if (m is MethodInfo mi && Names(mi.ReturnType))
                yield return $"{type.Name}.{m.Name} return";
        }
    }

    private static bool Names(Type t)
    {
        if (t.HasElementType) return Names(t.GetElementType()!);
        if (WholeService.Contains(t)) return true;
        return t.IsGenericType && t.GetGenericArguments().Any(Names);
    }

    private sealed class HoldsTheWholeService(IAudioPlaybackService audio)
    {
        private readonly IAudioPlaybackService _audio = audio;
        public IReadOnlyList<IAudioPlaybackService> Engines => [_audio];
    }

    private sealed class HoldsOnlyASeam(IPlaybackTransport transport)
    {
        public bool IsPlaying => transport.IsPlaying;
    }
}
