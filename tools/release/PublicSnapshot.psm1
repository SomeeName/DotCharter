# The public export: dev `main` becomes ONE snapshot commit per release on the
# public DotCharter and JuceSharp repos. Their history is linear - each snapshot's parent
# is the previous public snapshot (a root commit the first time) - and no dev commit id,
# trailer or private repo name reaches them. Export-PublicSnapshot.ps1 drives it; the
# leak check also runs alone through Test-PublicLeaks.ps1.
#
# Everything is git plumbing in scratch repositories that borrow the dev object stores
# through objects/info/alternates (read-only): the dev checkout's working tree, index,
# branches and refs are never written. Commit dates are the dev commits' own dates, so
# the same inputs give the same snapshot ids on a re-run.
#
# Every private name below is assembled from pieces, so this file neither matches its own
# leak check nor is rewritten by its own rename map when it is exported.
#
# Tested by DotCharter.Tests/Infrastructure/PublicSnapshotExportTests (pwsh, fixture repos).
# Requires PowerShell 7.

Set-StrictMode -Version Latest

$script:DevSuffix = '-' + 'Dev'
$script:Owner = 'SomeeName'
$script:AddOnName = 'DotCharter' + '.Rock' + 'smith'
$script:PendingHash = 'PENDING: pin with tools/release/Publish-NativeRuntime.ps1 (or re-run Export-PublicSnapshot.ps1 -NativeDir)'

# --- the rename map ----------------------------------------------------------

# The dev repos are renamed back to the public names. Ordered: the owner-qualified forms
# first (documentation), then the bare names, which also cover anything the first missed.
# Case-sensitive on purpose; a different casing is left for the leak check to report.
function Get-PublicRenameMap {
    $map = foreach ($name in 'DotCharter', 'JuceSharp') {
        [pscustomobject]@{ From = "$script:Owner/$name$script:DevSuffix"; To = "$script:Owner/$name" }
    }
    $map += foreach ($name in 'DotCharter', 'JuceSharp') {
        [pscustomobject]@{ From = "$name$script:DevSuffix"; To = $name }
    }
    return $map
}

# Applies the rename map. A name followed by a word character ("...-Development",
# "jucesharp-devrate") is another word and stays.
function Convert-PublicText {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    foreach ($rename in Get-PublicRenameMap) {
        $Text = [regex]::Replace($Text, [regex]::Escape($rename.From) + '(?![A-Za-z0-9_])', $rename.To.Replace('$', '$$'))
    }
    return $Text
}

# --- paths that never ship ------------------------------------------------------

# Every entry is git-ignored in dev today, so a dev tree holds none of them; they are
# removed again here because the export must not depend on .gitignore staying right.
function Get-PublicExcludedPaths {
    @(
        [pscustomobject]@{ Path = 'plugins/'; Reason = 'plugin checkouts (git-ignored; never public)' }
        [pscustomobject]@{ Path = 'add' + 'ons/'; Reason = 'the old plugin folder name, in case a stale checkout is still there' }
        [pscustomobject]@{ Path = '.claude/worktrees/'; Reason = 'agent worktrees: whole embedded checkouts' }
        [pscustomobject]@{ Path = '.claude/settings.local.json'; Reason = 'personal Claude Code permissions' }
        [pscustomobject]@{ Path = 'TestData/'; Reason = "junction to the owner's local corpus" }
        [pscustomobject]@{ Path = 'artifacts/'; Reason = 'build and release output' }
        [pscustomobject]@{ Path = 'DotCharter.Tests/LocalOnly_'; Reason = "owner-only probes that read the owner's own files" }
    )
}

function Test-PublicExcludedPath {
    param([Parameter(Mandatory)][string]$Path)
    $normal = $Path.Replace('\', '/')
    foreach ($excluded in Get-PublicExcludedPaths) {
        if ($normal.StartsWith($excluded.Path, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# --- the leak check -------------------------------------------------------------

# The patterns of DotCharter.Tests/Infrastructure/PersonalDataLintTests, in its order and
# with its options. PublicSnapshotExportTests fails when the two lists differ.
function Get-PersonalDataPatterns {
    function Escape([string]$s) { [regex]::Escape($s) }
    @(
        [pscustomobject]@{ Pattern = 'lu' + 'kas'; IgnoreCase = $true }
        [pscustomobject]@{ Pattern = 'stad' + 'ler'; IgnoreCase = $true }
        [pscustomobject]@{ Pattern = '\b' + 'Be' + 'la' + '\b'; IgnoreCase = $false }
        [pscustomobject]@{ Pattern = (Escape ('luk.' + 'stad' + 'ler' + '@' + 'outlook.com')); IgnoreCase = $true }
        [pscustomobject]@{ Pattern = (Escape ('somee.' + 'name1' + '@' + 'gmail.com')); IgnoreCase = $true }
        [pscustomobject]@{ Pattern = (Escape ('beci.' + 'music' + '@' + 'outlook.com')); IgnoreCase = $true }
        [pscustomobject]@{ Pattern = '\b' + 'F' + ':[\\/]+' + 'Rock' + 'smith'; IgnoreCase = $true }
        [pscustomobject]@{ Pattern = '\b' + 'D' + ':[\\/]+' + 'Sort' + 'ed'; IgnoreCase = $true }
    )
}

# The user-folder names the tree uses as made-up examples (sample data, test inputs, the
# redactor's own docs). Exactly the ones in use; any other name under the profile folder is
# a finding. Matched whole: "you" passes, "youngster" does not. As regex fragments: the
# umlaut and the ellipsis are escapes so this file stays ASCII, and the JSON-escaped
# spelling of the umlaut name is one of the inputs.
function Get-UserPathPlaceholders {
    @('you', 'sie', 'me', 'u', 'player', 'someone', 'alice', 'Alex', 'Test', 'A B', 'NAME',
        'Müller', 'M\\+u00fcller', '<name>', '&lt;name&gt;', '<user>', '…')
}

function Get-UserPathPlaceholderLookahead {
    $names = (Get-UserPathPlaceholders | ForEach-Object { if ($_ -match '\\') { $_ } else { [regex]::Escape($_) } }) -join '|'
    return '(?!(?:' + $names + ')(?![\p{L}\p{N}_]))'
}

# One rule per kind of leak. Key material is gitleaks' job (.gitleaks.toml), which
# Export-PublicSnapshot.ps1 runs on the snapshot (-Push refuses without it).
function Get-PublicLeakRules {
    $ci = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    $cs = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    $rules = @(
        [pscustomobject]@{ Name = 'dev-repo'; Regex = [regex]::new('\b(?:DotCharter|JuceSharp)' + $script:DevSuffix + '(?![A-Za-z0-9_])', $ci) }
        [pscustomobject]@{ Name = 'archive-repo'; Regex = [regex]::new('\b(?:DotCharter|JuceSharp)-' + 'Archive(?![A-Za-z0-9_])', $ci) }
        [pscustomobject]@{ Name = 'addon-name'; Regex = [regex]::new([regex]::Escape($script:AddOnName), $ci) }
        [pscustomobject]@{ Name = 'addons-path'; Regex = [regex]::new('(?<![A-Za-z0-9_.-])' + 'add' + 'ons[\\/]', $ci) }
        # A dev-repo pull request, issue or commit link: the public repos start from one root
        # commit, so every such link is dead there (the numbers name the dev history).
        [pscustomobject]@{ Name = 'history-link'; Regex = [regex]::new('github\.com/' + 'SomeeName/(?:DotCharter|JuceSharp)(?:' + $script:DevSuffix + ')?/(?:' + 'pull|issues|commit)/', $ci) }
        [pscustomobject]@{ Name = 'user-profile-path'; Regex = [regex]::new('\b' + 'C' + ':[\\/]+' + 'Users(?>[\\/]+)' + (Get-UserPathPlaceholderLookahead), $ci) }
    )
    $rules += foreach ($p in Get-PersonalDataPatterns) {
        [pscustomobject]@{ Name = 'personal-data'; Regex = [regex]::new($p.Pattern, $(if ($p.IgnoreCase) { $ci } else { $cs })) }
    }
    return $rules
}

# Findings in one text: Rule, Path, Line (1-based), Text (the trimmed line).
function Find-PublicLeaksInText {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $lines = $null
    foreach ($rule in Get-PublicLeakRules) {
        foreach ($m in $rule.Regex.Matches($Text)) {
            if ($null -eq $lines) { $lines = $Text.Split("`n") }
            $line = 1
            for ($i = $Text.IndexOf("`n"); $i -ge 0 -and $i -lt $m.Index; $i = $Text.IndexOf("`n", $i + 1)) { $line++ }
            $content = $lines[$line - 1].Trim()
            if ($content.Length -gt 160) { $content = $content.Substring(0, 160) + '...' }
            [pscustomobject]@{ Rule = $rule.Name; Path = $Path; Line = $line; Text = $content }
        }
    }
}

# Findings in a file's own path (a file in the add-on folder, or named after the add-on). Line 0.
function Find-PublicLeaksInPath {
    param([Parameter(Mandatory)][string]$Path)
    foreach ($rule in Get-PublicLeakRules) {
        if ($rule.Regex.IsMatch($Path)) { [pscustomobject]@{ Rule = $rule.Name; Path = $Path; Line = 0; Text = '(the path itself)' } }
    }
}

function Test-BinaryBytes([byte[]]$Bytes) {
    $n = [Math]::Min($Bytes.Length, 8192)
    return [Array]::IndexOf($Bytes, [byte]0, 0, $n) -ge 0
}

# The text the rules read from $Bytes: UTF-8 for a text file; for a file with a NUL in its
# first 8 KB (PersonalDataLintTests' "binary"), Latin-1 - one char per byte, so every ASCII
# name in it is still found (a PDB path inside a DLL, a source file with a literal NUL).
function ConvertTo-LeakScanText {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    if (Test-BinaryBytes $Bytes) { return [System.Text.Encoding]::Latin1.GetString($Bytes) }
    return [System.Text.Encoding]::UTF8.GetString($Bytes)
}

# The decompressed bytes of a gzip file (magic 1F 8B), in memory; $null for anything else,
# or a gzip stream that does not decompress.
function Expand-LeakScanGzip {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    if ($Bytes.Length -lt 18 -or $Bytes[0] -ne 0x1f -or $Bytes[1] -ne 0x8b) { return $null }
    $in = [System.IO.MemoryStream]::new($Bytes)
    $out = [System.IO.MemoryStream]::new()
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($in, [System.IO.Compression.CompressionMode]::Decompress)
        try { $gzip.CopyTo($out) } finally { $gzip.Dispose() }
        return , $out.ToArray()
    }
    catch [System.IO.InvalidDataException] { return $null }
    finally { $in.Dispose(); $out.Dispose() }
}

# Every finding under $Root (a materialised tree; .git folders are skipped). Paths are
# relative with forward slashes. Nothing is skipped by content: a file with a NUL is read
# as Latin-1 (ConvertTo-LeakScanText), and a gzip file is read both as stored (its header
# can carry a file name) and decompressed in memory; a finding inside reports the .gz path
# and the line of the decompressed text. Screenshot PIXELS are not read: see the manual
# check in tools/release/README.md.
function Find-PublicTreeLeaks {
    param([Parameter(Mandatory)][string]$Root)
    $Root = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
    $files = [System.IO.Directory]::EnumerateFiles($Root, '*', [System.IO.SearchOption]::AllDirectories)
    foreach ($file in $files) {
        $relative = $file.Substring($Root.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)\.git(/|$)') { continue }
        Find-PublicLeaksInPath -Path $relative
        $bytes = [System.IO.File]::ReadAllBytes($file)
        Find-PublicLeaksInText -Path $relative -Text (ConvertTo-LeakScanText $bytes)
        $expanded = Expand-LeakScanGzip $bytes
        if ($null -ne $expanded) { Find-PublicLeaksInText -Path $relative -Text (ConvertTo-LeakScanText $expanded) }
    }
}

# The report: a count per rule, then file:line per finding, grouped by rule.
function Format-PublicLeakReport {
    param([AllowEmptyCollection()][object[]]$Findings = @(), [int]$PerRule = 0)
    $Findings = @($Findings)
    if ($Findings.Count -eq 0) { return 'leak check: clean (0 findings)' }
    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add("leak check: $($Findings.Count) finding(s) in $(@($Findings.Path | Sort-Object -Unique).Count) file(s)")
    foreach ($group in $Findings | Group-Object Rule | Sort-Object Name) {
        $out.Add("  [$($group.Name)] $($group.Count) finding(s) in $(@($group.Group.Path | Sort-Object -Unique).Count) file(s)")
        $shown = if ($PerRule -gt 0) { $group.Group | Select-Object -First $PerRule } else { $group.Group }
        foreach ($f in $shown) { $out.Add("    $($f.Path):$($f.Line): $($f.Text)") }
        if ($PerRule -gt 0 -and $group.Count -gt $PerRule) { $out.Add("    ... $($group.Count - $PerRule) more") }
    }
    return ($out -join "`n")
}

# --- native-runtime.json ----------------------------------------------------------

function Get-NativeRuntimePendingHash { $script:PendingHash }

# Adds the pin for the PUBLIC JuceSharp commit. With $Files (name -> SHA-256 of a pair
# built from that tree) the pin is real; without, each hash is the PENDING marker, which
# NativeRuntime.psm1 refuses to install and NativeRuntimeFetchTests fails on, so the
# snapshot cannot pass its gate until the publish step has pinned the pair.
function Add-PublicNativeRuntimeEntry {
    param(
        [Parameter(Mandatory)][string]$Json,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][int]$Abi,
        [System.Collections.IDictionary]$Files
    )
    $manifest = $Json | ConvertFrom-Json
    if (@($manifest.runtimes | Where-Object { $_.commit -eq $Commit }).Count -gt 0) { return $Json }
    $pins = [ordered]@{}
    foreach ($name in 'juce_sharp.dll', 'juce_vst_host.exe') {
        $pins[$name] = if ($Files) { [string]$Files[$name] } else { $script:PendingHash }
    }
    $entry = [pscustomobject][ordered]@{ commit = $Commit; abi = $Abi; files = [pscustomobject]$pins }
    $manifest.runtimes = @($manifest.runtimes) + $entry
    return ($manifest | ConvertTo-Json -Depth 5) + "`n"
}

# The dev pin's hashes for $DevCommit, reusable for the public JuceSharp commit ONLY when
# the two trees are byte-identical (same tree id): then the pair built from one is the
# pair of the other. $null otherwise, or when the dev pin is missing or not real hashes.
function Get-InheritedNativePins {
    param(
        [Parameter(Mandatory)][string]$Json,
        [Parameter(Mandatory)][string]$DevCommit,
        [Parameter(Mandatory)][string]$DevTree,
        [Parameter(Mandatory)][string]$PublicTree
    )
    if ($DevTree -ne $PublicTree) { return $null }
    $entry = @(($Json | ConvertFrom-Json).runtimes | Where-Object { $_.commit -eq $DevCommit }) | Select-Object -First 1
    if (-not $entry) { return $null }
    $pins = [ordered]@{}
    foreach ($name in 'juce_sharp.dll', 'juce_vst_host.exe') {
        $hash = "$($entry.files.$name)"
        if ($hash -cnotmatch '^[0-9a-f]{64}$') { return $null }
        $pins[$name] = $hash
    }
    return $pins
}

function Get-MsBuildVersion {
    param([Parameter(Mandatory)][string]$Text)
    $m = [regex]::Match($Text, '(?m)^\s*<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw 'No <Version> element.' }
    return $m.Groups[1].Value.Trim()
}

# --- git plumbing -------------------------------------------------------------------

# Runs git with an exact argument list. -InputBytes feeds stdin; -AsBytes returns stdout
# raw (blob contents); otherwise stdout is UTF-8 text, trimmed. Throws on a non-zero exit
# unless -AllowExitCodes lists it.
function Invoke-SnapshotGit {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [hashtable]$Environment = @{},
        [byte[]]$InputBytes,
        [switch]$AsBytes,
        [int[]]$AllowExitCodes = @()
    )
    $info = [System.Diagnostics.ProcessStartInfo]::new('git')
    foreach ($a in $Arguments) { $info.ArgumentList.Add($a) }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $null -ne $InputBytes
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = [string]$Environment[$key] }
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $stderr = $process.StandardError.ReadToEndAsync()
        $buffer = [System.IO.MemoryStream]::new()
        $copy = $process.StandardOutput.BaseStream.CopyToAsync($buffer)
        if ($null -ne $InputBytes) {
            $process.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length)
            $process.StandardInput.Close()
        }
        $copy.Wait()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -and $AllowExitCodes -notcontains $process.ExitCode) {
            throw "git $($Arguments -join ' ') failed ($($process.ExitCode)): $($stderr.Result.Trim())"
        }
        if ($AsBytes) { return , $buffer.ToArray() }
        return [System.Text.Encoding]::UTF8.GetString($buffer.ToArray()).Trim()
    }
    finally { $process.Dispose() }
}

# A scratch repository that reads the dev objects through alternates and writes only its
# own. core.autocrlf off: what is checked out is byte-for-byte the snapshot.
function New-SnapshotScratchRepo {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$AlternateObjectDirs, [switch]$Bare)
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $init = @('init', '-q', '-b', 'main')
    if ($Bare) { $init += '--bare' }
    Invoke-SnapshotGit -Arguments ($init + $Path) | Out-Null
    $gitDir = Invoke-SnapshotGit -Arguments @('-C', $Path, 'rev-parse', '--path-format=absolute', '--git-dir')
    $alternates = Join-Path $gitDir 'objects/info/alternates'
    [System.IO.File]::WriteAllText($alternates, (($AlternateObjectDirs | ForEach-Object { $_.Replace('\', '/') }) -join "`n") + "`n")
    Invoke-SnapshotGit -Arguments @('-C', $Path, 'config', 'core.autocrlf', 'false') | Out-Null
    return $gitDir
}

# The previous public snapshot: the tip of `main` at $Url (a URL or a local repo path),
# fetched into $Repo - fetch only, nothing is pushed. $null when no URL is given, or
# when the repo has no `main` yet (a freshly created, empty public repo).
function Get-PreviousSnapshot {
    param([Parameter(Mandatory)][string]$Repo, [string]$Url)
    if (-not $Url) { return $null }
    if (-not (Invoke-SnapshotGit -Arguments @('ls-remote', '--heads', $Url, 'main'))) { return $null }
    # Never into submodules: the kept work tree's JuceSharp is a plain checkout, and an
    # on-demand recursion into it fails the fetch ("Could not access submodule").
    Invoke-SnapshotGit -Arguments @('-C', $Repo, '-c', 'fetch.recurseSubmodules=false', 'fetch', '-q', '--no-tags', '--no-recurse-submodules',
        $Url, '+refs/heads/main:refs/remotes/public/main') | Out-Null
    return Invoke-SnapshotGit -Arguments @('-C', $Repo, 'rev-parse', 'refs/remotes/public/main^{commit}')
}

# JuceSharp's <Version> moves with its tree: a public tree $Tree that differs from the
# previous snapshot $Parent's while Directory.Build.props still says $Version would put a
# second tree under one version (its tag, its packs, its native pin). The reason, or $null.
function Get-JuceSharpVersionProblem {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string]$Tree, [string]$Parent, [Parameter(Mandatory)][string]$Version)
    if (-not $Parent) { return $null }
    if ((Invoke-SnapshotGit -Arguments @('-C', $Repo, 'rev-parse', "$Parent^{tree}")) -eq $Tree) { return $null }
    try { $parentVersion = Get-MsBuildVersion (Invoke-SnapshotGit -Arguments @('-C', $Repo, 'show', "${Parent}:Directory.Build.props")) }
    catch { return $null }
    if ($parentVersion -cne $Version) { return $null }
    return "JuceSharp changed since its last public snapshot ($($Parent.Substring(0, 12))) but its <Version> is still ${Version}: " +
        'bump it in JuceSharp/Directory.Build.props first, then build the native pair and the packs from that tree'
}

# Rewrites the tree $SourceTree (readable in $Repo) for the public repo: excluded paths
# dropped, the rename map applied to every text blob that holds a dev name, then
# $Edit (a scriptblock given the index environment) for anything path-specific.
# Returns the new tree id. Uses a private index file; $Repo's own index is untouched.
function New-PublicTree {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$SourceTree,
        [scriptblock]$Edit
    )
    $index = Join-Path ([System.IO.Path]::GetTempPath()) ('dc-snapshot-index-' + [guid]::NewGuid().ToString('N'))
    $envIndex = @{ GIT_INDEX_FILE = $index }
    try {
        Invoke-SnapshotGit -Arguments @('-C', $Repo, 'read-tree', $SourceTree) -Environment $envIndex | Out-Null

        $listed = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'ls-files', '-z') -Environment $envIndex
        $drop = @($listed.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { Test-PublicExcludedPath $_ })
        foreach ($path in $drop) {
            Invoke-SnapshotGit -Arguments @('-C', $Repo, 'update-index', '--force-remove', '--', $path) -Environment $envIndex | Out-Null
        }

        # Candidates by a fast grep over the tree (-I: text only), then the exact rewrite.
        $pattern = '(DotCharter|JuceSharp)' + $script:DevSuffix
        $grep = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'grep', '-z', '-l', '-I', '-E', $pattern, $SourceTree) -AllowExitCodes @(1)
        $prefix = $SourceTree + ':'
        $latin1 = [System.Text.Encoding]::Latin1
        foreach ($hit in $grep.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
            $path = if ($hit.StartsWith($prefix)) { $hit.Substring($prefix.Length) } else { $hit }
            if (Test-PublicExcludedPath $path) { continue }
            $entry = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'ls-files', '-s', '--', $path) -Environment $envIndex
            if ($entry -notmatch '^(\d{6}) ([0-9a-f]{40}) \d\t') { continue }
            $mode = $Matches[1]; $blob = $Matches[2]
            $bytes = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'cat-file', 'blob', $blob) -AsBytes
            # Latin-1 maps every byte to one char and back, so untouched bytes stay exact.
            $text = $latin1.GetString($bytes)
            $renamed = Convert-PublicText -Text $text
            if ($renamed -ceq $text) { continue }
            $newBlob = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'hash-object', '-w', '--stdin') -InputBytes $latin1.GetBytes($renamed)
            Invoke-SnapshotGit -Arguments @('-C', $Repo, 'update-index', '--cacheinfo', "$mode,$newBlob,$path") -Environment $envIndex | Out-Null
        }

        if ($Edit) { & $Edit $envIndex }
        return Invoke-SnapshotGit -Arguments @('-C', $Repo, 'write-tree') -Environment $envIndex
    }
    finally { Remove-Item -LiteralPath $index -Force -ErrorAction SilentlyContinue }
}

# Replaces one file's content in the index given by $IndexEnvironment.
function Set-SnapshotIndexText {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][hashtable]$IndexEnvironment,
        [Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)
    $blob = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'hash-object', '-w', '--stdin') -InputBytes ([System.Text.UTF8Encoding]::new($false).GetBytes($Text))
    Invoke-SnapshotGit -Arguments @('-C', $Repo, 'update-index', '--add', '--cacheinfo', "100644,$blob,$Path") -Environment $IndexEnvironment | Out-Null
}

function Get-SnapshotIndexText {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][hashtable]$IndexEnvironment, [Parameter(Mandatory)][string]$Path)
    $entry = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'ls-files', '-s', '--', $Path) -Environment $IndexEnvironment
    if ($entry -notmatch '^\d{6} ([0-9a-f]{40}) ') { return $null }
    $bytes = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'cat-file', 'blob', $Matches[1]) -AsBytes
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

# The snapshot commit: $Tree on top of $Parent (none: a root commit), the owner as author
# AND committer, the given date, and exactly the one-line message - no trailer, no dev id.
# Reuses $Parent when the tree did not change. Moves refs/heads/main and the tag.
# The one-line message of a snapshot commit and of its tag, after SomeeName/rs7string's
# public history: the first snapshot (no public parent: a root commit) is "Initial public
# commit of <product>", every later one "Update to v<version>".
function Get-SnapshotMessage {
    param([Parameter(Mandatory)][string]$Product, [Parameter(Mandatory)][string]$Version, [string]$Parent)
    if (-not $Parent) { return "Initial public commit of $Product" }
    return "Update to v$Version"
}

function New-SnapshotCommit {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tree,
        [string]$Parent,
        [Parameter(Mandatory)][string]$Message,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Email,
        [Parameter(Mandatory)][string]$Date,
        [string]$Tag
    )
    if ($Message -match '[\r\n]') { throw 'A snapshot message is one line: no body, no trailer.' }
    # The owner is author, committer and (for the tag) tagger, all at $Date.
    $identity = @{
        GIT_AUTHOR_NAME = $Name; GIT_AUTHOR_EMAIL = $Email; GIT_AUTHOR_DATE = $Date
        GIT_COMMITTER_NAME = $Name; GIT_COMMITTER_EMAIL = $Email; GIT_COMMITTER_DATE = $Date
    }
    if ($Parent -and (Invoke-SnapshotGit -Arguments @('-C', $Repo, 'rev-parse', "$Parent^{tree}")) -eq $Tree) {
        $commit = $Parent
        $created = $false
        # Same tree, same version: the tag annotates the reused commit with ITS message, so a
        # re-export recreates the tag already public instead of one a push would reject.
        $Message = (Invoke-SnapshotGit -Arguments @('-C', $Repo, 'log', '-1', '--format=%B', $Parent)).Trim()
    }
    else {
        $arguments = @('-C', $Repo, 'commit-tree', $Tree)
        if ($Parent) { $arguments += @('-p', $Parent) }
        $arguments += @('-m', $Message)
        $commit = Invoke-SnapshotGit -Arguments $arguments -Environment $identity
        $created = $true
    }
    Invoke-SnapshotGit -Arguments @('-C', $Repo, 'update-ref', 'refs/heads/main', $commit) | Out-Null
    # An annotated tag whose message is the commit's one line.
    if ($Tag) { Invoke-SnapshotGit -Arguments @('-C', $Repo, 'tag', '-a', '-f', '-m', $Message, $Tag, $commit) -Environment $identity | Out-Null }
    return [pscustomobject]@{ Commit = $commit; Created = $created; Parent = $Parent; Tag = $Tag }
}

# Where the public repo at $Url stands for this snapshot, from `git ls-remote` alone
# (nothing fetched, nothing pushed): Problems, each a reason -Push must not run, and
# Refspecs, what an atomic push sends (none when the snapshot and its tag are public).
#   - main must still be $Parent, the snapshot's parent (none: no main yet), or already be
#     $Commit (an unchanged tree reused the public commit);
#   - the tag $Tag must be absent, or already name $Commit (the unchanged JuceSharp case:
#     its existing public tag is kept, never re-pushed). A tag for another commit is a
#     version that was not bumped: public tags are never moved.
function Get-PublicPushPlan {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Commit,
        [string]$Parent,
        [Parameter(Mandatory)][string]$Tag
    )
    $refs = @{}
    foreach ($line in (Invoke-SnapshotGit -Arguments @('ls-remote', '--heads', '--tags', $Url)).Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
        $f = $line.Trim().Split("`t")
        if ($f.Count -eq 2) { $refs[$f[1]] = $f[0] }
    }
    $problems = [System.Collections.Generic.List[string]]::new()
    $refspecs = [System.Collections.Generic.List[string]]::new()
    $main = $refs['refs/heads/main']
    if ($main -ne $Commit) {
        if ("$main" -ne "$Parent") {
            $was = if ($Parent) { $Parent } else { '(no main: a root commit)' }
            $now = if ($main) { $main } else { '(no main)' }
            $problems.Add("public main is $now, but the snapshot was built on ${was}: re-run the export")
        }
        else { $refspecs.Add('refs/heads/main:refs/heads/main') }
    }
    $tagObject = $refs["refs/tags/$Tag"]
    if ($tagObject) {
        $peeled = $refs["refs/tags/$Tag^{}"]
        if (-not $peeled) { $peeled = $tagObject }
        if ($peeled -ne $Commit) {
            $problems.Add("tag $Tag already exists for commit $peeled, but this snapshot is ${Commit}: bump the version (a public tag is never moved)")
        }
    }
    else { $refspecs.Add("refs/tags/${Tag}:refs/tags/$Tag") }
    return [pscustomobject]@{ Url = $Url; Commit = $Commit; Tag = $Tag; Problems = @($problems); Refspecs = @($refspecs) }
}

# Writes the tree of $Commit (in $GitDir) into $Destination through a private index.
# -IndexPath keeps that index between runs: a kept work tree is then updated like a
# checkout (only changed files are written, deleted ones removed, the rest keep their
# timestamps, so an incremental build stays incremental).
function Export-SnapshotWorkTree {
    param([Parameter(Mandatory)][string]$GitDir, [Parameter(Mandatory)][string]$Commit, [Parameter(Mandatory)][string]$Destination,
        [string]$IndexPath)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $base = @("--git-dir=$GitDir", "--work-tree=$Destination", '-c', 'core.autocrlf=false', '-c', 'core.longpaths=true')
    if ($IndexPath) {
        Invoke-SnapshotGit -Arguments ($base + @('read-tree', '--reset', '-u', $Commit)) -Environment @{ GIT_INDEX_FILE = $IndexPath } | Out-Null
        return
    }
    $index = Join-Path ([System.IO.Path]::GetTempPath()) ('dc-snapshot-index-' + [guid]::NewGuid().ToString('N'))
    try {
        $envIndex = @{ GIT_INDEX_FILE = $index }
        Invoke-SnapshotGit -Arguments ($base + @('read-tree', $Commit)) -Environment $envIndex | Out-Null
        Invoke-SnapshotGit -Arguments ($base + @('checkout-index', '-a', '-f')) -Environment $envIndex | Out-Null
    }
    finally { Remove-Item -LiteralPath $index -Force -ErrorAction SilentlyContinue }
}

# --- the kept scratch work tree ---------------------------------------------------------
# The export keeps its DotCharter work tree between runs so the public build is
# incremental. Its build outputs (git-ignored: bin/, obj/, artifacts/, the copied native
# pair) are PARKED outside the tree while the leak check and gitleaks read it, so both
# scans see exactly the tracked files: Assert-SnapshotTracksEverything refuses a scan of
# a tree holding anything git does not track. A $Trees entry is one git view of the work
# tree: @{ Prefix = '' or 'JuceSharp/'; Git = <git arguments naming it>; Environment = @{} }.

# Clears every ref of a kept scratch repo (branches, tags, the fetched public main), so
# nothing from the previous run can be read as this run's; the index stays.
function Reset-SnapshotScratchRefs {
    param([Parameter(Mandatory)][string]$Repo)
    $refs = Invoke-SnapshotGit -Arguments @('-C', $Repo, 'for-each-ref', '--format=%(refname)')
    foreach ($ref in $refs.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
        Invoke-SnapshotGit -Arguments @('-C', $Repo, 'update-ref', '-d', $ref.Trim()) | Out-Null
    }
    Invoke-SnapshotGit -Arguments @('-C', $Repo, 'symbolic-ref', 'HEAD', 'refs/heads/main') | Out-Null
}

# Every path of one work tree view that git does not track, untracked directories
# collapsed (--directory); -Ignored: only the git-ignored ones.
function Get-SnapshotUntrackedPaths {
    param([Parameter(Mandatory)][string[]]$GitArguments, [hashtable]$Environment = @{}, [switch]$Ignored)
    $arguments = $GitArguments + @('-c', 'core.quotepath=off', 'ls-files', '-z', '--others', '--directory')
    if ($Ignored) { $arguments += @('--ignored', '--exclude-standard') }
    $out = Invoke-SnapshotGit -Arguments $arguments -Environment $Environment
    return @($out.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.TrimEnd('/') } | Where-Object { $_ })
}

# Moves every git-ignored path of $Trees out of $Root into $Cache/tree/<same path>, and
# lists them in $Cache/parked.txt (merged with a list a crashed run left). Returns the list.
function Move-SnapshotBuildOutputs {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Cache, [Parameter(Mandatory)][object[]]$Trees)
    $manifest = Join-Path $Cache 'parked.txt'
    $parked = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $manifest) { foreach ($line in Get-Content -LiteralPath $manifest) { if ($line) { $parked.Add($line) } } }
    # Every view is listed BEFORE anything moves, so the manifest is what git reported at
    # the start; a path inside one another view listed has gone with it by its turn.
    $listed = @(foreach ($tree in $Trees) {
            if (-not (Test-Path -LiteralPath (Join-Path $Root $tree.Prefix))) { continue }
            foreach ($path in Get-SnapshotUntrackedPaths -GitArguments $tree.Git -Environment $tree.Environment -Ignored) { $tree.Prefix + $path }
        })
    foreach ($relative in $listed) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $relative))) { continue }
        $target = Join-Path (Join-Path $Cache 'tree') $relative
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Move-Item -LiteralPath (Join-Path $Root $relative) -Destination $target
        if (-not $parked.Contains($relative)) { $parked.Add($relative) }
    }
    New-Item -ItemType Directory -Force -Path $Cache | Out-Null
    Set-Content -LiteralPath $manifest -Value @($parked) -Encoding utf8NoBOM
    return @($parked)
}

# Throws unless every view of $Trees has NOTHING git does not track: the scans that
# follow read the file system, so this is what makes them read the tracked tree only.
function Assert-SnapshotTracksEverything {
    param([Parameter(Mandatory)][object[]]$Trees)
    $stray = @(foreach ($tree in $Trees) {
            foreach ($path in Get-SnapshotUntrackedPaths -GitArguments $tree.Git -Environment $tree.Environment) { $tree.Prefix + $path }
        })
    if ($stray.Count -gt 0) {
        throw "Refusing to leak-check a work tree holding $($stray.Count) untracked path(s) (the scans must read the tracked tree only): $(($stray | Select-Object -First 10) -join ', ')"
    }
}

# Moves the parked outputs back into $Root and deletes $Cache. A path the new tree now
# occupies keeps the tree's version; its parked copy is dropped. Returns what moved back.
function Restore-SnapshotBuildOutputs {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Cache)
    $manifest = Join-Path $Cache 'parked.txt'
    if (-not (Test-Path -LiteralPath $manifest)) { return @() }
    $restored = @(foreach ($relative in Get-Content -LiteralPath $manifest) {
            if (-not $relative) { continue }
            $source = Join-Path (Join-Path $Cache 'tree') $relative
            $target = Join-Path $Root $relative
            if (-not (Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $target)) { continue }
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Move-Item -LiteralPath $source -Destination $target
            $relative
        })
    Remove-Item -LiteralPath $Cache -Recurse -Force
    return $restored
}

Export-ModuleMember -Function Get-PublicRenameMap, Convert-PublicText, Get-PublicExcludedPaths, Test-PublicExcludedPath,
    Get-PersonalDataPatterns, Get-PublicLeakRules, Find-PublicLeaksInText, Find-PublicLeaksInPath, Find-PublicTreeLeaks,
    Format-PublicLeakReport, ConvertTo-LeakScanText, Expand-LeakScanGzip, Get-PublicPushPlan, Get-NativeRuntimePendingHash, Add-PublicNativeRuntimeEntry, Get-MsBuildVersion,
    Get-InheritedNativePins, Get-UserPathPlaceholders, Get-UserPathPlaceholderLookahead,
    Invoke-SnapshotGit, New-SnapshotScratchRepo, Get-PreviousSnapshot, Get-JuceSharpVersionProblem, New-PublicTree, Set-SnapshotIndexText,
    Get-SnapshotIndexText, Get-SnapshotMessage, New-SnapshotCommit, Export-SnapshotWorkTree,
    Reset-SnapshotScratchRefs, Get-SnapshotUntrackedPaths, Move-SnapshotBuildOutputs, Assert-SnapshotTracksEverything, Restore-SnapshotBuildOutputs
