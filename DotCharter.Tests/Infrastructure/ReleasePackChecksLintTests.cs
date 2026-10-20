using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Keeps <c>tools/release/New-Release.ps1</c>'s #137 checks WIRED and ON BY
///     DEFAULT. The rules themselves are pinned by
///     <c>Services/Update/ReleasePayloadIntegrityTests</c> and the script's own
///     <c>-SelfTest</c>; neither notices if the pack stops CALLING them, or if a
///     default flips back to "warn and ship" — which is how v0.59.0's first pack
///     validated a zero-filled <c>DotCharter.dll</c>, and how a 404ing ffmpeg pin
///     would have shipped a release with no ffmpeg.
/// </summary>
public sealed class ReleasePackChecksLintTests
{
    private static string Script() =>
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));

    private static int IndexOf(string script, string needle)
    {
        var at = script.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"New-Release.ps1 no longer contains `{needle}`.");
        return at;
    }

    /// <summary>The checker tool applies the rules type the gate pins, not a copy of them.</summary>
    [Fact]
    public void The_binary_check_tool_runs_the_pinned_rules_type()
    {
        var tool = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "CheckPayloadBinaries.cs"));

        Assert.Contains("ReleasePayloadIntegrity.Scan(", tool, StringComparison.Ordinal);
        Assert.Contains("$BinaryCheckTool = Join-Path $PSScriptRoot 'CheckPayloadBinaries.cs'", Script(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Both payloads are checked, and each BEFORE it becomes an asset: the UI
    ///     payload before it is hashed into the manifest, the rig before its zip.
    /// </summary>
    [Fact]
    public void Both_payloads_are_checked_before_they_are_hashed_or_zipped()
    {
        var script = Script();

        Assert.True(IndexOf(script, "Test-PayloadBinaries -PayloadDir $publishDir")
                    < IndexOf(script, "& dotnet run $ManifestTool manifest"),
            "the UI payload must be checked before payload-manifest.json hashes it.");
        Assert.True(IndexOf(script, "Test-PayloadBinaries -PayloadDir $rigDir.FullName")
                    < IndexOf(script, "New-FolderWrappedZip -Directory $rigDir.FullName"),
            "the RigLab payload must be checked before it is zipped.");
    }

    /// <summary>
    ///     The licence-inventory check restores first. It reads every
    ///     <c>obj/project.assets.json</c> and runs before the publishes, so without
    ///     <c>-Restore</c> a fresh clone fails it and an old tree checks a stale set.
    /// </summary>
    [Fact]
    public void The_dependency_inventory_check_restores_before_it_reads_the_assets() =>
        Assert.Contains("'Update-DependencyInventory.ps1') -Check -Restore", Script(), StringComparison.Ordinal);

    /// <summary>Every published byte is compiled by the release run itself.</summary>
    [Fact]
    public void Release_outputs_are_deleted_before_the_first_publish()
    {
        var script = Script();

        // The UI is published by Publish-UiPayload: once by the release (the LAST call)
        // and once by -PayloadOnly, which clears the UI's outputs itself.
        IndexOf(script, "& dotnet publish (Join-Path $RepoRoot 'DotCharter.UI/DotCharter.UI.csproj')");
        Assert.True(IndexOf(script, "Clear-ReleaseBuildOutputs $publishRoots")
                    < script.LastIndexOf(PublishCall, StringComparison.Ordinal),
            "bin/Release + obj/Release must be deleted before DotCharter.UI is published.");
        Assert.True(IndexOf(script, "Clear-ReleaseBuildOutputs @((Join-Path $RepoRoot 'DotCharter.UI/DotCharter.UI.csproj'))")
                    < IndexOf(script, PublishCall),
            "-PayloadOnly must delete the UI's bin/Release + obj/Release before it publishes.");
    }

    /// <summary>The release flow's call of the UI publish (the function holds the command).</summary>
    internal const string PublishCall = "\n    Publish-UiPayload -AppDir $appDir";

    /// <summary>
    ///     The launch smoke runs on the zip unless someone asks, by name, for it
    ///     not to — and a failed smoke removes the zip it judged.
    /// </summary>
    [Fact]
    public void The_launch_smoke_runs_on_the_zip_by_default()
    {
        var script = Script();

        Assert.Contains("[switch]$SkipLaunchSmoke", script, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"if \(\$SkipLaunchSmoke\)\s*\{[^}]*\}\s*else\s*\{\s*try\s*\{\s*Invoke-PackSmoke -Source \$uiZip"), script);
        Assert.Matches(new Regex(@"catch\s*\{[^}]*Remove-Item -LiteralPath \$uiZip[^}]*throw"), script);
    }

    /// <summary>A media tool that cannot be acquired fails the release unless explicitly allowed.</summary>
    [Fact]
    public void A_missing_media_tool_fails_the_release_by_default()
    {
        var script = Script();

        Assert.DoesNotContain("[switch]$RequireTools", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$AllowMissingTools", script, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"if \(-not \$AllowMissingTools\)\s*\{\s*throw"), script);
    }

    /// <summary>
    ///     ffmpeg is our minimal build (#409), not a download: the pack builds it from the
    ///     pinned sources when the cache is stale, and a cached OR fresh exe is accepted
    ///     only when it reports the inventory's version and lists every inventory
    ///     component — the check runs BEFORE "preflight done", so a missing codec costs
    ///     seconds, not a pack.
    /// </summary>
    [Fact]
    public void The_ffmpeg_is_built_and_checked_against_the_inventory_in_preflight()
    {
        var script = Script();

        Assert.DoesNotContain("BtbN/FFmpeg-Builds/releases/download", script, StringComparison.Ordinal);
        Assert.Contains("Import-Module (Join-Path $PSScriptRoot 'FfmpegInventory.psm1')", script, StringComparison.Ordinal);
        Assert.Contains("$FfmpegBuildScript = Join-Path $PSScriptRoot 'Build-MinimalFfmpeg.ps1'", script, StringComparison.Ordinal);

        var ensure = IndexOf(script, "function Ensure-RedistTools");
        var ensureEnd = IndexOf(script, "function Add-BundledTools");
        var body = script[ensure..ensureEnd];
        Assert.Contains("& $FfmpegBuildScript -OutDir $RedistDir", body, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(body, @"Test-FfmpegInventory \$ffmpeg \$FfmpegInventory").Count);
        Assert.Matches(new Regex(@"if \(\$missing\.Count -gt 0\) \{ throw"), body);

        Assert.True(IndexOf(script, "$redist = Ensure-RedistTools") < IndexOf(script, "Write-Ok 'preflight done'"),
            "the media tools (and the ffmpeg inventory check) must run in preflight.");
    }

    /// <summary>
    ///     The version a cached exe must report is the one the build script configures:
    ///     both read <c>components.json</c>, and the build script's ffmpeg tarball is that
    ///     version, so moving the pin in one place rebuilds instead of half-moving.
    /// </summary>
    [Fact]
    public void The_ffmpeg_version_pin_lives_in_the_inventory_alone()
    {
        var script = Script();
        var build = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Build-MinimalFfmpeg.ps1"));

        Assert.Contains("[regex]::Escape(\"$($FfmpegInventory.ffmpegVersion)-$($FfmpegInventory.extraVersion)\")", script, StringComparison.Ordinal);
        Assert.Contains("FFMPEG_VERSION = $inventory.ffmpegVersion", build, StringComparison.Ordinal);
        Assert.Contains("$env:FFMPEG_EXTRA_VERSION = $inventory.extraVersion", build, StringComparison.Ordinal);
        Assert.Contains("\"https://ffmpeg.org/releases/ffmpeg-$($Versions.FFMPEG_VERSION).tar.xz\")", build, StringComparison.Ordinal);
        // LGPL: no configure argument line turns on GPL or nonfree code (comments may name them).
        var buildSh = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "ffmpeg-min", "build.sh"));
        Assert.Contains("--disable-everything", buildSh, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"^\s*--enable-(gpl|version3|nonfree)\b", RegexOptions.Multiline), buildSh);
        foreach (var source in Regex.Matches(build, @"Sha256 = '(?<h>[^']*)'").Select(m => m.Groups["h"].Value))
            Assert.Matches(new Regex("^[0-9a-f]{64}$"), source);
    }
}
