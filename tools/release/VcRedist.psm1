# The app-local Visual C++ runtime the release ships beside onnxruntime.dll.
#
# onnxruntime.dll and onnxruntime_providers_shared.dll come from NuGet, are built
# against the dynamic C++ runtime and cannot be relinked. Microsoft permits shipping
# the redistributable's x64 CRT DLLs app-local, so the release copies exactly the four
# onnxruntime imports (tools/release/vcredist.json: pinned version + SHA-256 per file)
# from the installed Visual Studio's VC\Redist\MSVC\<version>\x64\<folder>\ into the
# payload. A file that differs from its pin fails the release, like the other pinned
# tools. A verified copy is kept in the release cache (ReleaseCache.psm1) so a later
# release still works once a Visual Studio update has removed the pinned version.
#
# Tested by DotCharter.Tests/Infrastructure/VcRedistTests (fixture folders, no VS needed).

# No -Force: a forced nested import would reload the caller's own ReleaseCache copy.
Import-Module (Join-Path $PSScriptRoot 'ReleaseCache.psm1')

function Read-VcRedistPin {
    param([Parameter(Mandatory)][string]$Path)
    $pin = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $files = [ordered]@{}
    foreach ($property in $pin.files.PSObject.Properties) { $files[$property.Name] = $property.Value.ToLowerInvariant() }
    if (-not $pin.version -or -not $pin.folder -or $files.Count -eq 0) { throw "$Path must name version, folder and files." }
    return [pscustomobject]@{ Version = $pin.version; Folder = $pin.folder; Files = $files }
}

# The release-cache key: a SHA-256 over the pinned names and hashes, so changing the pin changes the key.
function Get-VcRedistKey {
    param([Parameter(Mandatory)]$Pin)
    $text = ($Pin.Files.Keys | Sort-Object | ForEach-Object { "$_=$($Pin.Files[$_])" }) -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("$($Pin.Version)`n$text")
    return ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($bytes)) -replace '-', '').ToLowerInvariant()
}

# Names every pinned file that is missing from $Dir or hashes to something else; $null when all match.
function Get-VcRedistMismatch {
    param([Parameter(Mandatory)]$Pin, [Parameter(Mandatory)][string]$Dir)
    $problems = foreach ($name in $Pin.Files.Keys) {
        $path = Join-Path $Dir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { "$name is missing"; continue }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Pin.Files[$name]) { "$name has SHA-256 $actual, the pin is $($Pin.Files[$name])" }
    }
    if ($problems) { return ($problems -join '; ') }
    return $null
}

# The pinned redistributable folder inside any installed Visual Studio / Build Tools, or $null.
function Find-VcRedistSourceDir {
    param([Parameter(Mandatory)]$Pin, [string[]]$InstallationPaths)
    if (-not $InstallationPaths) {
        $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
        $InstallationPaths = @(& $vswhere -all -products '*' -property installationPath)
    }
    foreach ($root in $InstallationPaths) {
        $candidate = Join-Path $root "VC\Redist\MSVC\$($Pin.Version)\x64\$($Pin.Folder)"
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    return $null
}

# Copies the pinned DLLs into $Destination, each verified against the pin. Source order: the
# installed Visual Studio, then the release cache. Throws when neither holds the pinned bytes.
# Returns where they came from.
function Install-VcRedist {
    param([Parameter(Mandatory)]$Pin, [Parameter(Mandatory)][string]$Destination, [string[]]$InstallationPaths)
    $key = Get-VcRedistKey -Pin $Pin
    $names = @($Pin.Files.Keys)
    $why = @()
    $source = Find-VcRedistSourceDir -Pin $Pin -InstallationPaths $InstallationPaths
    if ($source) {
        $mismatch = Get-VcRedistMismatch -Pin $Pin -Dir $source
        if (-not $mismatch) {
            $cacheDir = Get-RedistCacheDir -Tool 'vcredist' -Key $key
            if (-not (Test-Path -LiteralPath $cacheDir)) { Save-RedistToCache -Tool 'vcredist' -Key $key -Source $source -Files $names | Out-Null }
            New-Item -ItemType Directory -Force -Path $Destination | Out-Null
            foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $Destination $name) -Force }
            return $source
        }
        $why += "the installed copy $source differs from the pin: $mismatch"
    }
    else { $why += "no installed Visual Studio carries VC\Redist\MSVC\$($Pin.Version)\x64\$($Pin.Folder)" }

    $cacheDir = Get-RedistCacheDir -Tool 'vcredist' -Key $key
    if (Copy-RedistFromCache -Tool 'vcredist' -Key $key -Destination $Destination -Files $names) {
        $mismatch = Get-VcRedistMismatch -Pin $Pin -Dir $Destination
        if (-not $mismatch) { return $cacheDir }
        Remove-RedistCacheEntry -Tool 'vcredist' -Key $key
        $why += "the release cache copy differs from the pin: $mismatch (dropped)"
    }
    else { $why += 'the release cache holds no copy' }

    foreach ($name in $names) { Remove-Item -LiteralPath (Join-Path $Destination $name) -Force -ErrorAction SilentlyContinue }
    throw "The pinned Visual C++ runtime $($Pin.Version) is unavailable (tools/release/vcredist.json): $($why -join '; '). " +
          'Install the Visual Studio Build Tools C++ workload at that version, or re-pin after checking the new files.'
}

Export-ModuleMember -Function Read-VcRedistPin, Get-VcRedistKey, Get-VcRedistMismatch, Find-VcRedistSourceDir, Install-VcRedist
