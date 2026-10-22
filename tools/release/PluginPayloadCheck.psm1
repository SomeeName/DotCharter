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

# The console tools a plugin ships with its -WithPlugins release: a project below plugins/<name>/
# (its tests and obj/bin folders excluded) that sets <DotCharterReleaseTool>true</DotCharterReleaseTool>.
# Each is published into the app folder, where it shares the app's runtime, and gets a launcher
# script in the install's tools/ folder. The public build ships none of them.
function Get-PluginReleaseTools([string]$RepoRoot) {
    $dir = Join-Path $RepoRoot 'plugins'
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '<DotCharterReleaseTool>\s*true\s*</DotCharterReleaseTool>' } |
        Sort-Object FullName | ForEach-Object {
            $assembly = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
            $text = Get-Content -LiteralPath $_.FullName -Raw
            if ($text -match '<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>') { $assembly = $Matches[1] }
            [pscustomobject]@{ Project = $_.FullName; Name = $assembly; Exe = "$assembly.exe"; Launcher = "$assembly.cmd" }
        })
}

# The tool executables a payload's app folder must hold (-WithPlugins) or must not (public).
function Get-PluginToolExeNames([string]$RepoRoot) {
    return @(Get-PluginReleaseTools $RepoRoot | ForEach-Object { $_.Exe })
}

# Every file a tool puts in a payload that names it: the executable and its launcher.
function Get-PluginToolFileNames([string]$RepoRoot) {
    return @(Get-PluginReleaseTools $RepoRoot | ForEach-Object { $_.Exe; $_.Launcher })
}

# The launcher script for a tool published into app\ (tools\<name>.cmd). Files dropped on it
# arrive as arguments; the tool waits for a key at the end only when Explorer started this script
# (cmd /c), so a run from a shell does not stop. Exit code is the tool's.
function Get-PluginToolLauncherText([string]$Name) {
    $lines = @(
        '@echo off',
        'set "DC_PAUSE="',
        'setlocal EnableDelayedExpansion',
        'echo !cmdcmdline!| findstr /i /c:"/c " >nul',
        'if not errorlevel 1 (endlocal & set "DC_PAUSE=--pause") else endlocal',
        ('"%~dp0..\app\{0}.exe" %DC_PAUSE% %*' -f $Name),
        'exit /b %errorlevel%')
    return ($lines -join "`r`n") + "`r`n"
}

# Publishes each tool self-contained into a scratch folder and copies into $AppDir the files it
# does not already hold: the tool's own executable and metadata. Every assembly the tool shares
# with the app stays the app's own build (a second copy would overwrite the ReadyToRun image).
function Publish-PluginTools([string]$RepoRoot, [string]$AppDir, [string]$Scratch, [string]$Version) {
    foreach ($tool in @(Get-PluginReleaseTools $RepoRoot)) {
        $out = Join-Path $Scratch ('tool-' + $tool.Name)
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
        & dotnet publish $tool.Project -c Release -r win-x64 --self-contained true -o $out "-p:Version=$Version" -p:DotCharterPlugins=true | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($($tool.Name)) failed." }
        if (-not (Test-Path -LiteralPath (Join-Path $out $tool.Exe))) { throw "The $($tool.Name) publish wrote no $($tool.Exe)." }
        foreach ($file in Get-ChildItem -LiteralPath $out -File) {
            $target = Join-Path $AppDir $file.Name
            if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $file.FullName -Destination $target }
        }
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    }
}

# Writes tools\<name>.cmd beside the bundled media tools for every plugin tool.
function Add-PluginToolLaunchers([string]$RepoRoot, [string]$InstallDir) {
    $toolsDir = Join-Path $InstallDir 'tools'
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    foreach ($tool in @(Get-PluginReleaseTools $RepoRoot)) {
        $path = Join-Path $toolsDir $tool.Launcher
        [System.IO.File]::WriteAllText($path, (Get-PluginToolLauncherText $tool.Name), (New-Object System.Text.UTF8Encoding($false)))
    }
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

Export-ModuleMember -Function Get-PluginProjects, Get-PluginAssemblyNames, Get-PluginReleaseTools, Get-PluginToolExeNames,
    Get-PluginToolFileNames, Get-PluginToolLauncherText, Publish-PluginTools, Add-PluginToolLaunchers, Find-PluginsInPayload, Assert-NoPluginInPayload,
    Assert-PluginsInPayload, Assert-ReleasePayloadPlugins, Get-UiPluginPublishArgs, Resolve-ReleaseFeed
