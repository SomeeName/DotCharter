using DotCharter.Services.Plugins;
using DotCharter.Services.Update;
using DotCharter.UI;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI;

/// <summary>
///     The app's update service reads the feed a loaded plugin registered, and the public repo
///     otherwise (#925): the composition root, not a test double, is what is checked. The
///     feed rule itself is <c>Services/Update/UpdateFeedTests</c>.
/// </summary>
public sealed class UpdateFeedCompositionTests
{
    private static bool PluginIsInThisBuild => PluginLoader.Declared(typeof(App).Assembly).Count > 0;

    private static IUpdateFeed ResolvedFeed(bool plugins, IUpdateFeed? extra = null)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services, new JsonConfigurationService(), plugins);
        if (extra != null) services.AddSingleton(extra);
        // Building the update service resolves the logger; keep it off the real log folder.
        services.AddSingleton<IAppLogger>(new FakeAppLogger());
        using var provider = services.BuildServiceProvider();
        var service = Assert.IsType<GitHubUpdateService>(provider.GetRequiredService<IAppUpdateService>());
        Assert.Equal("1.1.1", service.CurrentVersionText);
        return service.Feed;
    }

    [Fact]
    public void Without_a_plugin_the_app_reads_the_public_repo()
    {
        var feed = ResolvedFeed(plugins: false);

        Assert.Equal(UpdateFeed.Public, feed);
        Assert.False(feed.RequiresSignIn);
    }

    [Fact]
    public void A_feed_a_plugin_registered_is_the_one_the_app_reads()
    {
        var pluginFeed = new UpdateFeed("example-owner", "example-plugin", RequiresSignIn: true);

        Assert.Equal(pluginFeed, ResolvedFeed(plugins: false, pluginFeed));
    }

    [Fact]
    public void The_build_plugin_supplies_its_own_feed_exactly_when_this_build_has_one()
    {
        var feed = ResolvedFeed(plugins: true);

        // A public build (-Public, a public clone) reads the public repo; a build carrying the
        // plugin reads the plugin's own feed, which needs the sign-in. Its coordinates are the
        // plugin's business: here only that they are not the public repo's.
        Assert.Equal(PluginIsInThisBuild, feed.RequiresSignIn);
        Assert.Equal(PluginIsInThisBuild, !Equals(new UpdateFeed(feed.Owner, feed.Repo), UpdateFeed.Public));
        // The sign-in's client id comes from the plugin's feed alone; the public build has none.
        Assert.Equal(PluginIsInThisBuild, !string.IsNullOrEmpty(feed.SignInClientId));
    }

    [Fact]
    public void The_update_service_keeps_the_feeds_client_id()
    {
        var pluginFeed = new UpdateFeed("example-owner", "example-plugin", RequiresSignIn: true, SignInClientId: "Iv23.example");

        Assert.Equal("Iv23.example", ResolvedFeed(plugins: false, pluginFeed).SignInClientId);
        Assert.Null(ResolvedFeed(plugins: false).SignInClientId);
    }
}
