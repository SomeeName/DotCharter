using DotCharter.RigLab;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.RigLab;

/// <summary>
///     <c>riglab.json</c> follows the rule every JSON store here follows: absent
///     starts fresh, corrupt is moved aside, unreadable is presumed intact and is
///     never overwritten by defaults.
/// </summary>
public sealed class RigLabConfigServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "riglab-config-" + Guid.NewGuid().ToString("N"));

    public RigLabConfigServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string ConfigPath => Path.Combine(_dir, RigLabConfigService.FileName);

    [Fact]
    public void AnAbsentFile_StartsWithLiveInputOn()
    {
        using var service = new RigLabConfigService(ConfigPath);

        var config = service.Load();

        Assert.True(config.EnableInput, "RigLab exists to hear the guitar: a fresh install starts with live input on");
        Assert.Equal("system", config.LanguageCulture);
        Assert.Empty(config.GuitarVstChain);
    }

    [Fact]
    public void AnUpdate_ReachesTheFile_AndASecondServiceReadsItBack()
    {
        using (var service = new RigLabConfigService(ConfigPath, TimeSpan.Zero))
        {
            service.Update(c =>
            {
                c.AudioDriver = "ASIO";
                c.AudioBufferFrames = 128;
                c.GuitarVstChain.Add(new() { PluginPath = @"C:\Plugins\Chorus.vst3" });
            });
            service.Flush();
        }

        using var reread = new RigLabConfigService(ConfigPath);
        var config = reread.Load();
        Assert.Equal("ASIO", config.AudioDriver);
        Assert.Equal(128, config.AudioBufferFrames);
        Assert.Equal(@"C:\Plugins\Chorus.vst3", Assert.Single(config.GuitarVstChain).PluginPath);
    }

    [Fact]
    public void TheRigSeesTheSameInstance_ThroughIRigConfigService()
    {
        using var service = new RigLabConfigService(ConfigPath);
        IRigConfigService rig = service;

        rig.UpdateRigConfig(c => c.TunerReferenceHz = 442);

        Assert.Same(service.Load(), rig.LoadRigConfig());
        Assert.Equal(442, service.Load().TunerReferenceHz);
    }

    [Fact]
    public void ACorruptFile_IsMovedAside_AndTheAppStartsFresh()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");
        using var service = new RigLabConfigService(ConfigPath);

        var config = service.Load();

        Assert.True(config.EnableInput);
        Assert.False(service.IsUnreadable);
        Assert.False(File.Exists(ConfigPath), "the corrupt file must not stay where the next save lands");
        var aside = Assert.Single(Directory.GetFiles(_dir, "riglab.corrupt-*.json"));
        Assert.Equal("{ this is not json", File.ReadAllText(aside));
    }

    [Fact]
    public void AnUnreadableFile_IsNeverOverwrittenByDefaults()
    {
        File.WriteAllText(ConfigPath, """{ "AudioDriver": "ASIO", "AudioBufferFrames": 64 }""");
        using var service = new RigLabConfigService(ConfigPath, TimeSpan.Zero);

        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var served = service.Load();
            Assert.True(service.IsUnreadable);
            Assert.Equal("WASAPI", served.AudioDriver); // a default, served but not trusted

            service.Update(c => c.AudioBufferFrames = 512);
            service.Flush();
        }

        // The file the player had is untouched, and the next read sees it.
        Assert.Contains("\"AudioBufferFrames\": 64", File.ReadAllText(ConfigPath));
        var reread = service.Load();
        Assert.False(service.IsUnreadable);
        Assert.Equal("ASIO", reread.AudioDriver);
        Assert.Equal(64, reread.AudioBufferFrames);
    }
}
