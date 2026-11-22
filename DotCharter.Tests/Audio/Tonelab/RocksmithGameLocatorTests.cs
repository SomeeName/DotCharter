using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>Finding the Rocksmith folder over a fake registry and file system (#449): no wall clock, no real disk.</summary>
public sealed class RocksmithGameLocatorTests
{
    private const string Steam = @"C:\Program Files (x86)\Steam";
    private const string GameOnC = Steam + @"\steamapps\common\Rocksmith2014";
    private const string Manifest = "\"AppState\"\n{\n\t\"appid\"\t\t\"221680\"\n\t\"installdir\"\t\t\"Rocksmith2014\"\n}\n";

    private const string LibraryVdf = "\"libraryfolders\"\n{\n"
        + "\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"228980\"\t\t\"1\"\n\t\t}\n\t}\n"
        + "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"221680\"\t\t\"2\"\n\t\t}\n\t}\n}\n";

    [Fact]
    public void NoUninstallKey_ButSteamPathAndManifest_FindsTheGameFolder()
    {
        var system = new FakeSystem { SteamPath = "c:/program files (x86)/steam" };
        system.Files[Steam + @"\steamapps\appmanifest_221680.acf"] = Manifest;
        system.Files[GameOnC + @"\Rocksmith2014.exe"] = "";

        var finding = RocksmithGameLocator.Find(system);

        Assert.Equal(GameOnC, finding.Directory, ignoreCase: true);
        Assert.Equal("Steam library", finding.Source);
    }

    [Fact]
    public void TheSecondLibraryHoldingTheManifest_Wins_OverAnEmptyFirstOne()
    {
        var system = new FakeSystem { SteamPath = Steam };
        system.Files[Steam + @"\steamapps\libraryfolders.vdf"] = LibraryVdf;
        system.Files[@"D:\SteamLibrary\steamapps\appmanifest_221680.acf"] = Manifest;
        system.Files[@"D:\SteamLibrary\steamapps\common\Rocksmith2014\Rocksmith2014.exe"] = "";

        var finding = RocksmithGameLocator.Find(system);

        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Rocksmith2014", finding.Directory);
    }

    [Fact]
    public void TheConfiguredFolder_Wins_OverSteam()
    {
        var system = new FakeSystem { SteamPath = Steam, Configured = @"E:\Games\RS" };
        system.Files[Steam + @"\steamapps\appmanifest_221680.acf"] = Manifest;
        system.Files[GameOnC + @"\Rocksmith2014.exe"] = "";
        system.Files[@"E:\Games\RS\Rocksmith2014.exe"] = "";

        var finding = RocksmithGameLocator.Find(system);

        Assert.Equal(@"E:\Games\RS", finding.Directory);
        Assert.Equal(RocksmithGameLocator.ConfiguredFolderVariable, finding.Source);
    }

    [Fact]
    public void AConfiguredFolderWithoutTheGame_IsSkipped()
    {
        var system = new FakeSystem { SteamPath = Steam, Configured = @"E:\Nothing" };
        system.Files[Steam + @"\steamapps\appmanifest_221680.acf"] = Manifest;
        system.Files[GameOnC + @"\Rocksmith2014.exe"] = "";

        Assert.Equal(GameOnC, RocksmithGameLocator.Find(system).Directory);
    }

    [Fact]
    public void TheUninstallKey_StillWorks_WhenSteamHasNoPath()
    {
        var system = new FakeSystem { Uninstall = @"F:\Old\Rocksmith2014" };
        system.Directories.Add(@"F:\Old\Rocksmith2014");

        var finding = RocksmithGameLocator.Find(system);

        Assert.Equal(@"F:\Old\Rocksmith2014", finding.Directory);
        Assert.Equal("Steam uninstall key", finding.Source);
    }

    [Fact]
    public void NothingFound_GivesNull_AndAReason()
    {
        var finding = RocksmithGameLocator.Find(new FakeSystem { SteamPath = Steam });

        Assert.Null(finding.Directory);
        Assert.NotEmpty(finding.Reason);
    }

    [Fact]
    public void ARealGameDirectoryLookup_LogsWhereItCameFrom()
    {
        var system = new FakeSystem { SteamPath = Steam };
        system.Files[Steam + @"\steamapps\appmanifest_221680.acf"] = Manifest;
        system.Files[GameOnC + @"\Rocksmith2014.exe"] = "";
        var logger = new Infrastructure.FakeAppLogger();

        Assert.Equal(GameOnC, RocksmithGame.FindGameDirectory(system, logger));
        Assert.Contains(logger.Entries, e => e.Message.Contains("Steam library", StringComparison.Ordinal));
    }

    [Fact]
    public void TheVdfReader_UnescapesPaths_AndIgnoresTheRest()
    {
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], RocksmithGameLocator.LibraryPaths(LibraryVdf));
        Assert.Equal("Rocksmith2014", RocksmithGameLocator.InstallDir(Manifest));
        Assert.Null(RocksmithGameLocator.InstallDir("garbage"));
    }

    private sealed class FakeSystem : IGameLocatorSystem
    {
        public string? SteamPath { get; init; }

        public string? Uninstall { get; init; }

        public string? Configured { get; init; }

        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? EnvironmentVariable(string name) => name == RocksmithGameLocator.ConfiguredFolderVariable ? Configured : null;

        public string? RegistryString(GameLocatorRegistryRoot root, string subKey, string valueName) =>
            root == GameLocatorRegistryRoot.CurrentUser && valueName == "SteamPath" ? SteamPath
            : root == GameLocatorRegistryRoot.LocalMachine32 && subKey.EndsWith("Steam App 221680", StringComparison.Ordinal) ? Uninstall
            : null;

        public bool DirectoryExists(string path) =>
            Directories.Contains(path) || Files.Keys.Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        public bool FileExists(string path) => Files.ContainsKey(path);

        public string? ReadAllText(string path) => Files.GetValueOrDefault(path);
    }
}
