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

# The copy above is whatever the MAIN CHECKOUT has checked out, which lags the
# new worktree's base whenever the owner's checkout cannot fast-forward (local
# edits) or nobody ran `git submodule update` after a gitlink bump. A stale copy
# does not build (#56: after #49's ABI v38, `JuceChartNote` was missing). So
# when they differ, put the base commit's EXACT JuceSharp tree in place: fetch
# it into the main checkout's submodule repo (objects only - the owner's
# working tree is untouched) and mirror `git archive` of it over the copy,
# keeping the prebuilt DLL and build folders.
$gitlink = "$(git -C $wt rev-parse "HEAD:JuceSharp")".Trim()
$copied = "$(git -C $src rev-parse HEAD)".Trim()
if ($gitlink -and $gitlink -ne $copied) {
    Write-Host "JuceSharp: main checkout is at $($copied.Substring(0, 9)), the base needs $($gitlink.Substring(0, 9)) - extracting that tree."
    $ErrorActionPreference = 'Continue'
    git -C $src cat-file -e "$gitlink^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { git -C $src fetch --quiet origin 2>&1 | Out-Host }
    $tree = Join-Path ([IO.Path]::GetTempPath()) ("juce-" + $gitlink.Substring(0, 12))
    if (Test-Path $tree) { Remove-Item -Recurse -Force $tree }
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
        throw "Extracting JuceSharp $gitlink produced no sources (see $tree) - the copy was left as it was."
    }
    # Mirror the tracked tree over the copy; /XD keeps (and never deletes) build
    # output folders, and the DLL is put back explicitly because it is untracked.
    $keep = Join-Path $tree 'juce_sharp.dll.keep'
    if (Test-Path $dll) { Copy-Item $dll $keep }
    robocopy $tree $dst /MIR /XD build bin obj node_modules TestResults /XF juce_sharp.dll.keep /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy (JuceSharp at the gitlink) failed ($LASTEXITCODE)." }
    if (Test-Path $keep) {
        New-Item -ItemType Directory -Force -Path (Split-Path $dll) | Out-Null
        Copy-Item $keep $dll -Force
    }
    Remove-Item -Recurse -Force $tree
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
$dllMatchesGitlink = $true
if ($gitlink -and $gitlink -ne $copied) {
    $dllMatchesGitlink = $false
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
        $newestSrc = Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue `
            (Join-Path $c 'src\JuceSharp.Native\src'), (Join-Path $c 'src\JuceSharp.Native\include') |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newestSrc -and (Get-Item $cDll).LastWriteTime -lt $newestSrc.LastWriteTime) { continue }
        New-Item -ItemType Directory -Force -Path (Split-Path $dll) | Out-Null
        Copy-Item $cDll $dll -Force
        $vstHost = Join-Path (Split-Path $cDll) 'juce_vst_host.exe'
        if (Test-Path $vstHost) { Copy-Item $vstHost (Split-Path $dll) -Force }
        Write-Host "juce_sharp.dll: taken from $c (built at JuceSharp $($head.Substring(0, 9)); native sources match $($gitlink.Substring(0, 9)))."
        $dllMatchesGitlink = $true
        break
    }
    $ErrorActionPreference = 'Stop'
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
if (-not $dllMatchesGitlink) {
    Write-Warning ("juce_sharp.dll was built for the main checkout's JuceSharp $($copied.Substring(0, 9)), but this worktree " +
        "needs $($gitlink.Substring(0, 9)), and no other worktree has a DLL built there. The ABI number may still match while " +
        "native code differs (native-tone tests then fail for no reason in your change). Build the DLL in this worktree " +
        "(JuceSharp/AGENTS.md) or copy one built at $($gitlink.Substring(0, 9)) into src\JuceSharp\runtimes\win-x64\native\.")
}
if ($wantAbi -and $dllAbi -and $wantAbi -ne $dllAbi) {
    Write-Warning ("juce_sharp.dll is ABI ${dllAbi} but this worktree's JuceSharp is ABI ${wantAbi}: native tests and the app " +
        "will fail to open the engine. Rebuild the native DLL in this worktree (JuceSharp/AGENTS.md), or copy a DLL built " +
        "at JuceSharp $("$(git -C $wt rev-parse --short "HEAD:JuceSharp")".Trim()) into src\JuceSharp\runtimes\win-x64\native\.")
}
