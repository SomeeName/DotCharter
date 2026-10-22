using System.Diagnostics;
using System.Text;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The console tools a plugin ships with its <c>-WithPlugins</c> release (a project below
///     <c>plugins/&lt;name&gt;/</c> that sets <c>DotCharterReleaseTool</c>): <c>tools/release/PluginPayloadCheck.psm1</c>
///     finds them, names the files they put in a payload (the executable in <c>app/</c>, a launcher in
///     <c>tools/</c>) so a public payload is refused when it holds one and a plugin payload when it lacks
///     one, and writes the launcher a user drops files on. The module runs as itself under Windows
///     PowerShell on a fixture checkout; the launcher runs under <c>cmd</c> with its executable swapped for
///     an echo.
/// </summary>
public sealed class PluginReleaseToolTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "dotcharter-plugin-tools-" + Guid.NewGuid().ToString("N"));

    public PluginReleaseToolTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => BestEffortDelete.Directory(_scratch);

    private static string Q(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    private Dictionary<string, string> Run(string body) =>
        HeadlessShardsModule.RunModule(_scratch, body, "tools", "release", "PluginPayloadCheck.psm1")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);

    /// <summary>A checkout with one plugin, one marked tool, and the look-alikes that must not count.</summary>
    private string Checkout()
    {
        var root = Path.Combine(_scratch, "repo");
        var plugin = Path.Combine(root, "plugins", "Example");
        WriteProject(Path.Combine(plugin, "Example.Plugin.csproj"), "");
        WriteProject(Path.Combine(plugin, "tools", "Conv", "Example.Conv.csproj"),
            "<DotCharterReleaseTool>true</DotCharterReleaseTool><AssemblyName>ExampleConv</AssemblyName>");
        WriteProject(Path.Combine(plugin, "tools", "Plain", "Example.Plain.csproj"), "<OutputType>Exe</OutputType>");
        WriteProject(Path.Combine(plugin, "tools", "Off", "Example.Off.csproj"), "<DotCharterReleaseTool>false</DotCharterReleaseTool>");
        // A copy under obj/ (restore leaves some) and the tests project never ship.
        WriteProject(Path.Combine(plugin, "tools", "Conv", "obj", "Stale.csproj"), "<DotCharterReleaseTool>true</DotCharterReleaseTool>");
        WriteProject(Path.Combine(plugin, "Example.Plugin.Tests", "Example.Plugin.Tests.csproj"), "");
        return root;
    }

    private static void WriteProject(string path, string properties)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");
    }

    [Fact]
    public void A_release_tool_is_a_marked_project_below_a_plugin_and_names_its_executable_and_launcher()
    {
        var root = Checkout();
        var bare = Path.Combine(_scratch, "bare");
        Directory.CreateDirectory(bare);

        var results = Run(
            $"'exe=' + ((Get-PluginToolExeNames {Q(root)}) -join ',')\n" +
            $"'files=' + ((Get-PluginToolFileNames {Q(root)}) -join ',')\n" +
            $"'count=' + @(Get-PluginReleaseTools {Q(root)}).Count\n" +
            $"'assemblies=' + ((Get-PluginAssemblyNames {Q(root)}) -join ',')\n" +
            $"'none=' + @(Get-PluginReleaseTools {Q(bare)}).Count");

        Assert.Equal("ExampleConv.exe", results["exe"]);
        Assert.Equal("ExampleConv.exe,ExampleConv.cmd", results["files"]);
        // The unmarked, the marked-off, the obj/ copy: none of them is a tool.
        Assert.Equal("1", results["count"]);
        // A tool is not a plugin assembly: the plugin list stays the project at the top.
        Assert.Equal("Example.Plugin.dll", results["assemblies"]);
        Assert.Equal("0", results["none"]);
    }

    [Fact]
    public void A_public_payload_holding_a_tool_is_refused_and_a_plugin_payload_without_one_is_refused()
    {
        var root = Checkout();
        var cases = new (string Name, string Folder, bool WithPlugins)[]
        {
            ("public-clean", Payload("public-clean", exe: false, launcher: false), false),
            ("public-exe", Payload("public-exe", exe: true, launcher: false), false),
            ("public-launcher", Payload("public-launcher", exe: false, launcher: true), false),
            ("plugin-complete", Payload("plugin-complete", exe: true, launcher: true), true),
            ("plugin-no-exe", Payload("plugin-no-exe", exe: false, launcher: true), true),
            ("plugin-no-launcher", Payload("plugin-no-launcher", exe: true, launcher: false), true),
        };

        var results = Run($"$names = @(Get-PluginToolFileNames {Q(root)})\n" + string.Join("\n", cases.Select(c =>
            $"try {{ Assert-ReleasePayloadPlugins {Q(c.Folder)} $names{(c.WithPlugins ? " -WithPlugins" : "")}; '{c.Name}=PASS' }} " +
            $"catch {{ '{c.Name}=REFUSED ' + $_.Exception.Message }}")));

        Assert.Equal("PASS", results["public-clean"]);
        Assert.Contains("ExampleConv.exe", results["public-exe"], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", results["public-exe"], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED", results["public-launcher"], StringComparison.Ordinal);
        Assert.Equal("PASS", results["plugin-complete"]);
        Assert.Contains("lacks ExampleConv.exe", results["plugin-no-exe"], StringComparison.Ordinal);
        Assert.Contains("lacks ExampleConv.cmd", results["plugin-no-launcher"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_launcher_is_written_beside_the_bundled_tools_and_forwards_to_the_exe_in_the_app_folder()
    {
        var root = Checkout();
        var install = Path.Combine(_scratch, "install");
        Directory.CreateDirectory(install);

        Run($"Add-PluginToolLaunchers {Q(root)} {Q(install)}\n'done=1'");

        var path = Path.Combine(install, "tools", "ExampleConv.cmd");
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB, "a BOM before @echo off would be an unknown command.");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("@echo off\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\"%~dp0..\\app\\ExampleConv.exe\" %DC_PAUSE% %*", text, StringComparison.Ordinal);
        Assert.EndsWith("exit /b %errorlevel%\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_launcher_asks_the_tool_to_wait_for_a_key_only_when_explorer_started_it()
    {
        var root = Checkout();
        var install = Path.Combine(_scratch, "install");
        Directory.CreateDirectory(install);
        Run($"Add-PluginToolLaunchers {Q(root)} {Q(install)}\n'done=1'");
        var launcher = File.ReadAllText(Path.Combine(install, "tools", "ExampleConv.cmd"));

        // The tool's call becomes an echo of what the launcher would hand it.
        var echo = Path.Combine(_scratch, "echo.cmd");
        File.WriteAllText(echo, launcher.Replace("\"%~dp0..\\app\\ExampleConv.exe\" %DC_PAUSE% %*", "echo [%DC_PAUSE%] %*", StringComparison.Ordinal));
        Assert.Contains("echo [%DC_PAUSE%]", File.ReadAllText(echo), StringComparison.Ordinal);

        // Dropped on the script: Explorer runs `cmd /c ""script" "file""`, so the command line holds /c.
        var dropped = Cmd($"/c \"\"{echo}\" \"C:\\songs\\a & b.psarc\"\"");
        // Started from a shell prompt: the shell's own command line has no /c (here set by hand, as cmdcmdline is read-only to a script).
        var wrapper = Path.Combine(_scratch, "shell.cmd");
        File.WriteAllText(wrapper, "@echo off\r\nset \"cmdcmdline=\\\"C:\\Windows\\system32\\cmd.exe\\\" \"\r\ncall \"" + echo + "\" a.psarc\r\n");
        var shell = Cmd($"/c \"\"{wrapper}\"\"");

        Assert.Contains("[--pause] \"C:\\songs\\a & b.psarc\"", dropped, StringComparison.Ordinal);
        Assert.Contains("[] a.psarc", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("--pause", shell, StringComparison.Ordinal);
    }

    private string Cmd(string arguments)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the launcher is a Windows batch file.");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("cmd did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "cmd did not finish within 30 s.");
        return stdout.GetAwaiter().GetResult() + stderr;
    }

    private string Payload(string name, bool exe, bool launcher)
    {
        var root = Path.Combine(_scratch, name, "DotCharter-v0.0.0-win-x64");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        File.WriteAllText(Path.Combine(root, "app", "DotCharter.exe"), "app");
        if (exe) File.WriteAllText(Path.Combine(root, "app", "ExampleConv.exe"), "tool");
        if (launcher) File.WriteAllText(Path.Combine(root, "tools", "ExampleConv.cmd"), "launcher");
        return root;
    }
}
