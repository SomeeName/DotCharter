<#
.SYNOPSIS
    Creates a buildable DotCharter worktree under .claude/worktrees/<Name> on a
    new branch, with a plain-source copy of the JuceSharp submodule.

.DESCRIPTION
    Agent worktrees cannot initialise the JuceSharp submodule (the sandbox
    refuses `-c protocol.file.allow`), so this copies the main checkout's
    JuceSharp/ folder instead - excluding .git, build, bin, obj - and deletes
    the copied .git pointer so the folder is plain sources. The prebuilt
    juce_sharp.dll under JuceSharp/src/JuceSharp/runtimes/win-x64/native/
    comes with the copy, so DotCharter.sln builds without any submodule step.
    Such a worktree cannot commit to the submodule.

.PARAMETER Name
    Worktree folder name (also the default branch suffix).
.PARAMETER Branch
    Branch to create. Defaults to agent/<Name>.
.PARAMETER Base
    Commit to branch from. Defaults to main (NOT the session's HEAD, which can
    be stale - see "Check the merge-base" in AGENTS.md).
#>
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Branch = "agent/$Name",
    [string]$Base = 'main'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AgentWorktree.psm1') -Force

$repo = "$(git -C $PSScriptRoot rev-parse --show-toplevel)".Trim()
# If the script copy being run sits inside the submodule's tree, climb to the
# superproject; the git common dir then leads to the main checkout even when
# run from another worktree.
$super = "$(git -C $repo rev-parse --show-superproject-working-tree)".Trim()
if ($super) { $repo = $super }
$common = "$(git -C $repo rev-parse --path-format=absolute --git-common-dir)".Trim()
$main = Split-Path $common -Parent
if (-not (Test-Path (Join-Path $main 'DotCharter.sln'))) {
    throw "Main checkout not found (looked in $main) - run from the DotCharter repo, not JuceSharp."
}

$wt = Join-Path $main ".claude\worktrees\$Name"
if (Test-Path $wt) { throw "$wt already exists." }

# git reports progress on stderr, which Windows PowerShell 5.1 turns into a
# terminating error under 'Stop'; judge the exit code instead.
$ErrorActionPreference = 'Continue'
git -C $main worktree add -b $Branch $wt $Base 2>&1 | Out-Host
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed." }

$src = Join-Path $main 'JuceSharp'
$dst = Join-Path $wt 'JuceSharp'
robocopy $src $dst /E /XD .git build bin obj node_modules TestResults /XF .git /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)." }
$pointer = Join-Path $dst '.git'
if (Test-Path $pointer) { Remove-Item -Force -Recurse $pointer }

$dll = Join-Path $dst 'src\JuceSharp\runtimes\win-x64\native\juce_sharp.dll'
$nativeDir = Split-Path $dll
$nativeFiles = Get-JuceNativeRuntimeFiles
function Get-PresentNativeFiles { @($nativeFiles | Where-Object { Test-Path (Join-Path $nativeDir $_) }) }

# The copy above is the MAIN CHECKOUT's working tree: it lags the new worktree's
# base whenever the owner's checkout cannot fast-forward or nobody ran
# `git submodule update` after a gitlink bump (#56: a stale copy did not build),
# and even at the right HEAD it carries the owner's uncommitted edits (#613). So
# whenever the base names a gitlink, put that commit's EXACT tracked tree in
# place: fetch it into the main checkout's submodule repo (objects only - the
# owner's working tree is untouched) and mirror `git archive` of it over the
# copy, keeping the build folders and the untracked native pair.
$gitlink = "$(git -C $wt rev-parse "HEAD:JuceSharp")".Trim()
$copied = "$(git -C $src rev-parse HEAD)".Trim()
$plan = Get-JuceSourcePlan -Gitlink $gitlink -CopiedHead $copied
if ($plan.Extract) {
    $from = if ($copied -eq $gitlink) { 'has it checked out (its edits stay out)' } else { "is at $($copied.Substring(0, [Math]::Min(9, $copied.Length)))" }
    Write-Host "JuceSharp: the base needs $($gitlink.Substring(0, 9)), the main checkout $from - extracting that tree."
    # This invocation's own scratch folders: another worktree created at the same
    # time for the same gitlink must not delete or mirror this one's (#613).
    $tree = Get-JuceExtractDir -TempRoot ([IO.Path]::GetTempPath()) -Gitlink $gitlink -Token ([guid]::NewGuid().ToString('N'))
    $keepDir = "$tree.native"
    try {
        $ErrorActionPreference = 'Continue'
        git -C $src cat-file -e "$gitlink^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) { git -C $src fetch --quiet origin 2>&1 | Out-Host }
        New-Item -ItemType Directory -Path $tree | Out-Null
        $tar = Join-Path $tree 'src.tar'
        git -C $src archive --format=tar -o $tar $gitlink 2>&1 | Out-Host
        $archived = $LASTEXITCODE -eq 0
        $ErrorActionPreference = 'Stop'
        if (-not $archived) { throw "JuceSharp $gitlink is not in the main checkout's submodule repo even after a fetch - push it first." }
        # Windows' own bsdtar, by full path: a shell started from Git Bash puts GNU tar
        # first on PATH, which reads "C:\..." as a remote host and extracts nothing.
        & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $tar -C $tree
        Remove-Item $tar
        # Never mirror an empty or partial tree over the copy: /MIR would delete it.
        if (-not (Test-Path (Join-Path $tree 'src\JuceSharp.Native\include\dotcharter_audio.h'))) {
            throw "Extracting JuceSharp $gitlink produced no sources - the copy was left as it was."
        }
        # Mirror the tracked tree over the copy; /XD keeps (and never deletes) build
        # output folders, and the native PAIR is put back explicitly because /MIR
        # deletes untracked files (it used to keep only the DLL, #613).
        $before = @(Get-PresentNativeFiles)
        New-Item -ItemType Directory -Path $keepDir | Out-Null
        foreach ($file in $before) { Copy-Item (Join-Path $nativeDir $file) $keepDir }
        robocopy $tree $dst /MIR /XD build bin obj node_modules TestResults /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy (JuceSharp at the gitlink) failed ($LASTEXITCODE)." }
        if ($before.Count -gt 0) { New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null }
        foreach ($file in $before) { Copy-Item (Join-Path $keepDir $file) $nativeDir -Force }
        $lost = @(Get-JuceNativeRuntimeLoss -Before $before -After @(Get-PresentNativeFiles))
        if ($lost.Count -gt 0) { throw "The native runtime lost $($lost -join ', ') while JuceSharp was replaced - rebuild or fetch it." }
    }
    finally {
        $ErrorActionPreference = 'Stop'
        foreach ($scratch in @($tree, $keepDir)) {
            if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
        }
    }
}

# The DLL is untracked, so even the extracted tree above keeps the MAIN
# checkout's DLL - built for $copied, not for $gitlink. The same ABI NUMBER does
# not mean the same native code: on 2026-10-05 `rsamp` (#88) landed without an
# ABI bump, and four agent worktrees failed ~40 native-tone tests on a DLL that
# predated it while this script reported "ABI 41 / ABI 41". So look for a DLL
# actually built for $gitlink's native code: another worktree whose JuceSharp is
# a real git checkout at that commit - or at any commit whose native sources
# (src/JuceSharp.Native) are IDENTICAL to $gitlink's - with a DLL newer than its
# native sources. The second arm matters after a managed-only gitlink bump (#256
# moved 76047d6 -> 872b208 with no native change): no checkout sat at the new
# commit, so every new worktree fell back to the main checkout's older DLL and
# ~40 RsToneFidelityHarnessTests failed with "native op(s) failed".
$siblingFound = $false
if (-not $plan.MainDllFitsGitlink) {
    $ErrorActionPreference = 'Continue'
    $candidates = Get-ChildItem -Directory (Join-Path $main '.claude\worktrees') -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'JuceSharp' } |
        Where-Object { $_ -ne $dst -and (Test-Path (Join-Path $_ '.git')) }
    foreach ($c in $candidates) {
        $head = "$(git -C $c rev-parse HEAD 2>$null)".Trim()
        if (-not $head) { continue }
        if ($head -ne $gitlink) {
            # Same native code? Compare in a repo that has both commits: the main
            # checkout's JuceSharp (fetched for the gitlink) first, else the candidate.
            $same = $false
            foreach ($repo in @((Join-Path $main 'JuceSharp'), $c)) {
                git -C $repo cat-file -e "$gitlink^{commit}" 2>$null
                if ($LASTEXITCODE -ne 0) { continue }
                git -C $repo cat-file -e "$head^{commit}" 2>$null
                if ($LASTEXITCODE -ne 0) { continue }
                git -C $repo diff --quiet $head $gitlink -- src/JuceSharp.Native 2>$null
                $same = $LASTEXITCODE -eq 0
                break
            }
            if (-not $same) { continue }
        }
        # The right commit is not enough: an agent mid-change has uncommitted
        # native edits there (a DLL built from them can even carry a newer ABI).
        $dirty = "$(git -C $c status --porcelain -- src/JuceSharp.Native 2>$null)".Trim()
        if ($dirty) { continue }
        $cDll = Join-Path $c 'src\JuceSharp\runtimes\win-x64\native\juce_sharp.dll'
        if (-not (Test-Path $cDll)) { continue }
        $vstHost = Join-Path (Split-Path $cDll) 'juce_vst_host.exe'
        if (-not (Test-Path $vstHost)) { continue }
        $newestSrc = Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue `
            (Join-Path $c 'src\JuceSharp.Native\src'), (Join-Path $c 'src\JuceSharp.Native\include') |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newestSrc -and (Get-Item $cDll).LastWriteTime -lt $newestSrc.LastWriteTime) { continue }
        New-Item -ItemType Directory -Force -Path (Split-Path $dll) | Out-Null
        Copy-Item $cDll $dll -Force
        Copy-Item $vstHost (Split-Path $dll) -Force
        Write-Host "juce_sharp.dll: taken from $c (built at JuceSharp $($head.Substring(0, 9)); native sources match $($gitlink.Substring(0, 9)))."
        $siblingFound = $true
        break
    }
    $ErrorActionPreference = 'Stop'
}

$nativeState = Get-JuceNativePairState -RepoRoot $wt -Gitlink $gitlink
$nativePlan = Get-JuceNativePairPlan -Gitlink $gitlink -CopiedHead $copied -SiblingFound $siblingFound `
    -ManifestEntry $nativeState.ManifestEntry -Hashes $nativeState.Hashes -FailureReason $nativeState.FailureReason
if ($nativePlan.Action -eq 'install pinned') {
    $installer = Join-Path $wt 'tools/setup/Get-NativeRuntime.ps1'
    $installFailure = ''
    try {
        $installOutput = & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -Force 2>&1
        $installCode = $LASTEXITCODE
        $installOutput | Out-Host
        if ($installCode -ne 0) { $installFailure = "Pinned native runtime installation failed (exit ${installCode}): $(($installOutput | Out-String).Trim())" }
    }
    catch { $installFailure = "Pinned native runtime installation failed: $($_.Exception.Message)" }
    $nativeState = Get-JuceNativePairState -RepoRoot $wt -Gitlink $gitlink
    $failure = (@($nativeState.FailureReason, $installFailure) | Where-Object { $_ }) -join '; '
    $nativePlan = Get-JuceNativePairPlan -Gitlink $gitlink -CopiedHead $copied -SiblingFound $siblingFound `
        -ManifestEntry $nativeState.ManifestEntry -Hashes $nativeState.Hashes -InstallAttempted -FailureReason $failure
}

# The prebuilt DLL came from the main checkout's last native build; its ABI must
# match the headers the managed wrapper is compiled against, or every engine
# open fails with a version mismatch. Ask the DLL itself (`dca_abi_version`).
$header = Join-Path $dst 'src\JuceSharp.Native\include\dotcharter_audio.h'
$wantAbi = if (Test-Path $header) { (Select-String -Path $header -Pattern '^#define DCA_ABI_VERSION\s+(\d+)' | Select-Object -First 1).Matches.Groups[1].Value } else { '' }
$dllAbi = ''
if (Test-Path $dll) {
    try {
        if (-not ('DcAbiProbe' -as [type])) {
            Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class DcAbiProbe {
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32")] static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint AbiFn();
    public static long Read(string path) {
        var h = LoadLibraryW(path); if (h == IntPtr.Zero) return -1;
        try { var p = GetProcAddress(h, "dca_abi_version"); if (p == IntPtr.Zero) return -1;
              return ((AbiFn)Marshal.GetDelegateForFunctionPointer(p, typeof(AbiFn)))(); }
        finally { FreeLibrary(h); }
    }
}
'@
        }
        $v = [DcAbiProbe]::Read($dll)
        if ($v -ge 0) { $dllAbi = "$v" }
    } catch { $dllAbi = '' }
}
$ok = (Test-Path (Join-Path $wt 'DotCharter.sln')) -and (Test-Path $dll)
Write-Host "worktree: $wt"
Write-Host "branch:   $Branch (from $Base @ $("$(git -C $wt rev-parse --short HEAD)".Trim()))"
Write-Host "juce_sharp.dll present: $(Test-Path $dll)"
Write-Host "JuceSharp: $("$(git -C $wt rev-parse --short "HEAD:JuceSharp")".Trim()) (ABI $wantAbi in the headers, $(if ($dllAbi) { "ABI $dllAbi in the DLL" } else { 'DLL ABI unreadable' }))"
if (-not $ok) { throw "Worktree is missing DotCharter.sln or juce_sharp.dll - not buildable." }
if (-not (Test-JuceNativeRuntimePair -Present (Get-PresentNativeFiles))) {
    Write-Warning ("The native runtime in $nativeDir is not a pair ($($nativeFiles -join ' + ')): the VST host and the DLL " +
        "ship together. Fetch both (tools\setup\Get-NativeRuntime.ps1) or copy both from one build.")
}
if ($nativePlan.Action -eq 'warn') {
    $origin = if (-not $plan.MainDllFitsGitlink -and -not $siblingFound) {
        "juce_sharp.dll was built for the main checkout's JuceSharp $($copied.Substring(0, 9)), but this worktree needs $($gitlink.Substring(0, 9)). "
    } else { "The native pair for JuceSharp $gitlink could not be verified. " }
    Write-Warning ($origin + $nativePlan.Reason + ' Build the native pair in this worktree (JuceSharp/AGENTS.md), or fetch its pinned runtime.')
}
if ($wantAbi -and $dllAbi -and $wantAbi -ne $dllAbi) {
    Write-Warning ("juce_sharp.dll is ABI ${dllAbi} but this worktree's JuceSharp is ABI ${wantAbi}: native tests and the app " +
        "will fail to open the engine. Rebuild the native DLL in this worktree (JuceSharp/AGENTS.md), or copy a DLL built " +
        "at JuceSharp $("$(git -C $wt rev-parse --short "HEAD:JuceSharp")".Trim()) into src\JuceSharp\runtimes\win-x64\native\.")
}

Write-Output ("native-pair: " + ([pscustomobject]@{ Gitlink = $gitlink; Action = $nativePlan.Action; Reason = $nativePlan.Reason } | ConvertTo-Json -Compress))
