# The ffmpeg component inventory (tools/release/ffmpeg-min/components.json):
# load it, turn it into configure flags, and check a built ffmpeg.exe against
# it. Shared by Build-MinimalFfmpeg.ps1 (configure) and New-Release.ps1
# (preflight), so the two can never disagree about what "complete" means.

$script:InventoryPath = Join-Path $PSScriptRoot 'ffmpeg-min/components.json'

# Component kinds ffmpeg lists with a CLI flag, mapped to configure's singular.
$script:ListedKinds = [ordered]@{
    decoders  = 'decoder'
    encoders  = 'encoder'
    muxers    = 'muxer'
    demuxers  = 'demuxer'
    filters   = 'filter'
    protocols = 'protocol'
    bsfs      = 'bsf'
}

function Get-FfmpegInventory([string]$Path = $script:InventoryPath) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

# The --enable-<kind>=a,b,c flags for every kind in the inventory (parsers
# included; ffmpeg has no CLI listing for those, so they are checked through
# -buildconf instead).
function Get-FfmpegConfigureComponentArgs($Inventory) {
    $kinds = [ordered]@{}
    foreach ($k in $script:ListedKinds.Keys) { $kinds[$k] = $script:ListedKinds[$k] }
    $kinds['parsers'] = 'parser'
    $flags = foreach ($k in $kinds.Keys) {
        $names = @($Inventory.$k)
        if ($names.Count -gt 0) { "--enable-$($kinds[$k])=$(($names | Sort-Object -Unique) -join ',')" }
    }
    return @($flags)
}

# How many components the inventory names, external libraries included.
function Get-FfmpegInventoryCount($Inventory) {
    $n = @($Inventory.externalLibraries).Count + @($Inventory.parsers).Count
    foreach ($k in $script:ListedKinds.Keys) { $n += @($Inventory.$k).Count }
    return $n
}

# Component names out of one `ffmpeg -hide_banner -<kind>` listing. Pure, so
# New-Release.ps1 -SelfTest can pin it against canned text.
function ConvertFrom-FfmpegListing([string]$Kind, [string[]]$Lines) {
    $names = [System.Collections.Generic.List[string]]::new()
    switch ($Kind) {
        { $_ -in 'decoders', 'encoders' } {
            $body = $false
            foreach ($l in $Lines) {
                if ($l -match '^\s*-{3,}\s*$') { $body = $true; continue }
                if (-not $body) { continue }
                $t = -split $l
                if ($t.Count -ge 2) { $names.Add($t[1]) }
            }
        }
        { $_ -in 'muxers', 'demuxers' } {
            $body = $false
            foreach ($l in $Lines) {
                if ($l -match '^\s*-{2,}\s*$') { $body = $true; continue }
                if (-not $body) { continue }
                $t = -split $l
                if ($t.Count -ge 2) { foreach ($n in $t[1].Split(',')) { $names.Add($n) } }
            }
        }
        'filters' {
            foreach ($l in $Lines) {
                $t = -split $l
                if ($t.Count -ge 3 -and $t[2] -match '->') { $names.Add($t[1]) }
            }
        }
        'protocols' {
            foreach ($l in $Lines) {
                $s = $l.Trim()
                if ($s -and -not $s.EndsWith(':')) { $names.Add($s) }
            }
        }
        'bsfs' {
            foreach ($l in $Lines) {
                $s = $l.Trim()
                if ($s -and -not $s.EndsWith(':')) { $names.Add($s) }
            }
        }
        default { throw "unknown ffmpeg listing kind '$Kind'." }
    }
    return @($names | Sort-Object -Unique)
}

# The names in one --enable-<kind>=a,b,c of `ffmpeg -buildconf`, which quotes
# the list ('a,b,c').
function Get-BuildConfList([string]$BuildConf, [string]$Kind) {
    $m = [regex]::Match($BuildConf, "--enable-$Kind=(\S+)")
    if (-not $m.Success) { return @() }
    return @($m.Groups[1].Value.Trim("'", '"').Split(','))
}

# Every inventory component the given listings / buildconf do NOT contain, as
# "<kind>:<name>" strings. Empty means complete. Pure.
function Compare-FfmpegInventory($Inventory, [hashtable]$Listings, [string]$BuildConf) {
    $missing = [System.Collections.Generic.List[string]]::new()
    # ffmpeg -demuxers HIDES the image-pipe demuxers (dds_pipe, png_pipe, ...),
    # so those, alone, are taken from the configure line instead.
    $pipes = Get-BuildConfList $BuildConf 'demuxer' | Where-Object { $_ -like '*_pipe' }
    foreach ($k in $script:ListedKinds.Keys) {
        $have = @($Listings[$k])
        if ($k -eq 'demuxers') { $have += $pipes }
        foreach ($n in @($Inventory.$k)) { if ($have -notcontains $n) { $missing.Add("${k}:$n") } }
    }
    # Parsers have no listing. A build that never disabled them has them all
    # (a stock full build); ours lists them in --enable-parser.
    $allParsers = $BuildConf -notmatch '--disable-(everything|parsers)'
    $parsers = Get-BuildConfList $BuildConf 'parser'
    foreach ($n in @($Inventory.parsers)) { if (-not $allParsers -and $parsers -notcontains $n) { $missing.Add("parsers:$n") } }
    foreach ($lib in @($Inventory.externalLibraries)) {
        if ($BuildConf -notmatch "--enable-$([regex]::Escape($lib))(\s|$)") { $missing.Add("externalLibraries:$lib") }
    }
    return @($missing)
}

# Runs $Exe for every listing and compares. Returns the missing components;
# throws when the exe does not run at all.
function Test-FfmpegInventory([string]$Exe, $Inventory = (Get-FfmpegInventory)) {
    $listings = @{}
    foreach ($k in $script:ListedKinds.Keys) {
        $out = @(& $Exe -hide_banner "-$k" 2>$null)
        if ($LASTEXITCODE -ne 0) { $global:LASTEXITCODE = 0; throw "'$Exe -$k' failed." }
        $listings[$k] = ConvertFrom-FfmpegListing $k $out
    }
    $buildConf = (@(& $Exe -hide_banner -buildconf 2>$null) -join "`n")
    $global:LASTEXITCODE = 0
    return Compare-FfmpegInventory $Inventory $listings $buildConf
}

Export-ModuleMember -Function Get-FfmpegInventory, Get-FfmpegInventoryCount, Get-FfmpegConfigureComponentArgs, ConvertFrom-FfmpegListing,
    Compare-FfmpegInventory, Test-FfmpegInventory
