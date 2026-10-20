# `Update/` — the update feed: which repo the update check reads (#925)

- **`IUpdateFeed`** (`DotCharter.Services/Update/IUpdateFeed.cs`): owner, repo,
  `RequiresSignIn` and `SignInClientId`. `UpdateFeed.Public` is the public repo
  (`GitHubUpdateService.DefaultOwner` / `DefaultRepo`), read anonymously: no sign-in and no
  client id.
- **The product holds NO GitHub client id and shows NO sign-in for the public feed (#49).**
  Settings ▸ Updates' GitHub group (`GitHubSignInViewModel`, `IsAvailable`) exists only when
  the selected feed `RequiresSignIn` — hidden, and absent from the settings search, otherwise.
  The browser sign-in (device flow) uses the feed's `SignInClientId`; a private feed without
  one offers only the pasted token. A plugin's feed supplies its own GitHub App's id
  (`UpdateFeed.SignInClientId`). There is no client-id setting: an old config's
  `GitHubOAuthClientId` key is ignored. `Services/Update/UpdateFeedTests` fails on any
  `Iv1.` / `Iv23` / `Ov23` client-id literal in product source;
  `Presentation/Settings/UpdatesSettingsViewModelTests` pins the visibility and the id used.
- **Model downloads never need the sign-in in the public build.** `LyricModelAssetFetcher`
  and `GitHubReleaseAssetDownloader.For` (stem models, `RequiresSignIn` false) send a token
  only when one is stored (`stem-separation.md`).
- **The app never registers a feed itself.** An optional plugin may register one, as
  an instance, from its module's `Register` (`IPluginModule`); its coordinates live in
  the plugin alone, never in this tree. `UpdateFeed.Select` takes the first registered
  feed, else `UpdateFeed.Public`, and `App.OnlineServices.cs` builds the update service
  over it with `GitHubUpdateService.For`. Pinned by `UI/UpdateFeedCompositionTests`
  (the real composition) and `Services/Update/UpdateFeedTests` (the rule and the
  requests).
- **A feed that `RequiresSignIn` is never asked anonymously.** Without a stored GitHub
  token `CheckForUpdateAsync` sends nothing and answers
  `UpdateCheckStatus.SignInRequired`; `UpdateCheckFlow` turns that into
  `Update_SignInRequired` ("Sign in to GitHub in Settings ▸ Updates…") on the manual
  check, the Help-menu door AND the startup check (a warning on the status line), so a
  private build never looks "up to date" or fails silently. With a token the request
  carries it through the same `GitHubApiRequest` choke point as every other update call.
- **`PluginUpdateGuard` (#893) is independent of the feed**: an install carrying the
  plugin still refuses any payload without it, whichever repo it came from
  (`small-updates.md`).
- The release side is `tools/release/New-Release.ps1 -WithPlugins -Repo <owner/name>`
  (`tools/release/README.md` § Two modes): it builds the plugin payload, refuses one
  without the plugins, and prints the `gh release create` line for that repo. A plugin
  repository wraps it in its own release script, which passes the same coordinates its
  feed uses.


## Version comparison and beta publishing

`GitHubReleaseRules.TryParseVersionFromTag` validates SemVer tags (optional `v`),
retains prerelease identifiers and removes build metadata. `SemanticVersionRules`
compares numeric identifiers numerically, other identifiers by ordinal ASCII, and
stable releases above prereleases with the same core. Malformed tags are refused.
`CurrentVersionText` carries the running app's informational version; `CurrentVersion`
is retained for callers needing a binary version. Both public and plugin feeds use
the same rules. Checksum lookups use the complete release tag, including its suffix.

The check intentionally uses `/releases/latest`. Publish beta tags as **normal
releases marked Latest**, with `--latest` and without the GitHub prerelease flag.
GitHub excludes releases marked prerelease from this endpoint. The release script
prints that command and does not execute it. `Services/Update/PrereleaseVersionTests`
covers precedence, malformed tags, both feeds and beta checksum lookup without network.
