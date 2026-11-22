using System.Security.Cryptography;
using System.Text.Json;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Session;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;

namespace DotCharter.Tests.Services.Session;

public sealed class ProjectFolderTidyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chart_layout_").FullName;

    [Theory]
    [InlineData("song.wem", false)]
    [InlineData("song.preview.wem", false)]
    [InlineData("song.preview.wav", false)]
    [InlineData("song.wem", true)]
    [InlineData("song.preview.wem", true)]
    [InlineData("song.preview.wav", true)]
    public void Plain_audio_names_require_verified_ownership_before_recycling(string name, bool owned)
    {
        Write("project.dcproj", "project");
        Write("song.flac", "working audio");
        var path = Path.Combine(_dir, name);
        if (owned) Assert.True(CompanionOwnership.WriteIfUnmodified(path, target => File.WriteAllText(target, "generated"), out _));
        else Write(name, "foreign audio");
        var recycled = new List<string>();
        var logger = new FakeAppLogger();
        Tidy(Path.Combine(_dir, "project.dcproj"), new ChartProject { AudioFileName = "song.flac" }, logger,
            file =>
            {
                recycled.Add(file);
                File.Move(file, Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "recycle")).FullName, Path.GetFileName(file)));
            });
        if (owned)
        {
            Assert.Equal(path, Assert.Single(recycled));
            Assert.False(File.Exists(path));
        }
        else
        {
            Assert.Empty(recycled);
            Assert.Equal("foreign audio", File.ReadAllText(path));
            Assert.Contains(logger.Snapshot(), entry => entry.Message.Contains(name, StringComparison.Ordinal)
                && entry.Message.Contains("ownership", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Old_layout_moves_owned_files_recycles_stale_files_and_preserves_foreign_and_locked_files()
    {
        var project = new ChartProject { AudioFileName = "song.flac" };
        var path = Path.Combine(_dir, "project.dcproj");
        var owned = new Dictionary<string, string>();
        foreach (var name in new[] { "project.eof", "project.rs2dlc", "project.rscp" })
        {
            Write(name, name.EndsWith(".rs2dlc", StringComparison.Ordinal) ? "{\"owned\":true}" : "owned " + name);
            owned[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_dir, name))));
        }
        Write(".dotcharter-companions.json", JsonSerializer.Serialize(new { Version = 1, Files = owned }));
        foreach (var name in new[] { "project.dcproj", "song.flac", "foreign.wem", "notes.txt", "song.flac.pcm.wem", "song.wem", "song.preview.wem", "song.preview.wav", ".customsforge-lookup.json", "project.customsforge.json", "project.customsforge.fill.js", "project.rscp.bak", "project.rscp.2.bak" })
            Write(name, "input " + name);
        foreach (var name in new[] { "song.wem", "song.preview.wem", "song.preview.wav" })
            owned[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_dir, name))));
        Write(".dotcharter-companions.json", JsonSerializer.Serialize(new { Version = 1, Files = owned }));
        Directory.CreateDirectory(Path.Combine(_dir, "backups"));
        Directory.CreateDirectory(Path.Combine(_dir, "stems"));
        var before = Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(17, before.Length);
        var recycled = new List<string>();
        var logger = new FakeAppLogger();
        void Recycle(string file)
        {
            recycled.Add(Path.GetFileName(file));
            File.Move(file, Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "recycle")).FullName, Path.GetFileName(file)));
        }
        using (var locked = new FileStream(Path.Combine(_dir, "project.eof"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Tidy(path, project, logger, Recycle);
            Assert.True(File.Exists(Path.Combine(_dir, "project.eof")));
            Assert.False(File.Exists(Path.Combine(_dir, "export", "project.eof")));
        }
        Tidy(path, project, logger, Recycle);
        Assert.Equal(new[] { "foreign.wem", "notes.txt", "project.dcproj", "song.flac" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(new[] { "project.eof", "project.rs2dlc", "project.rscp" },
            Directory.GetFiles(Path.Combine(_dir, "export")).Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(8, recycled.Count);
        Assert.True(File.Exists(Path.Combine(_dir, ".dotcharter", "companions.json")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "backups")));
        Assert.Equal(new[] { "input project.rscp.2.bak", "input project.rscp.bak" },
            Directory.GetFiles(Path.Combine(_dir, "backups")).Select(File.ReadAllText).Order().ToArray());
        Assert.True(Directory.Exists(Path.Combine(_dir, "stems")));
        Tidy(path, project, logger, Recycle);
        Assert.Equal(8, recycled.Count);
    }

    [Fact]
    public void Edited_or_unstamped_companions_and_unrelated_backups_are_left_alone()
    {
        Write("project.dcproj", "project");
        Write("project.eof", "edited");
        Write("project.rs2dlc", "foreign");
        Write("other.rscp.bak", "foreign backup");
        Write(".dotcharter-companions.json", "{\"Version\":1,\"Files\":{\"project.eof\":\"old hash\"}}");
        Tidy(Path.Combine(_dir, "project.dcproj"), new ChartProject(), new FakeAppLogger(),
            file => Assert.Equal(".dotcharter-companions.json", Path.GetFileName(file)));
        Assert.Equal("edited", File.ReadAllText(Path.Combine(_dir, "project.eof")));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(_dir, "project.rs2dlc")));
        Assert.Equal("foreign backup", File.ReadAllText(Path.Combine(_dir, "other.rscp.bak")));
    }

    [Fact]
    public void Only_valid_owned_chats_move_and_foreign_conversations_stay_in_place()
    {
        Write("project.dcproj", "project");
        var project = new ChartProject();
        var chat = AiChatConversation.Start(project.ProjectId, "model", DateTimeOffset.UnixEpoch);
        var old = Directory.CreateDirectory(Path.Combine(_dir, "ai-chats", project.ProjectId)).FullName;
        File.WriteAllBytes(Path.Combine(old, chat.Id + ".json"), AiChatJson.Serialize(chat));
        File.WriteAllText(Path.Combine(old, "foreign.json"), "foreign");
        Tidy(Path.Combine(_dir, "project.dcproj"), project, new FakeAppLogger(), _ => Assert.Fail("Foreign file recycled"));
        Assert.True(File.Exists(Path.Combine(_dir, ".dotcharter", "ai-chats", project.ProjectId, chat.Id + ".json")));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(old, "foreign.json")));
        Assert.False(File.Exists(Path.Combine(old, chat.Id + ".json")));
    }

    private static void Tidy(string path, ChartProject project, FakeAppLogger logger, Action<string> recycle) =>
        ProjectFolderTidy.Run(path, project, logger, recycle);

    [Fact]
    public void Metadata_named_builder_projects_move_with_their_xml_and_game_named_package()
    {
        Write("project.dcproj", "project");
        var project = new ChartProject { Chart = { Artist = "Artist", SongName = "Song" } };
        var old = DlcBuilderProjectFile.PathIn(_dir, project);
        const string builder = "{\"Arrangements\":[{\"Xml\":\"DTRS XML/1_Lead_Main_RS2.xml\"}]}";
        CompanionOwnership.WriteIfUnmodified(old, file => File.WriteAllText(file, builder), out _);
        var bundle = Directory.CreateDirectory(Path.Combine(_dir, "DTRS XML")).FullName;
        File.WriteAllText(Path.Combine(bundle, "1_Lead_Main_RS2.xml"), "<song/>");
        CompanionOwnership.AdoptMovedFile(Path.Combine(bundle, "1_Lead_Main_RS2.xml"));
        var packageName = RocksmithDlcKey.Create("Artist", "Song") + "_p.psarc";
        Write(packageName, "package");
        CompanionOwnership.AdoptMovedFile(Path.Combine(_dir, packageName));
        Tidy(Path.Combine(_dir, "project.dcproj"), project, new FakeAppLogger(),
            directory => Directory.Move(directory, Path.Combine(_dir, ".dotcharter", "old-bundle")));
        Assert.False(File.Exists(old));
        Assert.Equal(builder, File.ReadAllText(Path.Combine(_dir, "export", "project.rs2dlc")));
        Assert.Equal("<song/>", File.ReadAllText(Path.Combine(_dir, "export", "DTRS XML", "1_Lead_Main_RS2.xml")));
        Assert.Equal("package", File.ReadAllText(Path.Combine(_dir, "export", "Artist_Song_v1_p.psarc")));
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    [Fact]
    public void A_supplied_wem_is_never_recycled_as_a_legacy_cache()
    {
        Write("project.dcproj", "project");
        Write("song.wem", "source");
        Tidy(Path.Combine(_dir, "project.dcproj"), new ChartProject { AudioFileName = "song.wem" },
            new FakeAppLogger(), _ => Assert.Fail("The working audio must remain in place"));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_dir, "song.wem")));
    }

    [Theory]
    [InlineData("m4a")]
    [InlineData("aac")]
    [InlineData("wma")]
    public void Every_declared_source_extension_uses_the_known_legacy_cache_scheme(string extension)
    {
        Write("project.dcproj", "project");
        var source = "song." + extension;
        Write(source, "source");
        var cache = source + ".vorbis.decode-v2.wem";
        Write(cache, "cache");
        Tidy(Path.Combine(_dir, "project.dcproj"), new ChartProject { AudioFileName = source }, new FakeAppLogger(),
            file => File.Move(file, Path.Combine(_dir, "recycled-cache")));
        Assert.True(File.Exists(Path.Combine(_dir, source)));
        Assert.False(File.Exists(Path.Combine(_dir, cache)));
    }

    [Fact]
    public void Locked_xml_is_retried_and_existing_export_destinations_keep_both_versions()
    {
        Write("project.dcproj", "project");
        var project = new ChartProject { Chart = { Artist = "Artist", SongName = "Song" } };
        var builder = Path.Combine(_dir, "project.rs2dlc");
        CompanionOwnership.WriteIfUnmodified(builder, file => File.WriteAllText(file,
            "{\"Xml\":\"DTRS XML/1_Lead_Main_RS2.xml\"}"), out _);
        var bundle = Directory.CreateDirectory(Path.Combine(_dir, "DTRS XML")).FullName;
        var xml = Path.Combine(bundle, "1_Lead_Main_RS2.xml");
        CompanionOwnership.WriteIfUnmodified(xml, file => File.WriteAllText(file, "<song/>"), out _);
        var packageName = RocksmithDlcKey.Create("Artist", "Song") + "_p.psarc";
        Write(packageName, "foreign package");
        using (var locked = new FileStream(xml, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Tidy(Path.Combine(_dir, "project.dcproj"), project, new FakeAppLogger(), _ => Assert.Fail("Locked XML must be left"));
            Assert.True(File.Exists(builder));
        }
        Tidy(Path.Combine(_dir, "project.dcproj"), project, new FakeAppLogger(),
            directory => Directory.Move(directory, Path.Combine(_dir, ".dotcharter", "old-bundle")));
        Assert.False(File.Exists(builder));
        Assert.True(File.Exists(Path.Combine(_dir, "export", "DTRS XML", Path.GetFileName(xml))));
        Assert.Equal("foreign package", File.ReadAllText(Path.Combine(_dir, "export", "Artist_Song_v1_p.psarc")));
        Directory.CreateDirectory(bundle);
        File.WriteAllText(xml, "foreign XML");
        CompanionOwnership.WriteIfUnmodified(builder, file => File.WriteAllText(file,
            "{\"Xml\":\"DTRS XML/1_Lead_Main_RS2.xml\"}"), out _);
        Tidy(Path.Combine(_dir, "project.dcproj"), project, new FakeAppLogger(), _ => Assert.Fail("Foreign XML must be left"));
        Assert.Equal("foreign XML", File.ReadAllText(xml));
        Assert.True(File.Exists(builder));
        Assert.False(CompanionOwnership.IsUnmodified(xml));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
