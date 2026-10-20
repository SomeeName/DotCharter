# The prebuilt native runtime (#832): juce_sharp.dll + juce_vst_host.exe, a
# matched pair built from ONE JuceSharp commit, published as the release asset
# juce-sharp-native-win-x64-<first 12 of the gitlink>.zip and pinned by
# tools/setup/native-runtime.json (SHA-256 of each file inside the zip + the
# DCA_ABI_VERSION it reports). Nothing unverified reaches the runtimes folder:
# the zip is unpacked into a fresh temp folder, each file is hashed against the
# pin, the DLL's dca_abi_version() is compared with the header, and only then
# are the two files moved into place (the DLL last, because its presence is
# what turns the fetch off).
#
# Used by Get-NativeRuntime.ps1 (called by the build when the DLL is missing)
# and tools/release/Publish-NativeRuntime.ps1. Tested by
# DotCharter.Tests/Infrastructure/NativeRuntimeFetchTests with fixture zips and
# a fake downloader (no network). Runs under Windows PowerShell 5.1 and pwsh 7.

$script:PairFiles = @('juce_sharp.dll', 'juce_vst_host.exe')

# Overrides the network: a folder that already holds the asset zip. Lets the
# publisher (and a test of the build wiring) install a zip that is not uploaded yet.
$script:AssetDirVariable = 'DOTCHARTER_NATIVE_ASSET_DIR'

function Get-NativeRuntimePairFiles { return $script:PairFiles }

# The release asset that carries the pair built from JuceSharp $Commit.
function Get-NativeRuntimeAssetName {
    param([Parameter(Mandatory)][string]$Commit)
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw "Not a full lower-case 40-hex commit id: '$Commit'." }
    return 'juce-sharp-native-win-x64-' + $Commit.Substring(0, 12) + '.zip'
}

# The JuceSharp commit the checkout's HEAD records (the gitlink), not whatever the
# submodule folder happens to have checked out.
function Get-JuceSharpGitlink {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $line = & git -C $RepoRoot ls-tree HEAD JuceSharp 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $line) { throw "git ls-tree HEAD JuceSharp found no gitlink in $RepoRoot." }
    if ("$line" -match '^160000 commit ([0-9a-f]{40})\s') { return $Matches[1] }
    throw "git ls-tree HEAD JuceSharp did not return a gitlink: '$line'."
}

function Get-HeaderAbiVersion {
    param([Parameter(Mandatory)][string]$Header)
    if (-not (Test-Path -LiteralPath $Header)) { throw "The native header is missing: $Header (is the JuceSharp submodule fetched?)." }
    $m = Select-String -LiteralPath $Header -Pattern '^\s*#define\s+DCA_ABI_VERSION\s+(\d+)' | Select-Object -First 1
    if (-not $m) { throw "No '#define DCA_ABI_VERSION' in $Header." }
    return [int]$m.Matches[0].Groups[1].Value
}

# .NET, not Get-FileHash: Windows PowerShell started from a pwsh session (the
# build's Exec) inherits pwsh's PSModulePath and cannot autoload that cmdlet.
function Get-FileSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Read-NativeRuntimeManifest {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "The native runtime manifest is missing: $Path." }
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $manifest.repository -or -not $manifest.release) { throw "$Path names no 'repository' or 'release'." }
    return $manifest
}

# The manifest entry pinning $Commit's pair. Throws when there is none: an
# unpinned download is never installed.
function Get-NativeRuntimeEntry {
    param([Parameter(Mandatory)]$Manifest, [Parameter(Mandatory)][string]$Commit)
    $entry = @($Manifest.runtimes | Where-Object { $_.commit -eq $Commit }) | Select-Object -First 1
    if (-not $entry) {
        throw ("No prebuilt native runtime is pinned for JuceSharp $Commit in tools/setup/native-runtime.json. " +
            'Build the native engine (docs/dev/GETTING-STARTED.md), or publish the pair with tools/release/Publish-NativeRuntime.ps1.')
    }
    foreach ($file in $script:PairFiles) {
        $pin = $entry.files.$file
        if ("$pin" -cnotmatch '^[0-9a-f]{64}$') { throw "native-runtime.json pins no lower-case SHA-256 for $file of JuceSharp $Commit." }
    }
    return $entry
}

# Loads the DLL only to call dca_abi_version(), then unloads it.
function Read-DllAbiVersion {
    param([Parameter(Mandatory)][string]$DllPath)
    if (-not ('DotCharterNativeRuntime.Kernel32' -as [type])) {
        Add-Type -Namespace DotCharterNativeRuntime -Name Kernel32 -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr LoadLibraryW(string path);
[System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
public static extern System.IntPtr GetProcAddress(System.IntPtr module, string name);
[System.Runtime.InteropServices.DllImport("kernel32")]
public static extern bool FreeLibrary(System.IntPtr module);
'@
    }
    if (-not ('DotCharterNativeRuntimeAbiFn' -as [type])) {
        Add-Type -TypeDefinition @'
[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
public delegate uint DotCharterNativeRuntimeAbiFn();
'@
    }
    $module = [DotCharterNativeRuntime.Kernel32]::LoadLibraryW($DllPath)
    if ($module -eq [IntPtr]::Zero) {
        $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Could not load $DllPath (Win32 error $code) to read its ABI version. Is the Microsoft Visual C++ runtime installed?"
    }
    try {
        $proc = [DotCharterNativeRuntime.Kernel32]::GetProcAddress($module, 'dca_abi_version')
        if ($proc -eq [IntPtr]::Zero) { throw "$DllPath exports no dca_abi_version()." }
        $fn = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($proc, [DotCharterNativeRuntimeAbiFn])
        return [int]$fn.Invoke()
    }
    finally { [void][DotCharterNativeRuntime.Kernel32]::FreeLibrary($module) }
}

# The real downloader: writes $OutDir\$Asset. A local asset folder wins; else the
# GitHub CLI (it reads a PRIVATE repository with the user's login); else plain
# HTTPS (enough once the repository is public).
$script:DefaultDownload = {
    param([string]$Repository, [string]$Release, [string]$Asset, [string]$OutDir)
    $out = Join-Path $OutDir $Asset
    $local = [Environment]::GetEnvironmentVariable($script:AssetDirVariable)
    if ($local) {
        $source = Join-Path $local $Asset
        if (-not (Test-Path -LiteralPath $source)) { throw "$($script:AssetDirVariable) is set, but $source does not exist." }
        Copy-Item -LiteralPath $source -Destination $out
        return
    }
    $refusals = @()
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $text = & gh release download $Release -R $Repository -p $Asset -D $OutDir 2>&1
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $out)) { return }
        $refusals += "gh release download $Release -R $Repository : exit $LASTEXITCODE $(($text | Out-String).Trim())"
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
    }
    else { $refusals += 'gh (GitHub CLI) is not on PATH' }
    $url = "https://github.com/$Repository/releases/download/$Release/$Asset"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $prev = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing } finally { $ProgressPreference = $prev }
        return
    }
    catch {
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
        $refusals += "$url : $($_.Exception.Message)"
    }
    throw ("Could not download $Asset. " + ($refusals -join '; ') +
        ". While $Repository is private, install the GitHub CLI and run 'gh auth login' with an account that can read it.")
}

# Downloads, verifies and installs the pair pinned for $Commit into $Destination.
# Throws - leaving $Destination untouched - on a missing pin, a manifest ABI that
# is not the header's, a missing file in the zip, a SHA-256 mismatch or a DLL
# reporting another ABI. $Download is (Repository, Release, Asset, OutDir) and
# $ReadAbi is (DllPath) -> int; tests inject fakes.
function Install-NativeRuntime {
    param(
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][int]$HeaderAbi,
        [Parameter(Mandatory)][string]$Destination,
        [scriptblock]$Download = $script:DefaultDownload,
        [scriptblock]$ReadAbi = ${function:Read-DllAbiVersion}
    )
    $manifest = Read-NativeRuntimeManifest $ManifestPath
    $entry = Get-NativeRuntimeEntry -Manifest $manifest -Commit $Commit
    if ([int]$entry.abi -ne $HeaderAbi) {
        throw "native-runtime.json says the pair for JuceSharp $Commit reports ABI $($entry.abi), but the header says DCA_ABI_VERSION $HeaderAbi."
    }
    $asset = Get-NativeRuntimeAssetName $Commit

    $stage = Join-Path ([IO.Path]::GetTempPath()) ('dotcharter-native-' + [guid]::NewGuid().ToString('N'))
    $zipDir = Join-Path $stage 'zip'
    $unpacked = Join-Path $stage 'files'
    New-Item -ItemType Directory -Path $zipDir, $unpacked -Force | Out-Null
    try {
        & $Download $manifest.repository $manifest.release $asset $zipDir
        $zip = Join-Path $zipDir $asset
        if (-not (Test-Path -LiteralPath $zip)) { throw "The download wrote no $asset." }

        # Only the two pinned entries, by exact name - no other path in the zip is ever written.
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            foreach ($file in $script:PairFiles) {
                $zipEntry = $archive.Entries | Where-Object { $_.FullName -eq $file } | Select-Object -First 1
                if (-not $zipEntry) { throw "$asset has no $file at its root." }
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($zipEntry, (Join-Path $unpacked $file))
            }
        }
        finally { $archive.Dispose() }

        foreach ($file in $script:PairFiles) {
            $actual = Get-FileSha256 (Join-Path $unpacked $file)
            $pin = $entry.files.$file
            if ($actual -ne $pin) {
                throw "SHA-256 mismatch for $file in $asset : got $actual, native-runtime.json pins $pin. This is not the published runtime; nothing was installed."
            }
        }

        $dllAbi = & $ReadAbi (Join-Path $unpacked 'juce_sharp.dll')
        if ([int]$dllAbi -ne $HeaderAbi) {
            throw "juce_sharp.dll from $asset reports dca_abi_version() $dllAbi, but the header says DCA_ABI_VERSION $HeaderAbi; nothing was installed."
        }

        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        foreach ($file in @('juce_vst_host.exe', 'juce_sharp.dll')) {
            $partial = Join-Path $Destination "$file.partial"
            Copy-Item -LiteralPath (Join-Path $unpacked $file) -Destination $partial -Force
            Move-Item -LiteralPath $partial -Destination (Join-Path $Destination $file) -Force
        }
        return [pscustomobject]@{ Asset = $asset; Abi = $HeaderAbi; Destination = $Destination }
    }
    finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

Export-ModuleMember -Function Get-NativeRuntimePairFiles, Get-NativeRuntimeAssetName, Get-JuceSharpGitlink, Get-HeaderAbiVersion,
    Get-FileSha256, Read-NativeRuntimeManifest, Get-NativeRuntimeEntry, Read-DllAbiVersion, Install-NativeRuntime
