# Downloads a file pinned by SHA-256 from an ordered list of mirrors (#449).
# Every mirror is held to the SAME pin: a mirror that serves other bytes (an
# HTML interstitial, a truncated file, a different release) is skipped and the
# next one is tried. Bytes land in "<Dest>.partial" and are moved into place
# only after they hash to the pin, so nothing unverified ever reaches the cache.
# Used by Build-MinimalFfmpeg.ps1; tested by
# DotCharter.Tests/Infrastructure/PinnedDownloadMirrorTests with a fake
# downloader (no network).

# The real downloader: one URL to one file, throwing on any HTTP failure.
$script:WebDownload = {
    param([string]$Url, [string]$OutFile)
    $prev = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing } finally { $ProgressPreference = $prev }
}

function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# Makes sure $Dest holds the bytes $Sha256 pins, trying $Urls in order. Returns
# the URL that served the file, or 'cache' when $Dest already matched. Throws,
# naming every mirror and why it was refused, when none serves the pinned bytes.
# $Download is (Url, OutFile) -> writes OutFile; tests inject a fake.
function Save-PinnedFile {
    param(
        [Parameter(Mandatory)][string[]]$Urls,
        [Parameter(Mandatory)][string]$Dest,
        [Parameter(Mandatory)][string]$Sha256,
        [scriptblock]$Download = $script:WebDownload
    )
    $pin = $Sha256.ToLowerInvariant()
    $name = Split-Path -Leaf $Dest
    if (Test-Path -LiteralPath $Dest) {
        if ((Get-Sha256 $Dest) -eq $pin) { Write-Host "  $name (cached)"; return 'cache' }
        # A cached file that fails its pin is not a cache entry; it never survives.
        Remove-Item -LiteralPath $Dest -Force
    }

    $tmp = "$Dest.partial"
    $refusals = [System.Collections.Generic.List[string]]::new()
    foreach ($url in $Urls) {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
        Write-Host "  downloading $url"
        try { & $Download $url $tmp }
        catch {
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
            $refusals.Add("$url : $($_.Exception.Message)")
            Write-Warning "  $name : $url failed ($($_.Exception.Message)); trying the next mirror."
            continue
        }
        if (-not (Test-Path -LiteralPath $tmp)) {
            $refusals.Add("$url : wrote no file")
            continue
        }
        $actual = Get-Sha256 $tmp
        if ($actual -ne $pin) {
            Remove-Item -LiteralPath $tmp -Force
            $refusals.Add("$url : SHA-256 $actual")
            Write-Warning "  $name : $url served SHA-256 $actual, expected $pin; trying the next mirror."
            continue
        }
        Move-Item -LiteralPath $tmp $Dest -Force
        Write-Host "  $name <- $url"
        return $url
    }
    throw "No mirror served $name with SHA-256 $pin :`n  $($refusals -join "`n  ")"
}

Export-ModuleMember -Function Save-PinnedFile
