# Public release assets never carry an optional plugin (docs/agents/services/contested-features.md
# section "Optional plugins"). New-Release.ps1 publishes the UI with -p:DotCharterPlugins=false, so
# the git-ignored plugins/ folder is ignored even when something is checked out there;
# Assert-NoPluginInPayload is the backstop that refuses a payload folder (before it is hashed and
# zipped) or a zip (after) that still holds a plugin's assembly. An install that carries a plugin
# refuses a payload WITHOUT it (DotCharter.Services/Update/PluginUpdateGuard), so the two feeds
# never cross.
#
# New-Release.ps1 -WithPlugins is the inverse: the build with every checked-out plugin, and
# Assert-PluginsInPayload refuses a payload that LACKS one of them. Which of the two runs is
# Assert-ReleasePayloadPlugins' one decision; which repo the printed `gh release create` targets
# is Resolve-ReleaseFeed's. A plugin build never goes to the public repo, and it names its repo
# explicitly (-Repo): the product names no other.
#
# A plugin is a folder plugins/<name>/ with its project file at the top; its assembly is named
# after that project file (the same convention DotCharter.UI.csproj declares plugins by).
#
# A module, not a function inside New-Release.ps1, so the gate runs THIS code on fixture
# payloads (DotCharter.Tests/Infrastructure/ReleasePluginRefusalTests). Runs under Windows
# PowerShell 5.1 and pwsh 7.

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Where a release goes when nothing else is asked for: the public repo.
$script:PublicReleaseRepo = 'SomeeName/DotCharter'

# The plugin projects checked out under $RepoRoot: the project files at the top of each
# plugins/<name>/ folder (a plugin's test or tool projects sit deeper and never ship).
function Get-PluginProjects([string]$RepoRoot) {
    $dir = Join-Path $RepoRoot 'plugins'
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Directory | ForEach-Object {
            Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File
        } | Sort-Object FullName | ForEach-Object { $_.FullName })
}

# The assembly file name of every plugin checked out under $RepoRoot.
function Get-PluginAssemblyNames([string]$RepoRoot) {
    return @(Get-PluginProjects $RepoRoot | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) + '.dll' })
}

# Every copy of the named assemblies in a payload: a folder (any file of those names under
# it, at any depth) or a zip (any entry of those names). A path that is neither throws.
function Find-PluginsInPayload([string]$Path, [string[]]$Names) {
    $Names = @($Names | Where-Object { $_ })
    if (Test-Path -LiteralPath $Path -PathType Container) {
        return @(Get-ChildItem -LiteralPath $Path -Recurse -File |
            Where-Object { $n = $_.Name; @($Names | Where-Object { $_ -ieq $n }).Count -gt 0 } | ForEach-Object { $_.FullName })
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "No payload at '$Path' - nothing to check, so nothing may be released."
    }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($zip.Entries | Where-Object { $n = $_.Name; @($Names | Where-Object { $_ -ieq $n }).Count -gt 0 } | ForEach-Object { $_.FullName })
    }
    finally { $zip.Dispose() }
}

# Throws when the payload carries a plugin; silent when it does not.
function Assert-NoPluginInPayload([string]$Path, [string[]]$Names) {
    $found = @(Find-PluginsInPayload $Path $Names)
    if ($found.Count -gt 0) {
        throw ("The public payload carries a plugin ({0}) - refusing to release it. " +
               'Public assets are built with -p:DotCharterPlugins=false; delete stale publish output and rerun.') -f ($found -join ', ')
    }
}

# Throws when the payload LACKS one of the plugins: a -WithPlugins build that came out public
# would strand every install it reaches (PluginUpdateGuard refuses it there).
function Assert-PluginsInPayload([string]$Path, [string[]]$Names) {
    $Names = @($Names | Where-Object { $_ })
    if ($Names.Count -eq 0) {
        throw "-WithPlugins needs at least one plugin checked out under plugins/<name>/ - there is none."
    }
    $found = @(Find-PluginsInPayload $Path $Names | ForEach-Object { Split-Path $_ -Leaf })
    $missing = @($Names | Where-Object { $n = $_; @($found | Where-Object { $_ -ieq $n }).Count -eq 0 })
    if ($missing.Count -gt 0) {
        throw ("The -WithPlugins payload at '{0}' lacks {1} - refusing to release it. " +
               'Do not pass -p:DotCharterPlugins=false to a plugin build.') -f $Path, ($missing -join ', ')
    }
}

# The one decision: a public payload must NOT carry a plugin, a -WithPlugins payload must carry them all.
function Assert-ReleasePayloadPlugins([string]$Path, [string[]]$Names, [switch]$WithPlugins) {
    if ($WithPlugins) { Assert-PluginsInPayload $Path $Names } else { Assert-NoPluginInPayload $Path $Names }
}

# The MSBuild switch for the UI publish: the public build ignores plugins/ even when it is
# there; the -WithPlugins build asks for it explicitly.
function Get-UiPluginPublishArgs([switch]$WithPlugins) {
    if ($WithPlugins) { return @('-p:DotCharterPlugins=true') }
    return @('-p:DotCharterPlugins=false')
}

# Where the printed `gh release create` points, and which checkout's HEAD it tags.
# Without -WithPlugins: -Repo, by default the public repo. With it: -Repo is required and may
# not be the public repo (a plugin build is never published there). -TagCheckout names the
# checkout whose HEAD the release tags (a plugin's own repo has no DotCharter commit);
# by default the DotCharter checkout.
function Resolve-ReleaseFeed([string]$Repo, [switch]$WithPlugins, [string]$TagCheckout, [string]$RepoRoot) {
    if ($WithPlugins) {
        if ([string]::IsNullOrWhiteSpace($Repo)) { throw '-WithPlugins needs -Repo <owner/name>: a plugin build is never released to the public repo.' }
        if ($Repo -ieq $script:PublicReleaseRepo) { throw "-WithPlugins may not release to the public repo ($Repo)." }
    }
    elseif ([string]::IsNullOrWhiteSpace($Repo)) { $Repo = $script:PublicReleaseRepo }
    if ($Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "-Repo must be <owner>/<name>, got '$Repo'." }
    $target = [System.IO.Path]::GetFullPath($(if ([string]::IsNullOrWhiteSpace($TagCheckout)) { $RepoRoot } else { $TagCheckout }))
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "-TagCheckout '$target' is not a folder." }
    $root = [System.IO.Path]::GetFullPath($RepoRoot)
    return [pscustomobject]@{ Repo = $Repo; TargetCheckout = $target; IsOtherCheckout = ($target.TrimEnd('\', '/') -ine $root.TrimEnd('\', '/')) }
}

Export-ModuleMember -Function Get-PluginProjects, Get-PluginAssemblyNames, Find-PluginsInPayload, Assert-NoPluginInPayload,
    Assert-PluginsInPayload, Assert-ReleasePayloadPlugins, Get-UiPluginPublishArgs, Resolve-ReleaseFeed
