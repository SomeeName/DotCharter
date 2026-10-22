<#
.SYNOPSIS
    Which test classes cover a change: the selection behind
    `tools/agent/Invoke-Gate.ps1 -Affected`, as functions DotCharter.Tests drives
    under Windows PowerShell 5.1 (Infrastructure/AffectedTestsModuleTests).

.DESCRIPTION
    -Affected is a LOOP tool. It runs the classes that plausibly cover the diff
    and records nothing: it never stands for a green tree, and the merge train
    still runs the full gate once. The selection is a heuristic that errs wide
    (a class that merely names a changed type runs), and it is deliberately
    plain text matching, so every pick can be explained in one line.

    THE RULES (Get-AffectedSelection), applied to every changed path:
      1. A changed test file selects the test classes declared in it.
      2. A changed file selects every test class whose source names the file
         (`Note.cs`) or, for `.cs` / `.axaml`, its main type (`Note`, the file name
         up to the first dot, as a whole word). The main type also selects the
         classes named after it (`NoteTests`, `NoteBarTests`). A changed file that
         itself declares test classes skips this rule (rule 1 covers it), and
         the file names every guide shares (AGENTS.md, README.md ...) are not
         matched by name: the docs lints below cover guides.
      3. The test folder mirroring the changed project folder
         (`DotCharter.Services/Editing/X.cs` -> `DotCharter.Tests/Services/Editing`),
         the deepest one that exists, and only when it holds at most
         $script:MirrorMaxClasses classes (a wider folder says little about one file).
      4. ALWAYS the structural lints (size and complexity budgets, comment
         hygiene, doc references, layering, the split-type ratchet).
      5. A `.resx` change adds the localization lints; a `.md` or `docs/` change
         adds the lints that read guides (the merge train's docs list).
    Headless classes (`*UiPerfSmokeTests`) are picked by the same rules and run
    through the headless path; the golden screenshots stay on `-Goldens`. Release
    lane classes are listed, not run, unless their own test file changed.

    FULL-GATE FALLBACK (Get-AffectedFallbackReason): a change to tools/, a
    project, props, targets, solution or global config, or the JuceSharp gitlink
    moves what every test runs on, so no subset is honest; the answer is the full
    gate, and a selection wider than the cap says the same.

    The index (New-AffectedTestIndex) reads the test sources, not the built
    assembly: a test class is a public, non-static, non-abstract class whose own
    text carries a [Fact] / [Theory] (or a derived attribute); a nested one is
    `Outer+Inner`. Names are the runner's full names, the form `--filter-class`
    takes exactly.

    Runs under Windows PowerShell 5.1 and pwsh 7.
#>

Set-StrictMode -Off
Import-Module (Join-Path $PSScriptRoot 'MergeTrain.psm1')

$script:TestProject = 'DotCharter.Tests'

# The lints that read the whole tree: any change can break them, so every
# -Affected run includes them. A renamed class is reported by name (and fails
# AffectedTestsModuleTests), never dropped silently.
$script:StructuralLints = @(
    'CommentHygieneTests',
    'DocReferenceLintTests',
    'SourceFileSizeBudgetTests',
    'MethodComplexityBudgetTests',
    'AssemblyLayeringTests',
    'SplitTypePartialRatchetTests'
)

# Added when a .resx changes, besides every class under DotCharter.Tests/Localization.
$script:LocalizationLints = @(
    'LocalizationSweepTests',
    'StaticLocalizationRatchetTests',
    'SourceEncodingLintTests'
)

# Classes with their own lane: the golden screenshots are owner-only (-Goldens).
$script:OwnLaneClasses = @('DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests')

# File names every guide or project shares; matching them by name would pick unrelated classes.
$script:SharedFileNames = @('AGENTS.md', 'CLAUDE.md', 'README.md', 'CHANGELOG.md', 'LICENSE', 'NOTICE', 'PROVENANCE.md')

# Changes that move what every test runs on (leaf names, wildcard; path prefixes).
$script:FallbackLeafPatterns = @(
    'Directory.*.props', 'Directory.*.targets', '*.csproj', '*.props', '*.targets', '*.sln', '*.slnx',
    'global.json', 'nuget.config', '.editorconfig', '.globalconfig', '.gitmodules', '.gitattributes', 'packages.lock.json'
)
$script:FallbackPrefixes = @('tools/')

# A source project -> the test folders (under DotCharter.Tests) that mirror it, most specific first.
# '' is the test project root (DotCharter.Tone/Audio/X -> DotCharter.Tests/Audio).
$script:MirrorRoots = @{
    'DotCharter.Core' = @('Core'); 'DotCharter.Analysis' = @('Analysis'); 'DotCharter.Rendering' = @('Rendering')
    'DotCharter.Localization' = @('Localization'); 'DotCharter.Services' = @('Services')
    'DotCharter.Presentation' = @('Presentation'); 'DotCharter.Presentation.Fakes' = @('Presentation')
    'DotCharter.RigLab' = @('RigLab'); 'DotCharter.UI' = @('UI'); 'DotCharter.UI.Gallery' = @('UI')
    'DotCharter.Tone' = @(''); 'DotCharter.Tone.UI' = @('UI')
}
$script:MirrorMaxClasses = 40
$script:HeadlessSuffix = 'UiPerfSmokeTests'

$script:FactPattern = '\[(?:\w+\.)*\w*(?:Fact|Theory)\b'

function ConvertTo-AffectedPath([string]$Path) { return ($Path -replace '\\', '/').Trim().TrimStart('/') }

function Get-AffectedLeaf([string]$Path) { return $Path.Substring($Path.LastIndexOf('/') + 1) }

# ---- the full-gate fallback ----------------------------------------------------

# The reason a change needs the full gate, or $null when a selection is honest.
function Get-AffectedFallbackReason([string[]]$Paths) {
    foreach ($raw in @($Paths | Where-Object { $_ -and $_.Trim() })) {
        $p = ConvertTo-AffectedPath $raw
        if ($p -eq 'JuceSharp' -or $p -like 'JuceSharp/*') { return "$p moves the native runtime every test loads" }
        foreach ($prefix in $script:FallbackPrefixes) {
            if ($p.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return "$p is under $prefix (the scripts and build tooling every gate runs through)" }
        }
        $leaf = Get-AffectedLeaf $p
        foreach ($pattern in $script:FallbackLeafPatterns) {
            if ($leaf -like $pattern) { return "$p is build or global configuration" }
        }
    }
    return $null
}

# ---- the changed paths -----------------------------------------------------------

function Invoke-AffectedGit([string]$RepoRoot, [string[]]$GitArgs) {
    $out = & git -C $RepoRoot -c core.quotepath=off @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed (exit $LASTEXITCODE): $($out -join ' ')" }
    return @($out | ForEach-Object { "$_" } | Where-Object { $_ })
}

<#
    The files changed since the merge-base of -Base and the head: committed and
    uncommitted tracked changes plus untracked files (the working tree), or, when
    -Head is given, only the commits Base..Head. Both sides of a rename are listed
    (a deleted file's tests still name it). Returns MergeBase and Paths.
#>
function Get-AffectedChangedPaths([string]$RepoRoot, [string]$Base = 'origin/main', [string]$Head = '') {
    $headRef = if ($Head) { $Head } else { 'HEAD' }
    $mergeBase = (Invoke-AffectedGit $RepoRoot @('merge-base', $Base, $headRef) | Select-Object -First 1).Trim()
    $paths = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    if ($Head) { $diff = Invoke-AffectedGit $RepoRoot @('diff', '--name-only', '--no-renames', $mergeBase, $Head) }
    else {
        $diff = @(Invoke-AffectedGit $RepoRoot @('diff', '--name-only', '--no-renames', $mergeBase))
        $diff += @(Invoke-AffectedGit $RepoRoot @('ls-files', '--others', '--exclude-standard'))
    }
    foreach ($d in $diff) { [void]$paths.Add((ConvertTo-AffectedPath $d)) }
    return [pscustomobject]@{ MergeBase = $mergeBase; Paths = [string[]]@($paths) }
}

# ---- the test index --------------------------------------------------------------

# The test classes declared at one nesting level of $Text (and, recursively, the classes nested in them,
# named `Outer+Inner` as the runner names them): each with the block of text it owns. A class is a test
# class when its OWN text (nested classes excluded) carries a fact; static, abstract, private and protected classes never are (the runner discovers public ones).
# The declarations are matched once over the whole file and read as plain arrays: this runs for every test file
# of the project, so a nested level only walks the same arrays inside its owner's range.
function Get-AffectedClassBlocks([string]$Text, [int]$Indent, [string]$Prefix, $Declaration, $FactPattern) {
    $found = $Declaration.Matches($Text)
    $count = $found.Count
    $starts = [int[]]::new($count)
    $indents = [int[]]::new($count)
    $skipped = [bool[]]::new($count)
    $names = [string[]]::new($count)
    for ($i = 0; $i -lt $count; $i++) {
        $m = $found[$i]
        $starts[$i] = $m.Index
        $indents[$i] = $m.Groups[1].Length
        $skipped[$i] = $m.Groups[2].Value -match '\b(static|abstract|private|protected)\b'
        $names[$i] = $m.Groups[3].Value
    }
    $blocks = [System.Collections.Generic.List[object]]::new()
    Add-AffectedClassBlocks $blocks $Text $starts $indents $skipped $names 0 $Text.Length $Indent $Prefix $FactPattern
    return $blocks.ToArray()
}

function Add-AffectedClassBlocks($Blocks, [string]$Text, [int[]]$Starts, [int[]]$Indents, [bool[]]$Skipped, [string[]]$Names, [int]$From, [int]$To, [int]$Indent, [string]$Prefix, $FactPattern) {
    $own = [System.Collections.Generic.List[int]]::new()
    for ($i = 0; $i -lt $Starts.Length; $i++) {
        if ($Starts[$i] -ge $From -and $Starts[$i] -lt $To -and $Indents[$i] -eq $Indent) { $own.Add($i) }
    }
    for ($k = 0; $k -lt $own.Count; $k++) {
        $i = $own[$k]
        if ($Skipped[$i]) { continue }
        $start = $Starts[$i]
        $end = if ($k + 1 -lt $own.Count) { $Starts[$own[$k + 1]] } else { $To }
        $full = $Prefix + $Names[$i]
        $slot = $Blocks.Count
        $Blocks.Add($null)
        Add-AffectedClassBlocks $Blocks $Text $Starts $Indents $Skipped $Names $start $end ($Indent + 4) ($full + '+') $FactPattern
        $body = $Text.Substring($start, $end - $start)
        $remaining = $body
        # Cut the direct children out, last first so earlier offsets stay valid.
        for ($j = $Blocks.Count - 1; $j -gt $slot; $j--) {
            $n = $Blocks[$j]
            if ($n.Level -eq ($Indent + 4)) { $remaining = $remaining.Remove($n.Start - $start, $n.Length) }
        }
        if ($FactPattern.IsMatch($remaining)) {
            $Blocks[$slot] = [pscustomobject]@{ Full = $full; Name = $Names[$i]; Body = $body; Start = $start; Length = $end - $start; Level = $Indent }
        }
        else { $Blocks.RemoveAt($slot) }
    }
}

<#
    The test classes of DotCharter.Tests, read from source. Returns
      Classes   full name -> { FullName, Name, Dir, Files, Text }
      ByName    simple name -> full names
      DirCounts test folder (repo-relative, '/') -> classes at or below it
      FileClasses test file (repo-relative) -> full names declared in it
#>
function New-AffectedTestIndex([string]$RepoRoot) {
    $testRoot = Join-Path $RepoRoot $script:TestProject
    $classes = @{}; $byName = @{}; $dirCounts = @{}; $fileClasses = @{}
    if (-not (Test-Path -LiteralPath $testRoot)) { return [pscustomobject]@{ Classes = $classes; ByName = $byName; DirCounts = $dirCounts; FileClasses = $fileClasses } }
    $declaration = New-Object System.Text.RegularExpressions.Regex('^(?<indent> *)(?<mods>(?:(?:public|internal|protected|private|sealed|abstract|static|partial|unsafe|file)\s+)*)class\s+(?<name>\w+)', 'Multiline, Compiled')
    $namespacePattern = New-Object System.Text.RegularExpressions.Regex('^namespace\s+(?<ns>[\w.]+)\s*(?<scoped>;)?', 'Multiline')
    $factPattern = New-Object System.Text.RegularExpressions.Regex($script:FactPattern)
    # Comments are not uses: a class that only mentions a type in prose does not cover it.
    $commentPattern = New-Object System.Text.RegularExpressions.Regex('//[^\r\n]*')
    $rootLength = $RepoRoot.TrimEnd('\', '/').Length + 1

    foreach ($file in (Get-ChildItem -LiteralPath $testRoot -Recurse -Filter '*.cs' -File)) {
        $rel = ($file.FullName.Substring($rootLength) -replace '\\', '/')
        if ($rel -match '/(obj|bin)/') { continue }
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if (-not $factPattern.IsMatch($text)) { continue }
        $ns = $namespacePattern.Match($text)
        $indent = if ($ns.Success -and -not $ns.Groups['scoped'].Success) { 4 } else { 0 }
        $prefix = if ($ns.Success) { $ns.Groups['ns'].Value + '.' } else { '' }
        $dir = $rel.Substring(0, $rel.LastIndexOf('/'))
        foreach ($block in @(Get-AffectedClassBlocks $text $indent $prefix $declaration $factPattern)) {
            $full = $block.Full
            if (-not $classes.ContainsKey($full)) {
                $classes[$full] = [pscustomobject]@{ FullName = $full; Name = $block.Name; Dir = $dir; Files = [System.Collections.Generic.List[string]]::new(); Text = '' }
                if (-not $byName.ContainsKey($block.Name)) { $byName[$block.Name] = [System.Collections.Generic.List[string]]::new() }
                $byName[$block.Name].Add($full)
                $walk = $dir
                while ($walk.Length -ge $script:TestProject.Length) {
                    $dirCounts[$walk] = 1 + [int]$dirCounts[$walk]
                    $cut = $walk.LastIndexOf('/')
                    if ($cut -lt 0) { break }
                    $walk = $walk.Substring(0, $cut)
                }
            }
            $classes[$full].Files.Add($rel)
            $classes[$full].Text += $commentPattern.Replace($block.Body, '')
            if (-not $fileClasses.ContainsKey($rel)) { $fileClasses[$rel] = [System.Collections.Generic.List[string]]::new() }
            $fileClasses[$rel].Add($full)
        }
    }
    return [pscustomobject]@{ Classes = $classes; ByName = $byName; DirCounts = $dirCounts; FileClasses = $fileClasses }
}

# ---- the selection ---------------------------------------------------------------

# The test folder (repo-relative) mirroring the folder of a changed source file, or $null.
function Get-AffectedMirrorFolder([string]$Path, $Index) {
    $segments = @($Path -split '/')
    if ($segments.Count -lt 2) { return $null }
    if (-not $script:MirrorRoots.ContainsKey($segments[0])) { return $null }
    $roots = @($script:MirrorRoots[$segments[0]])
    $below = if ($segments.Count -gt 2) { @($segments[1..($segments.Count - 2)]) } else { @() }
    foreach ($root in $roots) {
        for ($depth = $below.Count; $depth -ge 0; $depth--) {
            $parts = @($script:TestProject)
            if ($root) { $parts += $root }
            if ($depth -gt 0) { $parts += $below[0..($depth - 1)] }
            $candidate = $parts -join '/'
            $count = [int]$Index.DirCounts[$candidate]
            if ($count -eq 0) { continue }
            if ($count -gt $script:MirrorMaxClasses -or ($depth -eq 0 -and -not $root)) { return $null }
            return $candidate
        }
    }
    return $null
}

function Test-AffectedHeadless([string]$FullName) { return $FullName.EndsWith($script:HeadlessSuffix, [StringComparison]::Ordinal) }

<#
    The selection for the changed paths. Returns
      Fallback   $null, or the reason the full gate is the honest run
      NoChanges  no changed path
      Gate       selected non-headless classes: { FullName, Reasons, LaneSeconds }
      Headless   selected headless classes
      LeftToLane release-lane classes that matched but are not run
      OwnLane    classes with their own lane that matched
      Notes      problems worth a line (a structural lint the project does not declare)
      Tally      reason kind -> classes
#>
function Get-AffectedSelection {
    param([string[]]$Paths, $Index, [string[]]$LaneClasses = @(), [int]$MaxClasses = 600)
    $changed = @($Paths | Where-Object { $_ -and $_.Trim() } | ForEach-Object { ConvertTo-AffectedPath $_ } | Sort-Object -Unique)
    $result = [pscustomobject]@{ Fallback = $null; NoChanges = $false; Changed = $changed; Gate = @(); Headless = @()
        LeftToLane = @(); OwnLane = @(); Notes = @(); Tally = @{}; Reasons = @{} }
    if ($changed.Count -eq 0) { $result.NoChanges = $true; return $result }
    $result.Fallback = Get-AffectedFallbackReason $changed
    if ($result.Fallback) { return $result }

    $picked = @{}
    $add = {
        param([string]$Class, [string]$Kind, [string]$Text)
        if (-not $picked.ContainsKey($Class)) { $picked[$Class] = New-Object 'System.Collections.Generic.List[object]' }
        foreach ($r in $picked[$Class]) { if ($r.Text -eq $Text) { return } }
        $picked[$Class].Add([pscustomobject]@{ Kind = $Kind; Text = $Text })
    }
    $notes = [System.Collections.Generic.List[string]]::new()

    # Rule 4 and 5: lints picked by simple name.
    $byName = {
        param([string[]]$Names, [string]$Kind, [string]$Why)
        foreach ($n in $Names) {
            $full = if ($Index.ByName.ContainsKey($n)) { $Index.ByName[$n].ToArray() } else { @() }
            if ($full.Count -eq 0) { $notes.Add("$Kind '$n' is not a test class of $script:TestProject (renamed? fix tools/agent/AffectedTests.psm1)"); continue }
            foreach ($f in $full) { & $add $f $Kind $Why }
        }
    }
    & $byName $script:StructuralLints 'structural lint' 'always runs'

    $anyResx = $false; $anyDoc = $false
    foreach ($p in $changed) {
        if ($p -like '*.resx') { $anyResx = $true }
        if (Test-MergeTrainDocsOnly @($p)) { $anyDoc = $true }
    }
    if ($anyResx) {
        & $byName $script:LocalizationLints 'localization lint' 'a .resx changed'
        foreach ($c in $Index.Classes.Values) {
            if ($c.Dir -eq "$script:TestProject/Localization" -or $c.Dir.StartsWith("$script:TestProject/Localization/")) { & $add $c.FullName 'localization lint' 'a .resx changed' }
        }
    }
    if ($anyDoc) {
        $docNames = @(Get-MergeTrainDocsOnlyClasses | ForEach-Object { $_.TrimStart('*') })
        & $byName $docNames 'docs lint' 'a guide or docs/ file changed'
    }

    $classList = @($Index.Classes.Values)
    foreach ($p in $changed) {
        $leaf = Get-AffectedLeaf $p
        # Rule 1.
        $declaresTests = $Index.FileClasses.ContainsKey($p)
        if ($declaresTests) { foreach ($c in $Index.FileClasses[$p]) { & $add $c 'changed test file' "changed test file $p" } }
        # Rule 2.
        if (-not $declaresTests) {
            $type = $null
            if ($leaf -match '^(?<type>[A-Za-z_]\w*)\..*') {
                $ext = [System.IO.Path]::GetExtension($leaf)
                if ($ext -eq '.cs' -or $ext -eq '.axaml') { $type = $Matches['type'] }
            }
            # A code file is matched by its type: every mention of `Note.cs` contains the word `Note`.
            $matchLeaf = (-not $type) -and ($script:SharedFileNames -notcontains $leaf)
            $typeRegex = if ($type) { New-Object System.Text.RegularExpressions.Regex("\b$([regex]::Escape($type))\b") } else { $null }
            $namePrefix = if ($type) { New-Object System.Text.RegularExpressions.Regex("^$([regex]::Escape($type))(?![a-z0-9_])") } else { $null }
            foreach ($c in $classList) {
                if ($matchLeaf -and $c.Text.IndexOf($leaf, [StringComparison]::Ordinal) -ge 0) { & $add $c.FullName 'names file' "names $leaf" }
                if ($typeRegex -and $typeRegex.IsMatch($c.Text)) { & $add $c.FullName 'names type' "names type $type" }
                if ($namePrefix -and $namePrefix.IsMatch($c.Name)) { & $add $c.FullName 'name starts with type' "named after type $type" }
            }
        }
        # Rule 3.
        $mirror = Get-AffectedMirrorFolder $p $Index
        if ($mirror) {
            foreach ($c in $classList) {
                if ($c.Dir -eq $mirror -or $c.Dir.StartsWith("$mirror/")) { & $add $c.FullName 'folder mirror' "in $mirror/" }
            }
        }
    }

    $lane = @{}; foreach ($l in $LaneClasses) { $lane[$l] = $true }
    $own = @{}; foreach ($o in $script:OwnLaneClasses) { $own[$o] = $true }
    $gate = @(); $headless = @(); $left = @(); $ownHit = @()
    $names = [string[]]@($picked.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    foreach ($name in $names) {
        $reasons = $picked[$name].ToArray()
        $entry = [pscustomobject]@{ FullName = $name; Reasons = $reasons }
        $result.Reasons[$name] = $reasons
        if ($own.ContainsKey($name)) { $ownHit += $name; continue }
        $testEdited = @($reasons | Where-Object { $_.Kind -eq 'changed test file' }).Count -gt 0
        if ($lane.ContainsKey($name) -and -not $testEdited) { $left += $name; continue }
        if (Test-AffectedHeadless $name) { $headless += $entry } else { $gate += $entry }
        foreach ($kind in @($reasons | ForEach-Object { $_.Kind } | Sort-Object -Unique)) { $result.Tally[$kind] = 1 + [int]$result.Tally[$kind] }
    }
    $result.Gate = $gate; $result.Headless = $headless; $result.LeftToLane = $left; $result.OwnLane = $ownHit
    $result.Notes = @($notes)
    $total = $gate.Count + $headless.Count
    if ($total -gt $MaxClasses) {
        $result.Fallback = "the selection is $total classes (more than $MaxClasses): the diff reaches too much for a subset to save time"
    }
    return $result
}

# The classes -Affected will run, by full name: gate classes, then headless classes.
function Get-AffectedClassNames($Selection) {
    return @(@($Selection.Gate) + @($Selection.Headless) | ForEach-Object { $_.FullName })
}

# ---- running ---------------------------------------------------------------------

# Estimated gate seconds of the classes: the measured weights, an unlisted class (under 1 s) counted at $UnlistedSeconds.
function Get-AffectedEstimateSeconds([string[]]$Classes, [System.Collections.IDictionary]$Weights, [double]$UnlistedSeconds = 0.1) {
    $sum = 0.0
    foreach ($c in $Classes) { if ($Weights -and $Weights.Contains($c)) { $sum += [double]$Weights[$c] } else { $sum += $UnlistedSeconds } }
    return $sum
}

# Processes worth starting: one per $SecondsPerShard of estimated work, at most $MaxShards. A testhost
# costs seconds before its first case, so a small selection stays in one process.
function Get-AffectedShardCount([double]$EstimatedSeconds, [int]$MaxShards, [double]$SecondsPerShard = 30) {
    $wanted = [int][math]::Ceiling($EstimatedSeconds / $SecondsPerShard)
    return [math]::Max(1, [math]::Min([math]::Max(1, $MaxShards), $wanted))
}

# ---- the report ------------------------------------------------------------------

function Format-AffectedClassName([string]$FullName) {
    $prefix = "$script:TestProject."
    if ($FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { return $FullName.Substring($prefix.Length) }
    return $FullName
}

function Format-AffectedReasons($Reasons) {
    $texts = @($Reasons | ForEach-Object { $_.Text })
    $shown = @($texts | Select-Object -First 3) -join '; '
    if ($texts.Count -gt 3) { $shown += " (+$($texts.Count - 3) more)" }
    return $shown
}

# The lines -Affected prints: what changed, what was picked and why each class was.
function Format-AffectedSelection($Selection, [string]$BaseLabel, [int]$IndexedClasses = 0) {
    $lines = @()
    if ($Selection.NoChanges) { return @("affected: nothing changed against $BaseLabel - nothing to run") }
    $lines += "affected: $($Selection.Changed.Count) changed file(s) against $BaseLabel"
    if ($Selection.Changed.Count -le 12) { foreach ($c in $Selection.Changed) { $lines += "  changed: $c" } }
    if ($Selection.Fallback) {
        $lines += "affected: FULL GATE - $($Selection.Fallback)"
        return $lines
    }
    $total = @($Selection.Gate).Count + @($Selection.Headless).Count
    $of = if ($IndexedClasses -gt 0) { " of $IndexedClasses test classes" } else { '' }
    $lines += "affected: $total class(es) selected$of - $(@($Selection.Gate).Count) gate, $(@($Selection.Headless).Count) headless"
    $tally = @($Selection.Tally.Keys | Sort-Object | ForEach-Object { "$_ $($Selection.Tally[$_])" }) -join ', '
    if ($tally) { $lines += "  by reason: $tally" }
    foreach ($group in @(@{ Name = 'gate'; Items = $Selection.Gate }, @{ Name = 'headless'; Items = $Selection.Headless })) {
        if (@($group.Items).Count -eq 0) { continue }
        $lines += "  $($group.Name) classes:"
        foreach ($i in $group.Items) { $lines += ('    {0}  <- {1}' -f (Format-AffectedClassName $i.FullName), (Format-AffectedReasons $i.Reasons)) }
    }
    if (@($Selection.LeftToLane).Count -gt 0) {
        $lines += "  not run, release lane ($(@($Selection.LeftToLane).Count); the merge train runs them, or -FilterClass): " + ((@($Selection.LeftToLane) | ForEach-Object { Format-AffectedClassName $_ }) -join ', ')
    }
    if (@($Selection.OwnLane).Count -gt 0) {
        $lines += "  not run, own lane (-Goldens): " + ((@($Selection.OwnLane) | ForEach-Object { Format-AffectedClassName $_ }) -join ', ')
    }
    foreach ($n in @($Selection.Notes)) { $lines += "  NOTE: $n" }
    return $lines
}

Export-ModuleMember -Function Get-AffectedFallbackReason, Get-AffectedChangedPaths, New-AffectedTestIndex, `
    Get-AffectedMirrorFolder, Get-AffectedSelection, Get-AffectedClassNames, Get-AffectedEstimateSeconds, `
    Get-AffectedShardCount, Format-AffectedSelection
