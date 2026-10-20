using System.IO.Compression;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A public release asset never carries a plugin, and a <c>-WithPlugins</c> one never
///     LACKS one. <c>tools/release/PluginPayloadCheck.psm1</c> is run here as itself, under
///     Windows PowerShell 5.1, on fixture payloads and a fixture checkout: by default a clean
///     folder and zip pass and the same payload with a plugin assembly planted is refused both
///     ways; with <c>-WithPlugins</c> the other way round. The plugin names come from the
///     checkout (the project file at the top of each <c>plugins/&lt;name&gt;/</c>). The lints
///     below keep <c>New-Release.ps1</c> CALLING the check on the folder before the zip, on the
///     zip after, on a <c>-SmokeOnly</c> input, publishing with the mode's switch, and printing
///     the resolved repo.
/// </summary>
public sealed class ReleasePluginRefusalTests : IDisposable
{
    private const string Plugin = "Example.Plugin.dll";

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-plugin-refusal-" + Guid.NewGuid().ToString("N"));

    public ReleasePluginRefusalTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    private static string Script() =>
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "release", "New-Release.ps1"));

    private static string Q(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    [Fact]
    public void The_plugin_names_are_the_project_files_at_the_top_of_each_plugin_folder()
    {
        var root = Path.Combine(_scratch, "repo");
        Directory.CreateDirectory(Path.Combine(root, "plugins", "Example", "Example.Plugin.Tests"));
        File.WriteAllText(Path.Combine(root, "plugins", "Example", "Example.Plugin.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "plugins", "Example", "Example.Plugin.Tests", "Example.Plugin.Tests.csproj"), "<Project />");
        var bare = Path.Combine(_scratch, "bare");
        Directory.CreateDirectory(bare);

        var results = Run(
            $"'names=' + ((Get-PluginAssemblyNames {Q(root)}) -join ',')\n" +
            $"'none=' + @(Get-PluginAssemblyNames {Q(bare)}).Count");

        // The plugin's tests sit one level down and never ship.
        Assert.Equal(Plugin, results["names"]);
        Assert.Equal("0", results["none"]);
    }

    [Fact]
    public void The_refusal_passes_a_clean_payload_and_refuses_one_carrying_a_plugin()
    {
        var clean = Payload("clean", extra: null);
        var planted = Payload("planted", extra: "app/" + Plugin);
        var nearMiss = Payload("near-miss", extra: "app/Example.Plugin.Tests.dll");
        var cases = new (string Name, string Path)[]
        {
            ("clean-folder", clean),
            ("clean-zip", Zip(clean)),
            ("near-miss-folder", nearMiss),
            ("near-miss-zip", Zip(nearMiss)),
            ("planted-folder", planted),
            ("planted-zip", Zip(planted)),
            ("missing", Path.Combine(_scratch, "no-such-payload.zip")),
        };

        var results = Run(string.Join("\n", cases.Select(c =>
            $"try {{ Assert-NoPluginInPayload {Q(c.Path)} @('{Plugin}'); '{c.Name}=PASS' }} " +
            $"catch {{ '{c.Name}=REFUSED ' + $_.Exception.Message }}")));

        Assert.Equal("PASS", results["clean-folder"]);
        Assert.Equal("PASS", results["clean-zip"]);
        Assert.Equal("PASS", results["near-miss-folder"]);
        Assert.Equal("PASS", results["near-miss-zip"]);
        Assert.StartsWith("REFUSED", results["planted-folder"], StringComparison.Ordinal);
        Assert.Contains(Plugin, results["planted-folder"], StringComparison.Ordinal);
        Assert.Contains("DotCharter-v0.0.0-win-x64/app/" + Plugin, results["planted-zip"], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED No payload at", results["missing"], StringComparison.Ordinal);
    }

    [Fact]
    public void With_plugins_the_check_is_inverted_and_the_default_is_unchanged()
    {
        var clean = Payload("clean", extra: null);
        var planted = Payload("planted", extra: "app/" + Plugin);
        var cases = new (string Name, string Path, bool WithPlugins)[]
        {
            ("default-clean-folder", clean, false),
            ("default-planted-zip", Zip(planted), false),
            ("withplugins-planted-folder", planted, true),
            ("withplugins-planted-zip", planted + ".zip", true),
            ("withplugins-clean-folder", clean, true),
            ("withplugins-clean-zip", clean + ".zip", true),
        };
        Zip(clean);

        var results = Run(string.Join("\n", cases.Select(c =>
            $"try {{ Assert-ReleasePayloadPlugins {Q(c.Path)} @('{Plugin}'){(c.WithPlugins ? " -WithPlugins" : "")}; '{c.Name}=PASS' }} " +
            $"catch {{ '{c.Name}=REFUSED ' + $_.Exception.Message }}")) +
            $"\ntry {{ Assert-ReleasePayloadPlugins {Q(planted)} @() -WithPlugins; 'withplugins-none=PASS' }} catch {{ 'withplugins-none=REFUSED ' + $_.Exception.Message }}" +
            "\n'args-default=' + ((Get-UiPluginPublishArgs) -join ' ')" +
            "\n'args-withplugins=' + ((Get-UiPluginPublishArgs -WithPlugins) -join ' ')" +
            "\n'args-withplugins-false=' + ((Get-UiPluginPublishArgs -WithPlugins:$false) -join ' ')");

        Assert.Equal("PASS", results["default-clean-folder"]);
        Assert.StartsWith("REFUSED", results["default-planted-zip"], StringComparison.Ordinal);
        Assert.Equal("PASS", results["withplugins-planted-folder"]);
        Assert.Equal("PASS", results["withplugins-planted-zip"]);
        Assert.StartsWith("REFUSED", results["withplugins-clean-folder"], StringComparison.Ordinal);
        Assert.Contains("lacks " + Plugin, results["withplugins-clean-folder"], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", results["withplugins-clean-zip"], StringComparison.Ordinal);
        // A plugin build with no plugin checked out is no plugin build.
        Assert.StartsWith("REFUSED -WithPlugins needs at least one plugin", results["withplugins-none"], StringComparison.Ordinal);
        Assert.Equal("-p:DotCharterPlugins=false", results["args-default"]);
        Assert.Equal("-p:DotCharterPlugins=false", results["args-withplugins-false"]);
        Assert.Equal("-p:DotCharterPlugins=true", results["args-withplugins"]);
    }

    [Fact]
    public void The_release_repo_is_public_by_default_and_a_plugin_build_names_its_own()
    {
        var root = Path.Combine(_scratch, "repo");
        var other = Path.Combine(root, "plugins", "Example");
        Directory.CreateDirectory(other);

        var results = Run(string.Join("\n",
            $"$f = Resolve-ReleaseFeed -RepoRoot {Q(root)}; 'public=' + $f.Repo + '|' + ($f.TargetCheckout -eq {Q(root)}) + '|' + $f.IsOtherCheckout",
            $"$f = Resolve-ReleaseFeed -WithPlugins -Repo 'example-owner/example-plugin' -TagCheckout {Q(other)} -RepoRoot {Q(root)}; 'plugin=' + $f.Repo + '|' + ($f.TargetCheckout -eq {Q(other)}) + '|' + $f.IsOtherCheckout",
            $"try {{ Resolve-ReleaseFeed -WithPlugins -RepoRoot {Q(root)} | Out-Null; 'no-repo=PASS' }} catch {{ 'no-repo=REFUSED ' + $_.Exception.Message }}",
            $"try {{ Resolve-ReleaseFeed -WithPlugins -Repo 'SomeeName/DotCharter' -RepoRoot {Q(root)} | Out-Null; 'to-public=PASS' }} catch {{ 'to-public=REFUSED ' + $_.Exception.Message }}",
            $"try {{ Resolve-ReleaseFeed -Repo 'not a repo' -RepoRoot {Q(root)} | Out-Null; 'bad=PASS' }} catch {{ 'bad=REFUSED ' + $_.Exception.Message }}",
            $"try {{ Resolve-ReleaseFeed -TagCheckout {Q(Path.Combine(_scratch, "nowhere"))} -RepoRoot {Q(root)} | Out-Null; 'no-checkout=PASS' }} catch {{ 'no-checkout=REFUSED ' + $_.Exception.Message }}"));

        Assert.Equal("SomeeName/DotCharter|True|False", results["public"]);
        Assert.Equal("example-owner/example-plugin|True|True", results["plugin"]);
        Assert.StartsWith("REFUSED -WithPlugins needs -Repo", results["no-repo"], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED -WithPlugins may not release to the public repo", results["to-public"], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED -Repo must be <owner>/<name>", results["bad"], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED -TagCheckout", results["no-checkout"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_publishes_by_mode_and_checks_the_folder_and_the_zip()
    {
        var script = Script();

        // -WithPlugins is an explicit switch, off unless given; the publish args come from the module.
        Assert.Contains("[switch]$WithPlugins,", script, StringComparison.Ordinal);
        Assert.Contains("$UiPluginPublishArgs = @(Get-UiPluginPublishArgs -WithPlugins:$WithPlugins)", script, StringComparison.Ordinal);
        Assert.Contains("Import-Module (Join-Path $PSScriptRoot 'PluginPayloadCheck.psm1') -Force", script, StringComparison.Ordinal);
        Assert.Contains("$PluginAssemblies = @(Get-PluginAssemblyNames $RepoRoot)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("function Assert-NoPluginInPayload", script, StringComparison.Ordinal);
        Assert.DoesNotContain("function Assert-ReleasePayloadPlugins", script, StringComparison.Ordinal);

        // The one UI publish: the switch on the command, then the check on its output.
        var publish = script[script.IndexOf("function Publish-UiPayload", StringComparison.Ordinal)..];
        publish = publish[..publish.IndexOf("\nfunction ", 10, StringComparison.Ordinal)];
        Assert.Contains("@UiPluginPublishArgs", publish, StringComparison.Ordinal);
        Assert.True(publish.IndexOf("Assert-ReleasePayloadPlugins $AppDir $PluginAssemblies -WithPlugins:$WithPlugins", StringComparison.Ordinal)
                    > publish.IndexOf("& dotnet publish", StringComparison.Ordinal),
            "Publish-UiPayload must check its output after publishing it.");

        // The install folder before it is zipped, the zip after.
        var zipStep = script.IndexOf("New-FolderWrappedZip -Directory $publishDir -ZipPath $uiZip", StringComparison.Ordinal);
        Assert.True(zipStep > 0);
        Assert.True(script.IndexOf("Assert-ReleasePayloadPlugins $publishDir $PluginAssemblies -WithPlugins:$WithPlugins", StringComparison.Ordinal) is var folder
                    && folder > 0 && folder < zipStep, "the payload folder must be checked before it is zipped.");
        Assert.True(script.IndexOf("Assert-ReleasePayloadPlugins $uiZip $PluginAssemblies -WithPlugins:$WithPlugins", StringComparison.Ordinal) > zipStep,
            "the zip must be checked after it is written.");
    }

    [Fact]
    public void The_printed_command_targets_the_resolved_repo_settled_before_the_build()
    {
        var script = Script();

        var resolve = script.IndexOf(
            "$ReleaseFeed = Resolve-ReleaseFeed -Repo $Repo -WithPlugins:$WithPlugins -TagCheckout $TagCheckout -RepoRoot $RepoRoot",
            StringComparison.Ordinal);
        Assert.True(resolve > 0);
        Assert.True(resolve < script.IndexOf("if ($PayloadOnly) {", StringComparison.Ordinal),
            "the release repo must be resolved before anything is built.");
        Assert.Contains("gh release create v$Version -R $($ReleaseFeed.Repo)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release create v$Version -R SomeeName", script, StringComparison.Ordinal);
        Assert.Contains("$sha = git -C \"$($ReleaseFeed.TargetCheckout)\" rev-parse HEAD", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_smoke_only_input_is_refused_before_anything_is_started()
    {
        var script = Script();
        var smoke = script[script.IndexOf("if ($SmokeOnly) {", StringComparison.Ordinal)..];

        Assert.True(smoke.IndexOf("Assert-ReleasePayloadPlugins $source $PluginAssemblies -WithPlugins:$WithPlugins", StringComparison.Ordinal) is var check
                    && check > 0 && check < smoke.IndexOf("Invoke-PackSmoke", StringComparison.Ordinal),
            "-SmokeOnly must check the payload's plugins before it launches it.");
    }

    /// <summary>Runs <paramref name="body" /> against the real module; one <c>name=result</c> per line.</summary>
    private Dictionary<string, string> Run(string body) =>
        HeadlessShardsModule.RunModule(_scratch, body, "tools", "release", "PluginPayloadCheck.psm1")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);

    /// <summary>A split payload folder (#413) with the files the check walks past.</summary>
    private string Payload(string name, string? extra)
    {
        var root = Path.Combine(_scratch, name, "DotCharter-v0.0.0-win-x64");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "DotCharter.exe"), "launcher");
        File.WriteAllText(Path.Combine(root, "app", "DotCharter.exe"), "app");
        File.WriteAllText(Path.Combine(root, "app", "DotCharter.dll"), "app");
        if (extra != null) File.WriteAllText(Path.Combine(root, extra), "stub");
        return root;
    }

    /// <summary>Zipped the release's way: folder-wrapped, forward-slash entries.</summary>
    private static string Zip(string folder)
    {
        var zip = folder + ".zip";
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }
}
