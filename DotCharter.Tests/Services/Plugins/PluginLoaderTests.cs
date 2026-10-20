using DotCharter.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Plugins;

/// <summary>
///     The plugin loader: a host that declares no plugin registers nothing, a matching one
///     registers its services, and an outdated or broken one is refused WHOLE and
///     reported — never thrown, so the app always starts.
/// </summary>
public sealed class PluginLoaderTests
{
    private const string Name = "Test.Plugin";

    private static PluginLoadReport LoadWith(ServiceCollection services, Type? type) =>
        PluginLoader.Load(services, [Name], _ => type);

    [Fact]
    public void The_app_loads_every_plugin_its_build_declares_and_nothing_else()
    {
        var services = new ServiceCollection();
        var declared = PluginLoader.Declared(typeof(DotCharter.UI.App).Assembly);

        var report = PluginLoader.Load(services, declared);

        // The test project declares the same plugins as the app (Directory.Build.targets).
        Assert.Equal(declared, PluginLoader.Declared(typeof(PluginLoaderTests).Assembly));
        // A public build (-Public, a public clone) declares none: it registers nothing and shows nothing.
        Assert.Equal(declared.Count, report.Results.Count);
        Assert.All(report.Results, r => Assert.Equal(PluginLoadStatus.Loaded, r.Status));
        Assert.Equal(declared.Count > 0, services.Count > 0);
        Assert.Equal(declared.Count > 0, report.AnyLoaded);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void Every_plugin_built_into_this_output_is_declared()
    {
        // The project at the top of each plugins/<name>/ folder; a public build ignores the
        // folder, so none of them reaches the output and nothing is expected.
        var pluginsRoot = Path.Combine(TestPaths.RepoRoot(), "plugins");
        var built = Directory.Exists(pluginsRoot)
            ? Directory.EnumerateDirectories(pluginsRoot)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj"))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll")))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var declared = PluginLoader.Declared(typeof(DotCharter.UI.App).Assembly).Order(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(built, declared);
    }

    [Fact]
    public void A_declared_assembly_that_is_not_there_is_absent_and_one_without_a_module_fails()
    {
        var services = new ServiceCollection();

        var report = PluginLoader.Load(services, ["No.Such.Plugin.Assembly", typeof(PluginLoaderTests).Assembly.GetName().Name!]);

        Assert.Equal(PluginLoadStatus.Absent, report.Results[0].Status);
        // The test assembly holds several modules (the fakes below), so it is no plugin.
        Assert.Equal(PluginLoadStatus.Failed, report.Results[1].Status);
        Assert.Contains("exactly one", report.Results[1].Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public void Declared_reads_the_hosts_plugin_attributes()
    {
        var declared = PluginLoader.Declared(typeof(PluginLoaderTests).Assembly);

        Assert.Equal([.. declared.Select(n => n + ".dll")], PluginLoader.DeclaredFiles(typeof(PluginLoaderTests).Assembly));
        Assert.Empty(PluginLoader.DeclaredFiles(null));
        // A host without declarations (Services itself) has none.
        Assert.Empty(PluginLoader.Declared(typeof(PluginLoader).Assembly));
    }

    [Fact]
    public void A_matching_module_is_registered()
    {
        var services = new ServiceCollection();

        var report = LoadWith(services, typeof(MatchingModule));

        var result = Assert.Single(report.Results);
        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
        Assert.Equal("matching", result.Id);
        Assert.True(report.AnyLoaded);
        Assert.Empty(report.Problems);
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<IPackageFeatures>().ReadsGamePackages);
    }

    [Fact]
    public void An_outdated_module_is_refused_whole_and_reported()
    {
        var services = new ServiceCollection();

        var report = LoadWith(services, typeof(OutdatedModule));

        var problem = Assert.Single(report.Problems);
        Assert.Equal(PluginLoadStatus.Outdated, problem.Status);
        Assert.Equal("outdated", problem.Id);
        Assert.Equal(PluginApiVersion.Current + 1, problem.ApiVersion);
        Assert.Contains($"{PluginApiVersion.Current + 1}", problem.Message, StringComparison.Ordinal);
        Assert.Contains($"{PluginApiVersion.Current}", problem.Message, StringComparison.Ordinal);
        Assert.Empty(services);
        Assert.Equal(0, OutdatedModule.RegisterCalls);
    }

    [Fact]
    public void A_module_whose_Register_throws_leaves_nothing_half_registered()
    {
        var services = new ServiceCollection();

        var report = LoadWith(services, typeof(ThrowingModule));

        Assert.Equal(PluginLoadStatus.Failed, Assert.Single(report.Problems).Status);
        Assert.Empty(services);
    }

    [Fact]
    public void A_type_that_is_not_a_module_or_cannot_load_is_reported_not_thrown()
    {
        var services = new ServiceCollection();

        Assert.Equal(PluginLoadStatus.Failed, Assert.Single(LoadWith(services, typeof(string)).Results).Status);
        var broken = PluginLoader.Load(services, [Name], _ => throw new BadImageFormatException("bad image"));
        Assert.Equal(PluginLoadStatus.Failed, Assert.Single(broken.Results).Status);
        Assert.Contains("bad image", broken.Results[0].Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public void The_app_starts_without_an_outdated_add_on_and_warns_about_it()
    {
        var services = new ServiceCollection();
        var report = DotCharter.UI.AppPlugins.AddPlugins(services, [Name], _ => typeof(OutdatedModule));
        using var provider = services.BuildServiceProvider();
        var logger = new FakeAppLogger();

        DotCharter.UI.AppPlugins.ReportPlugins(provider, logger, FakeLocalization.KeysOnly());

        Assert.Equal(PluginLoadStatus.Outdated, Assert.Single(report.Problems).Status);
        Assert.Equal(PackageFeatures.None, provider.GetRequiredService<IPackageFeatures>());
        var warning = Assert.Single(logger.Entries, e => e.Level == "WARN");
        Assert.Contains("Plugin_Outdated", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_module_adds_what_it_reads_into_one_merged_registration()
    {
        var neither = new ServiceCollection();
        DotCharter.UI.AppPlugins.AddPlugins(neither, []);
        var both = new ServiceCollection();
        DotCharter.UI.AppPlugins.AddPlugins(both, [Name, Name + "2"],
            name => name == Name ? typeof(PackageReaderModule) : typeof(GameInstallModule));
        var pluginOnly = new ServiceCollection();
        DotCharter.UI.AppPlugins.AddPlugins(pluginOnly, [Name], _ => typeof(GameInstallModule));

        Assert.Equal(PackageFeatures.None, Resolve(neither));
        Assert.Equal(PackageFeatures.All, Resolve(both));
        Assert.Equal(new PackageFeatures(ReadsGamePackages: false, ReadsGameInstall: true), Resolve(pluginOnly));
        // One registration, however many modules said something.
        Assert.Single(both, d => d.ServiceType == typeof(IPackageFeatures));

        static IPackageFeatures Resolve(ServiceCollection services)
        {
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IPackageFeatures>();
        }
    }

    [Fact]
    public void Activate_hands_the_registered_game_install_reader_to_GameInstall_and_never_clears_it()
    {
        var previous = GameInstall.Reader;
        try
        {
            var reader = new FakeReader();
            var with = new ServiceCollection();
            with.AddSingleton<IGameInstallReader>(reader);
            using (var provider = with.BuildServiceProvider()) DotCharter.UI.AppPlugins.Activate(provider);
            Assert.Same(reader, GameInstall.Reader);
            Assert.Equal(["game"], GameInstall.Directories());

            using (var provider = new ServiceCollection().BuildServiceProvider()) DotCharter.UI.AppPlugins.Activate(provider);
            Assert.Same(reader, GameInstall.Reader);
        }
        finally
        {
            GameInstall.Use(previous);
        }
    }

    [Fact]
    public async Task The_test_switch_keeps_the_background_load_from_the_developers_install()
    {
        var reader = new FakeReader();
        var before = GameInstall.BackgroundLoadDisabled;
        try
        {
            GameInstall.BackgroundLoadDisabled = true;
            await GameInstall.StartInBackground(reader, null);
            Assert.Equal(0, reader.Started);

            GameInstall.BackgroundLoadDisabled = false;
            await GameInstall.StartInBackground(reader, null);
            Assert.Equal(1, reader.Started);
        }
        finally
        {
            GameInstall.BackgroundLoadDisabled = before;
        }
    }

    private sealed class FakeReader : IGameInstallReader
    {
        public int Started { get; private set; }

        public IReadOnlyList<string> GameDirectories() => ["game"];

        public Task StartInBackground(IAppLogger? logger)
        {
            Started++;
            return Task.CompletedTask;
        }
    }

    public sealed class PackageReaderModule : IPluginModule
    {
        public string Id => "package-reader";
        public int ApiVersion => PluginApiVersion.Current;
        public void Register(IServiceCollection services) => services.AddSingleton<IPackageFeatures>(new PackageFeatures(ReadsGamePackages: true, ReadsGameInstall: false));
    }

    public sealed class GameInstallModule : IPluginModule
    {
        public string Id => "game-install";
        public int ApiVersion => PluginApiVersion.Current;
        public void Register(IServiceCollection services) => services.AddSingleton<IPackageFeatures>(new PackageFeatures(ReadsGamePackages: false, ReadsGameInstall: true));
    }

    public sealed class MatchingModule : IPluginModule
    {
        public string Id => "matching";
        public int ApiVersion => PluginApiVersion.Current;
        public void Register(IServiceCollection services) => services.AddSingleton<IPackageFeatures>(PackageFeatures.All);
    }

    public sealed class OutdatedModule : IPluginModule
    {
        public static int RegisterCalls { get; private set; }
        public string Id => "outdated";
        public int ApiVersion => PluginApiVersion.Current + 1;
        public void Register(IServiceCollection services) => RegisterCalls++;
    }

    public sealed class ThrowingModule : IPluginModule
    {
        public string Id => "throwing";
        public int ApiVersion => PluginApiVersion.Current;

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IPackageFeatures>(PackageFeatures.All);
            throw new InvalidOperationException("half way");
        }
    }
}
