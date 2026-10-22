<#
.SYNOPSIS
    The default gate's per-class durations, and its split across N concurrent
    `dotnet test` processes. Used by tools/agent/Invoke-Gate.ps1 (-Gate, and so
    Merge-Train.ps1 and CI's gate job). The process machinery - start, sample,
    wait, merge the TRX files - is tools/ci/HeadlessShards.psm1's; this module
    only decides WHAT each process runs and reads the durations back.

.DESCRIPTION
    DURATIONS (Read-TrxClassDurations, Get-ClassDurationSummary). A TRX's
    UnitTestResult carries a duration and a testId; its TestDefinitions map the
    id to the class (TestMethod/@className), which is right even for a nested
    class or a case with a custom DisplayName. The summary is the top N classes
    with their seconds and share of the SUMMED case time.

    THE PARTITION (New-GateShardPlan) is an EXACT cover of the gate by
    construction, with no discovery run and no reliance on how a case's name
    lists:
      * shards 1..N-1 each run an explicit list of classes from the weights
        file: `--filter-not-class <the gate's exclusion>` plus one
        `--filter-class` per class;
      * shard N, the REMAINDER, runs the gate's own filter minus every class
        listed for shards 1..N-1 (one `--filter-not-class` each).
    The listed sets are disjoint, so the union of the shards is the gate's case
    set, each case exactly once - whatever classes were added, renamed or
    deleted since the weights were measured (a new class simply runs in the
    remainder; a deleted one matches nothing). Invoke-Gate.ps1 -SelfTest
    proves it against the runner's own listing.

    Balance: longest-processing-time first over the measured class seconds,
    the remainder pre-loaded with the seconds of every class too small to list
    (`otherSeconds` in the weights file). Deterministic: ties by ordinal name
    and lowest shard index.

    The filters go on the command line, plain `--filter-class` /
    `--filter-not-class` the test executable reads like any other (no hook in
    DotCharter.Tests/TestEntryPoint.cs). The remainder's line is the longest -
    it names every listed class - and Windows caps a command line at 32,767
    characters, which `dotnet test` must then pass on to the testhost. So the
    plan lists only the heaviest classes whose names fit -MaxListChars
    (default 20,000); a lighter class beyond it joins the remainder. That is
    a cost in balance only, never in coverage.

    THE RELEASE LANE (Read-ReleaseLane, Get-TestLaneFilter,
    New-ReleaseLaneShardPlan): the heaviest corpus / integration / determinism
    classes are declared in tools/ci/release-lane.json, run by
    `Invoke-Gate.ps1 -ReleaseLane` (always in the merge train, the release
    preflight and CI) and left out of the default gate. See the section below.

    Runs under Windows PowerShell 5.1 and pwsh 7.
#>

Set-StrictMode -Off
Import-Module (Join-Path $PSScriptRoot 'HeadlessShards.psm1')

$script:TrxNs = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$script:Invariant = [Globalization.CultureInfo]::InvariantCulture

function Format-Seconds([double]$Value) { return [math]::Round($Value, 1).ToString($script:Invariant) }

<#
    Class -> summed case seconds and case count over one or more TRX files.
    Returns objects Class, Seconds, Cases sorted heaviest first (ties ordinal).
#>
function Read-TrxClassDurations([string[]]$TrxPaths) {
    $seconds = @{}; $cases = @{}
    foreach ($path in $TrxPaths) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        [xml]$doc = Get-Content -LiteralPath $path -Raw
        $nsm = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
        $nsm.AddNamespace('t', $script:TrxNs)
        $classOf = @{}
        foreach ($u in @($doc.SelectNodes('//t:TestDefinitions/t:UnitTest', $nsm))) {
            $m = $u.SelectSingleNode('t:TestMethod', $nsm)
            if ($m -and $m.GetAttribute('className')) { $classOf[$u.GetAttribute('id')] = $m.GetAttribute('className') }
        }
        foreach ($r in @($doc.SelectNodes('//t:Results/t:UnitTestResult', $nsm))) {
            $id = $r.GetAttribute('testId')
            $class = if ($id -and $classOf.ContainsKey($id)) { $classOf[$id] } else { Get-HeadlessClassName $r.GetAttribute('testName') }
            $d = [TimeSpan]::Zero
            $text = $r.GetAttribute('duration')
            if ($text) { [void][TimeSpan]::TryParse($text, $script:Invariant, [ref]$d) }
            if ($seconds.ContainsKey($class)) { $seconds[$class] += $d.TotalSeconds; $cases[$class]++ }
            else { $seconds[$class] = $d.TotalSeconds; $cases[$class] = 1 }
        }
    }
    $list = New-Object 'System.Collections.Generic.List[object]'
    foreach ($k in $seconds.Keys) { $list.Add([pscustomobject]@{ Class = [string]$k; Seconds = [double]$seconds[$k]; Cases = [int]$cases[$k] }) }
    $list.Sort([System.Comparison[object]] {
        param($a, $b)
        $c = $b.Seconds.CompareTo($a.Seconds)
        if ($c -ne 0) { return $c }
        return [string]::CompareOrdinal($a.Class, $b.Class)
    })
    return , $list.ToArray()
}

<#
    The top $Top classes: Rank, Class, Cases, Seconds, Share (percent of the
    summed time), plus the totals. Pure.
#>
function Get-ClassDurationSummary([object[]]$Durations, [int]$Top = 20) {
    $all = @($Durations | Where-Object { $_ })
    $total = 0.0; $caseCount = 0
    foreach ($d in $all) { $total += $d.Seconds; $caseCount += $d.Cases }
    $rows = @()
    $topSeconds = 0.0
    for ($i = 0; $i -lt [math]::Min($Top, $all.Count); $i++) {
        $d = $all[$i]
        $topSeconds += $d.Seconds
        $share = if ($total -gt 0) { 100.0 * $d.Seconds / $total } else { 0.0 }
        $rows += [pscustomobject]@{ Rank = $i + 1; Class = $d.Class; Cases = $d.Cases; Seconds = [math]::Round($d.Seconds, 1); Share = [math]::Round($share, 1) }
    }
    return [pscustomobject]@{
        Classes = $all.Count; Cases = $caseCount; TotalSeconds = [math]::Round($total, 1)
        TopShare = $(if ($total -gt 0) { [math]::Round(100.0 * $topSeconds / $total, 1) } else { 0.0 })
        Top = $rows
    }
}

# The summary as console lines (invariant numbers; the class without the
# DotCharter.Tests. prefix).
function Format-ClassDurationSummary($Summary) {
    $lines = @(('  slowest classes: top {0} of {1} = {2} % of {3} s summed case time ({4} cases)' -f `
        $Summary.Top.Count, $Summary.Classes, (Format-Seconds $Summary.TopShare), (Format-Seconds $Summary.TotalSeconds), $Summary.Cases))
    foreach ($r in $Summary.Top) {
        $name = ([string]$r.Class) -replace '^DotCharter\.Tests\.', ''
        $lines += ('    {0,2}. {1,7} s {2,5} %  {3} ({4})' -f $r.Rank, (Format-Seconds $r.Seconds), (Format-Seconds $r.Share), $name, $r.Cases)
    }
    return , $lines
}

# The summary and EVERY class's duration, as JSON (invariant numbers).
function Write-ClassDurationJson($Summary, [object[]]$Durations, [string]$Path) {
    $doc = [ordered]@{
        classes = $Summary.Classes; cases = $Summary.Cases; totalSeconds = $Summary.TotalSeconds; topShare = $Summary.TopShare
        top = @($Summary.Top | ForEach-Object { [ordered]@{ rank = $_.Rank; class = $_.Class; cases = $_.Cases; seconds = $_.Seconds; share = $_.Share } })
        all = @($Durations | Where-Object { $_ } | ForEach-Object { [ordered]@{ class = $_.Class; cases = $_.Cases; seconds = [math]::Round($_.Seconds, 2) } })
    }
    $json = ConvertTo-Json -InputObject $doc -Depth 4
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

<#
    The committed weights: Classes (class -> seconds) and OtherSeconds (the
    summed seconds of the classes too small to list). Missing file = none.
#>
function Read-GateShardWeights([string]$Path) {
    $classes = @{}; $other = 0.0
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        foreach ($p in $json.classes.PSObject.Properties) { $classes[$p.Name] = [double]$p.Value }
        if ($json.otherSeconds) { $other = [double]$json.otherSeconds }
    }
    return [pscustomobject]@{ Classes = $classes; OtherSeconds = $other }
}

<#
    Rewrites the weights file from Read-TrxClassDurations output: every class
    of at least $MinSeconds listed, the rest summed into otherSeconds. A stale
    file costs balance, never coverage (see New-GateShardPlan).
#>
function Write-GateShardWeights([object[]]$Durations, [string]$OutPath, [double]$MinSeconds = 1.0) {
    $listed = @{}; $other = 0.0
    foreach ($d in @($Durations | Where-Object { $_ })) {
        if ($d.Seconds -ge $MinSeconds) { $listed[$d.Class] = $d.Seconds } else { $other += $d.Seconds }
    }
    $names = [string[]]@($listed.Keys); [Array]::Sort($names, [StringComparer]::Ordinal)
    $lines = @()
    for ($i = 0; $i -lt $names.Count; $i++) {
        $sep = if ($i -lt $names.Count - 1) { ',' } else { '' }
        $lines += ('    "{0}": {1}{2}' -f $names[$i], (Format-Seconds $listed[$names[$i]]), $sep)
    }
    $header = @(
        '{',
        ('  "_comment": "Measured seconds per default-gate class (summed TRX case durations of a gate run), classes of at least {0} s; otherSeconds sums the rest. Balances tools/ci/GateShards.psm1; a stale file costs balance, never coverage. Regenerate, best as one process: tools/agent/Invoke-Gate.ps1 -Gate -Shards 1 -UpdateShardWeights",' -f (Format-Seconds $MinSeconds)),
        ('  "otherSeconds": {0},' -f (Format-Seconds $other)),
        '  "classes": {'
    )
    $text = (($header + $lines + @('  }', '}')) -join "`n") + "`n"
    [System.IO.File]::WriteAllText($OutPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

<#
    The exact-cover partition (see the module help). $Weights: class ->
    seconds. $OtherSeconds: the unlisted classes' seconds, pre-loaded on the
    remainder. $Exclude: the gate's own --filter-not-class patterns. Returns
    one object per non-empty shard: Index, Remainder, Classes (the classes it
    was dealt; for the remainder informational - it runs by exclusion), Weight
    (estimated seconds) and CommandArgs (its filter arguments). $MaxListChars
    caps the characters of the listed class names together (see the help).
#>
function New-GateShardPlan([System.Collections.IDictionary]$Weights, [double]$OtherSeconds, [int]$ShardCount, [string[]]$Exclude,
        [int]$MaxListChars = 20000) {
    if ($ShardCount -lt 1) { throw "ShardCount must be at least 1 (got $ShardCount)." }
    $excludeArgs = @(); foreach ($p in $Exclude) { $excludeArgs += @('--filter-not-class', $p) }
    if ($ShardCount -eq 1) {
        return @([pscustomobject]@{ Index = 1; Remainder = $true; Classes = [string[]]@(); Weight = [double]$OtherSeconds; CommandArgs = [string[]]$excludeArgs })
    }

    $items = New-Object 'System.Collections.Generic.List[object]'
    foreach ($class in @($Weights.Keys)) {
        $name = [string]$class
        $excluded = $false
        foreach ($p in $Exclude) { if ($name -like $p) { $excluded = $true } }
        # A class the gate leaves out is never listed: a listed shard must stay inside the gate.
        if (-not $excluded) { $items.Add([pscustomobject]@{ Name = $name; Weight = [double]$Weights[$class] }) }
    }
    $items.Sort([System.Comparison[object]] {
        param($a, $b)
        $c = $b.Weight.CompareTo($a.Weight)
        if ($c -ne 0) { return $c }
        return [string]::CompareOrdinal($a.Name, $b.Name)
    })

    $shards = @()
    for ($i = 0; $i -lt $ShardCount; $i++) {
        $shards += [pscustomobject]@{ Index = $i + 1; Weight = 0.0; Classes = (New-Object 'System.Collections.Generic.List[string]') }
    }
    $remainder = $shards[$ShardCount - 1]
    $remainder.Weight = [math]::Max(0.0, $OtherSeconds)
    # Heaviest first until the names no longer fit one command line; the rest
    # run in the remainder, by exclusion.
    $chars = 0; $fit = New-Object 'System.Collections.Generic.List[object]'
    foreach ($item in $items) {
        $cost = $item.Name.Length + ' --filter-not-class '.Length
        if ($chars + $cost -le $MaxListChars) { $fit.Add($item); $chars += $cost } else { $remainder.Weight += $item.Weight }
    }
    $items = $fit
    foreach ($item in $items) {
        $target = $shards[0]
        foreach ($s in $shards) { if ($s.Weight -lt $target.Weight) { $target = $s } }  # strict: lowest index wins a tie
        $target.Classes.Add($item.Name)
        $target.Weight += $item.Weight
    }

    $plan = @()
    $listed = New-Object 'System.Collections.Generic.List[string]'
    foreach ($s in $shards) {
        if ($s -eq $remainder -or $s.Classes.Count -eq 0) { continue }
        $sorted = $s.Classes.ToArray(); [Array]::Sort($sorted, [StringComparer]::Ordinal)
        $filter = @($excludeArgs); foreach ($c in $sorted) { $filter += @('--filter-class', $c); $listed.Add($c) }
        $plan += [pscustomobject]@{ Index = $s.Index; Remainder = $false; Classes = [string[]]$sorted; Weight = $s.Weight; CommandArgs = [string[]]$filter }
    }
    $sortedListed = $listed.ToArray(); [Array]::Sort($sortedListed, [StringComparer]::Ordinal)
    $filter = @($excludeArgs); foreach ($c in $sortedListed) { $filter += @('--filter-not-class', $c) }
    $own = $remainder.Classes.ToArray(); [Array]::Sort($own, [StringComparer]::Ordinal)
    $plan += [pscustomobject]@{ Index = $remainder.Index; Remainder = $true; Classes = [string[]]$own; Weight = $remainder.Weight; CommandArgs = [string[]]$filter }
    return $plan
}

<#
    THE RELEASE LANE (tools/ci/release-lane.json). The slow corpus / integration
    / determinism classes leave the default gate and run in their own lane,
    which the merge train, the release preflight and CI always run. The JSON is
    the ONE declared list; the two lanes' filters are both derived from it here,
    so a class is in exactly one of them whatever the file says:
      * default lane  = `--filter-not-class *UiPerfSmokeTests` plus one
        `--filter-not-class` per release class (what a plain `dotnet test`
        runs too, DotCharter.Tests/TestEntryPoint.cs);
      * release lane  = one `--filter-class` per release class.
    Infrastructure/ReleaseLaneLintTests replays both over every test class of
    the assembly (with the headless and golden lanes) and fails a class that
    runs in none or in two.
#>

# The list: Classes (class -> measured seconds, ordinal order) and Why (class ->
# reason). A missing or unreadable file THROWS: a lane nobody can read is never
# "empty", and the gate must not silently run the old, longer set.
function Read-ReleaseLane([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { throw "The release lane list is missing: $Path" }
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $names = [string[]]@($json.classes.PSObject.Properties | ForEach-Object { $_.Name })
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $classes = [ordered]@{}; $why = @{}
    foreach ($n in $names) {
        $entry = $json.classes.$n
        $classes[$n] = [double]$entry.seconds
        $why[$n] = [string]$entry.why
    }
    return [pscustomobject]@{ Classes = $classes; Why = $why }
}

# The filter arguments of one lane: 'default' (the default gate) or 'release'.
function Get-TestLaneFilter([ValidateSet('default', 'release')][string]$Lane, [string[]]$ReleaseClasses, [string]$HeadlessPattern = '*UiPerfSmokeTests') {
    $args2 = @()
    if ($Lane -eq 'default') {
        $args2 += @('--filter-not-class', $HeadlessPattern)
        foreach ($c in $ReleaseClasses) { $args2 += @('--filter-not-class', $c) }
    }
    else { foreach ($c in $ReleaseClasses) { $args2 += @('--filter-class', $c) } }
    return , [string[]]$args2
}

<#
    The release lane's shards: every class named by an exact `--filter-class`,
    dealt longest-processing-time first (ties by ordinal name and lowest shard
    index); an empty shard is not returned. Same plan object shape as
    New-GateShardPlan (Index, Remainder = $false, Classes, Weight, CommandArgs).
#>
function New-ReleaseLaneShardPlan([System.Collections.IDictionary]$Weights, [int]$ShardCount) {
    if ($ShardCount -lt 1) { throw "ShardCount must be at least 1 (got $ShardCount)." }
    $items = New-Object 'System.Collections.Generic.List[object]'
    foreach ($class in @($Weights.Keys)) { $items.Add([pscustomobject]@{ Name = [string]$class; Weight = [double]$Weights[$class] }) }
    $items.Sort([System.Comparison[object]] {
        param($a, $b)
        $c = $b.Weight.CompareTo($a.Weight)
        if ($c -ne 0) { return $c }
        return [string]::CompareOrdinal($a.Name, $b.Name)
    })
    $shards = @()
    for ($i = 0; $i -lt $ShardCount; $i++) {
        $shards += [pscustomobject]@{ Index = $i + 1; Weight = 0.0; Classes = (New-Object 'System.Collections.Generic.List[string]') }
    }
    foreach ($item in $items) {
        $target = $shards[0]
        foreach ($s in $shards) { if ($s.Weight -lt $target.Weight) { $target = $s } }
        $target.Classes.Add($item.Name)
        $target.Weight += $item.Weight
    }
    $plan = @()
    foreach ($s in $shards) {
        if ($s.Classes.Count -eq 0) { continue }
        $sorted = $s.Classes.ToArray(); [Array]::Sort($sorted, [StringComparer]::Ordinal)
        $plan += [pscustomobject]@{ Index = $s.Index; Remainder = $false; Classes = [string[]]$sorted; Weight = $s.Weight
            CommandArgs = (Get-TestLaneFilter 'release' $sorted) }
    }
    return $plan
}

# Rewrites the `seconds` of every class a run measured; the reasons and the
# set of classes are the file's own and never change here.
function Write-ReleaseLaneWeights([object[]]$Durations, [string]$Path) {
    $measured = @{}
    foreach ($d in @($Durations | Where-Object { $_ })) { $measured[$d.Class] = $d.Seconds }
    $doc = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $lane = Read-ReleaseLane $Path
    $lines = @()
    $names = @($lane.Classes.Keys)
    for ($i = 0; $i -lt $names.Count; $i++) {
        $n = $names[$i]
        $seconds = if ($measured.ContainsKey($n)) { $measured[$n] } else { $lane.Classes[$n] }
        $sep = if ($i -lt $names.Count - 1) { ',' } else { '' }
        $lines += ('    {0}: {{' -f (ConvertTo-Json -InputObject $n -Compress))
        $lines += ('      "seconds": {0},' -f (Format-Seconds $seconds))
        $lines += ('      "why": {0}' -f (ConvertTo-Json -InputObject $lane.Why[$n] -Compress))
        $lines += ('    }}{0}' -f $sep)
    }
    $head = @('{', ('  "_comment": {0},' -f (ConvertTo-Json -InputObject ([string]$doc._comment) -Compress)), '  "classes": {')
    $text = (($head + $lines + @('  }', '}')) -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# The names a `dotnet test --list-tests` log lists (the runner indents each by
# two spaces; the banners around them are localized and are not).
function Read-TestListingNames([string]$LogPath) {
    $names = @()
    foreach ($line in (Get-Content -LiteralPath $LogPath)) { if ($line -match '^  (\S.*)$') { $names += $Matches[1].TrimEnd() } }
    return , [string[]]$names
}

<#
    The parity check, pure: do the shards' listings together equal the whole
    gate's listing, case for case (a multiset - a theory lists one name per
    row)? Returns Equal, WholeCount, PartsCount, Missing (in the gate, in no
    shard), Extra (in a shard, not in the gate) and Duplicated (more often in
    the shards than in the gate).
#>
function Compare-TestListings([string[]]$Whole, [object[]]$Parts) {
    $count = @{}
    foreach ($n in $Whole) { if ($count.ContainsKey($n)) { $count[$n]++ } else { $count[$n] = 1 } }
    $partsCount = 0
    $seen = @{}
    foreach ($part in $Parts) {
        foreach ($n in @($part)) {
            if (-not $n) { continue }
            $partsCount++
            if ($seen.ContainsKey($n)) { $seen[$n]++ } else { $seen[$n] = 1 }
        }
    }
    $missing = @(); $extra = @(); $dup = @()
    foreach ($n in $count.Keys) {
        $got = if ($seen.ContainsKey($n)) { $seen[$n] } else { 0 }
        if ($got -eq 0) { $missing += $n } elseif ($got -gt $count[$n]) { $dup += $n } elseif ($got -lt $count[$n]) { $missing += $n }
    }
    foreach ($n in $seen.Keys) { if (-not $count.ContainsKey($n)) { $extra += $n } }
    $sort = { param($a) $x = [string[]]@($a); [Array]::Sort($x, [StringComparer]::Ordinal); , $x }
    return [pscustomobject]@{
        Equal = ($missing.Count -eq 0 -and $extra.Count -eq 0 -and $dup.Count -eq 0 -and $partsCount -eq $Whole.Count)
        WholeCount = $Whole.Count; PartsCount = $partsCount
        Missing = (& $sort $missing); Extra = (& $sort $extra); Duplicated = (& $sort $dup)
    }
}

<#
    Run the gate shards and the headless shards at the same time, or one after
    the other? Concurrent only when the machine has at least $MinFreeGB of free
    physical memory before they start (the headless shards alone peak around
    5-7 GB for four). Pure: the caller passes the free bytes.
#>
function Get-GateConcurrencyDecision([long]$FreeBytes, [double]$MinFreeGB) {
    $free = $FreeBytes / 1GB
    $text = '{0} GB free, {1} GB needed' -f (Format-Seconds $free), (Format-Seconds $MinFreeGB)
    if ($free -ge $MinFreeGB) { return [pscustomobject]@{ Concurrent = $true; Reason = "concurrent: $text" } }
    return [pscustomobject]@{ Concurrent = $false; Reason = "sequential: $text" }
}

function Get-FreePhysicalMemoryBytes {
    $os = Get-CimInstance Win32_OperatingSystem -Property FreePhysicalMemory
    return [long]$os.FreePhysicalMemory * 1KB
}

Export-ModuleMember -Function Read-TrxClassDurations, Get-ClassDurationSummary, Format-ClassDurationSummary, `
    Write-ClassDurationJson, Read-GateShardWeights, Write-GateShardWeights, New-GateShardPlan, `
    Read-TestListingNames, Compare-TestListings, Get-GateConcurrencyDecision, Get-FreePhysicalMemoryBytes, `
    Read-ReleaseLane, Get-TestLaneFilter, New-ReleaseLaneShardPlan, Write-ReleaseLaneWeights
