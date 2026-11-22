using static DotCharter.Tests.Infrastructure.PublicSnapshotExportTests;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The native pair's build provenance (<c>tools/setup/NativeRuntime.psm1</c>): the same ABI is
///     not the same sources, so <c>Publish-NativeRuntime.ps1</c> pins or publishes a pair only when
///     a build record binds BOTH files' hashes to the native source tree of the gitlink. A fixture
///     JuceSharp repo under pwsh; the publish script is read, never executed here.
/// </summary>
public sealed class NativeBuildProvenanceTests
{
    private static string Module => Path.Combine(TestPaths.RepoRoot(), "tools", "setup", "NativeRuntime.psm1");
    private static string PublishScript => Path.Combine(TestPaths.RepoRoot(), "tools", "release", "Publish-NativeRuntime.ps1");

    [Fact]
    public void NativeBuild_RecordsTheDeployedPair_AndAFailedBuildInvalidatesItsRecord()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-native-build-record-").FullName;
        try
        {
            var root = Path.Combine(scratch, "repo");
            var juce = Path.Combine(root, "JuceSharp");
            Directory.CreateDirectory(juce);
            Git(juce, "init", "-q", "-b", "main");
            WriteTree(juce, "src/JuceSharp.Native/engine.cpp", "int engine() { return 1; }\n");
            Commit(juce, "native", ".");
            WriteTree(juce, "src/JuceSharp/runtimes/win-x64/native/juce_sharp.dll", "old dll");
            WriteTree(juce, "src/JuceSharp/runtimes/win-x64/native/juce_vst_host.exe", "old host");
            var fakeCmake = Path.Combine(scratch, "cmake.ps1");
            File.WriteAllText(fakeCmake, $$"""
                if ($env:DC_TEST_BUILD_FAIL -eq '1') { $global:LASTEXITCODE = 1; return }
                if ($args[0] -eq '--build' -and $args -contains '--clean-first') {
                    $native = {{Quote(Path.Combine(juce, "src/JuceSharp/runtimes/win-x64/native"))}}
                    New-Item -ItemType Directory -Path $native -Force | Out-Null
                    Set-Content (Join-Path $native 'juce_sharp.dll') 'new dll'
                    Set-Content (Join-Path $native 'juce_vst_host.exe') 'new host'
                }
                $global:LASTEXITCODE = 0
                """);
            var build = Path.Combine(TestPaths.RepoRoot(), "tools", "setup", "Build-NativeRuntime.ps1");
            var run = RunPwsh(scratch, $$"""
                & {{Quote(build)}} -RepoRoot {{Quote(root)}} -CMakeCommand {{Quote(fakeCmake)}}
                Import-Module {{Quote(Module)}} -Force
                $record = Get-NativeBuildProvenancePath {{Quote(root)}}
                $native = {{Quote(Path.Combine(juce, "src/JuceSharp/runtimes/win-x64/native"))}}
                $hashes = [ordered]@{}
                foreach ($file in Get-NativeRuntimePairFiles) { $hashes[$file] = Get-FileSha256 (Join-Path $native $file) }
                $problems = @(Test-NativeBuildProvenance -Path $record -NativeSourceTree (Get-NativeSourceTree {{Quote(juce)}} HEAD) -Hashes $hashes)
                "recorded=" + ((Test-Path $record) -and $problems.Count -eq 0)
                $env:DC_TEST_BUILD_FAIL = '1'
                try { & {{Quote(build)}} -RepoRoot {{Quote(root)}} -CMakeCommand {{Quote(fakeCmake)}}; 'failed=accepted' }
                catch { 'failed=refused' }
                "staleRecord=" + (Test-Path $record)
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("recorded=True", run.Output, StringComparison.Ordinal);
            Assert.Contains("failed=refused", run.Output, StringComparison.Ordinal);
            Assert.Contains("staleRecord=False", run.Output, StringComparison.Ordinal);
            Assert.Equal("new dll", File.ReadAllText(Path.Combine(juce, "src/JuceSharp/runtimes/win-x64/native/juce_sharp.dll")).Trim());
            Assert.Equal("new host", File.ReadAllText(Path.Combine(juce, "src/JuceSharp/runtimes/win-x64/native/juce_vst_host.exe")).Trim());
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void NativeBuild_ConfiguresFromAFreshCache_AndRefusesInjectedCompilerInputs()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-native-build-inputs-").FullName;
        try
        {
            var root = Path.Combine(scratch, "repo");
            var juce = Path.Combine(root, "JuceSharp");
            Directory.CreateDirectory(juce);
            Git(juce, "init", "-q", "-b", "main");
            WriteTree(juce, "src/JuceSharp.Native/engine.cpp", "int engine() { return 1; }\n");
            Commit(juce, "native", ".");
            var calls = Path.Combine(scratch, "calls.txt");
            var fakeCmake = Path.Combine(scratch, "cmake.ps1");
            File.WriteAllText(fakeCmake, $$"""
                Add-Content -LiteralPath {{Quote(calls)}} -Value ($args -join ' ')
                if ($args[0] -eq '--build') {
                    $native = {{Quote(Path.Combine(juce, "src/JuceSharp/runtimes/win-x64/native"))}}
                    New-Item -ItemType Directory -Path $native -Force | Out-Null
                    Set-Content (Join-Path $native 'juce_sharp.dll') 'new dll'
                    Set-Content (Join-Path $native 'juce_vst_host.exe') 'new host'
                }
                $global:LASTEXITCODE = 0
                """);
            var build = Path.Combine(TestPaths.RepoRoot(), "tools", "setup", "Build-NativeRuntime.ps1");
            var run = RunPwsh(scratch, $$"""
                foreach ($name in 'CFLAGS', 'CXXFLAGS', 'LDFLAGS', 'CMAKE_TOOLCHAIN_FILE') { [Environment]::SetEnvironmentVariable($name, $null) }
                & {{Quote(build)}} -RepoRoot {{Quote(root)}} -CMakeCommand {{Quote(fakeCmake)}}
                'clean=built'
                'fresh=' + [bool]((Get-Content -LiteralPath {{Quote(calls)}})[0] -match '^--fresh ')
                Remove-Item -LiteralPath {{Quote(calls)}}
                $env:CXXFLAGS = '/DJUCE_DEBUG=1'
                try { & {{Quote(build)}} -RepoRoot {{Quote(root)}} -CMakeCommand {{Quote(fakeCmake)}}; 'flags=accepted' }
                catch { "flags=refused: $($_.Exception.Message)" }
                'configured=' + (Test-Path -LiteralPath {{Quote(calls)}})
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("clean=built", run.Output, StringComparison.Ordinal);
            Assert.Contains("fresh=True", run.Output, StringComparison.Ordinal);
            Assert.Contains("flags=refused: Unset CXXFLAGS", run.Output, StringComparison.Ordinal);
            Assert.Contains("configured=False", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void NativeBuildInputs_AreAllInsideTheNativeSourceFolder()
    {
        var juce = Path.Combine(TestPaths.RepoRoot(), "JuceSharp");
        Assert.SkipUnless(File.Exists(Path.Combine(juce, "src", "JuceSharp.Native", "CMakeLists.txt")), "the JuceSharp submodule is not checked out");
        var tracked = Git(juce, "ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        const string native = "src/JuceSharp.Native/";
        var offenders = new List<string>();
        offenders.AddRange(tracked.Where(f => !f.StartsWith(native, StringComparison.Ordinal) &&
            (Path.GetFileName(f) == "CMakeLists.txt" || f.EndsWith(".cmake", StringComparison.Ordinal)))
            .Select(f => f + ": a CMake file outside " + native));
        var nativeRoot = Path.GetFullPath(Path.Combine(juce, native));
        var include = new System.Text.RegularExpressions.Regex(@"^\s*#\s*include\s*""([^""]*\.\.[^""]*)""", System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (var file in tracked.Where(f => f.StartsWith(native, StringComparison.Ordinal) &&
                     Path.GetExtension(f) is ".h" or ".hpp" or ".cpp" or ".c" or ".inl"))
        {
            var full = Path.Combine(juce, file);
            if (!File.Exists(full)) continue;
            foreach (System.Text.RegularExpressions.Match m in include.Matches(File.ReadAllText(full)))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, m.Groups[1].Value));
                if (!target.StartsWith(nativeRoot, StringComparison.OrdinalIgnoreCase)) offenders.Add(file + ": includes " + m.Groups[1].Value);
            }
        }
        var cmake = File.ReadAllText(Path.Combine(nativeRoot, "CMakeLists.txt"));
        offenders.AddRange(System.Text.RegularExpressions.Regex.Matches(cmake, @"\$\{CMAKE_CURRENT_SOURCE_DIR\}/\.\.[^\s""\)]*")
            .Select(m => m.Value)
            .Where(v => !v.StartsWith("${CMAKE_CURRENT_SOURCE_DIR}/../JuceSharp/runtimes/", StringComparison.Ordinal))
            .Select(v => "CMakeLists.txt reads " + v));
        Assert.True(offenders.Count == 0,
            "The native build record hashes only src/JuceSharp.Native; these inputs lie outside it:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ARecordedBuild_BindsBothFiles_ToTheNativeSourceTree_AndNothingElsePasses()
    {
        var scratch = Directory.CreateTempSubdirectory("dc-native-provenance-").FullName;
        try
        {
            var root = Path.Combine(scratch, "repo");
            var juce = Path.Combine(root, "JuceSharp");
            Directory.CreateDirectory(juce);
            Git(juce, "init", "-q", "-b", "main");
            WriteTree(juce, "src/JuceSharp.Native/engine.cpp", "int engine() { return 1; }\n");
            Commit(juce, "native v1", ".");
            var oldTree = Git(juce, "rev-parse", "HEAD:src/JuceSharp.Native");
            WriteTree(juce, "src/JuceSharp.Native/engine.cpp", "int engine() { return 2; }\n");
            Commit(juce, "native v2", ".");
            var native = Path.Combine(scratch, "native");
            WriteTree(native, "juce_sharp.dll", "the dll of v2");
            WriteTree(native, "juce_vst_host.exe", "the host of v2");
            var record = Path.Combine(scratch, "artifacts", "native-build.provenance.json");

            var run = RunPwsh(scratch, $$"""
                Import-Module {{Quote(Module)}} -Force
                $native = {{Quote(native)}}
                $record = {{Quote(record)}}
                $hashes = [ordered]@{
                    'juce_sharp.dll' = Get-FileSha256 (Join-Path $native 'juce_sharp.dll')
                    'juce_vst_host.exe' = Get-FileSha256 (Join-Path $native 'juce_vst_host.exe')
                }
                $tree = Get-NativeSourceTree -JuceSharpDir {{Quote(juce)}} -Rev HEAD
                function Show([string]$Case, [string]$Tree, $Pair) {
                    "$Case=" + (@(Test-NativeBuildProvenance -Path $record -NativeSourceTree $Tree -Hashes $Pair) -join ' | ')
                }
                Show 'unrecorded' $tree $hashes
                [void](Write-NativeBuildProvenance -RepoRoot {{Quote(root)}} -NativeDir $native -Path $record)
                Show 'recorded' $tree $hashes
                Show 'olderSources' {{Quote(oldTree)}} $hashes
                $swapped = [ordered]@{ 'juce_sharp.dll' = ('e' * 64); 'juce_vst_host.exe' = $hashes['juce_vst_host.exe'] }
                Show 'swappedDll' $tree $swapped
                Set-Content -LiteralPath (Join-Path {{Quote(juce)}} 'src/JuceSharp.Native/engine.cpp') -Value 'int engine() { return 3; }'
                try { [void](Write-NativeBuildProvenance -RepoRoot {{Quote(root)}} -NativeDir $native -Path $record); 'dirty=recorded' }
                catch { "dirty=refused: $($_.Exception.Message)" }
                """);
            Assert.True(run.ExitCode == 0, run.Output);
            var lines = run.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains('=', StringComparison.Ordinal))
                .ToDictionary(l => l[..l.IndexOf('=', StringComparison.Ordinal)], l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..]);

            Assert.Contains("no build provenance record", lines["unrecorded"], StringComparison.Ordinal);
            Assert.Equal("", lines["recorded"]);
            // A pair with the right ABI but recorded from other native sources is refused.
            Assert.Contains("recorded as built from native sources", lines["olderSources"], StringComparison.Ordinal);
            Assert.Contains("juce_sharp.dll has SHA-256 " + new string('e', 64), lines["swappedDll"], StringComparison.Ordinal);
            Assert.DoesNotContain("juce_vst_host.exe", lines["swappedDll"], StringComparison.Ordinal);
            Assert.StartsWith("refused: ", lines["dirty"], StringComparison.Ordinal);
            Assert.Contains("uncommitted changes", lines["dirty"], StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(scratch);
        }
    }

    [Fact]
    public void PublishScript_ChecksTheBuildRecord_BeforeItPinsOrUploads()
    {
        var source = string.Join('\n', System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(PublishScript).Replace("\r\n", "\n", StringComparison.Ordinal), "<#.*?#>", "",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        var check = source.IndexOf("Test-NativeBuildProvenance -Path $ProvenancePath -NativeSourceTree $sourceTree -Hashes $hashes", StringComparison.Ordinal);
        Assert.True(check >= 0, "Publish-NativeRuntime.ps1 must check the pair against its build record (Test-NativeBuildProvenance).");
        Assert.Contains("Get-NativeSourceTree -JuceSharpDir (Join-Path $repoRoot 'JuceSharp') -Rev $commit", source, StringComparison.Ordinal);
        var refusal = source.IndexOf("if ($provenanceProblems.Count -gt 0)", StringComparison.Ordinal);
        Assert.True(refusal > check, "Publish-NativeRuntime.ps1 must refuse when the build record does not hold.");
        foreach (var later in new[] { "[System.IO.Compression.ZipFile]::Open", "Read-NativeRuntimeManifest $manifestPath", "gh release upload" })
            Assert.True(source.IndexOf(later, StringComparison.Ordinal) > refusal, $"Publish-NativeRuntime.ps1 reaches '{later}' before it checks the build record.");
    }
}
