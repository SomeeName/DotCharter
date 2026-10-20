namespace DotCharter.Services.Update;

/// <summary>
/// Where the update check looks for releases: a GitHub repo. The public build
///     reads <see cref="UpdateFeed.Public" />. An optional plugin can register its OWN feed
///     (as an instance, from its module's <c>Register</c>), so a build carrying that plugin
///     updates from the plugin's releases; its coordinates live in the plugin alone.
///     <see cref="PluginUpdateGuard" /> still refuses a payload without the plugin.
/// </summary>
public interface IUpdateFeed
{
    /// <summary>The GitHub account or organisation that owns the repo.</summary>
    string Owner { get; }

    /// <summary>The repo's name.</summary>
    string Repo { get; }

    /// <summary>
    ///     Whether the releases are only visible to a signed-in GitHub account (a private
    ///     repo). Such a feed is never asked anonymously: without a token the check answers
    ///     <see cref="UpdateCheckStatus.SignInRequired" />.
    /// </summary>
    bool RequiresSignIn { get; }

    /// <summary>
    ///     The GitHub App / OAuth app client id the browser sign-in (device flow) uses for this
    ///     feed, or null. Only a feed that <see cref="RequiresSignIn" /> has one: the product
    ///     itself carries none, so the public build offers no sign-in at all.
    /// </summary>
    string? SignInClientId { get; }
}

/// <summary>An <see cref="IUpdateFeed" /> value, and the rule that picks the one the app reads.</summary>
public sealed record UpdateFeed(string Owner, string Repo, bool RequiresSignIn = false, string? SignInClientId = null)
    : IUpdateFeed
{
    /// <summary>The public repo, read anonymously: every build without a plugin feed.</summary>
    public static UpdateFeed Public { get; } = new(GitHubUpdateService.DefaultOwner, GitHubUpdateService.DefaultRepo);

    /// <summary>
    ///     The feed the app reads: the first one a plugin registered, else <see cref="Public" />.
    ///     The app itself never registers an <see cref="IUpdateFeed" />, so anything in
    ///     <paramref name="registered" /> came from a loaded plugin.
    /// </summary>
    public static IUpdateFeed Select(IEnumerable<IUpdateFeed> registered) => registered.FirstOrDefault() ?? Public;
}
