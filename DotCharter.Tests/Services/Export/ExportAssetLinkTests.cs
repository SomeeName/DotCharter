using DotCharter.Services.Export;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Services.Export;

public sealed class ExportAssetLinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("export_link_").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Media_uses_a_hard_link_or_falls_back_to_a_copy(bool failLink)
    {
        var source = Path.Combine(_dir, "song.ogg");
        var target = Path.Combine(_dir, "export", "song.ogg");
        File.WriteAllText(source, "audio");
        var attempts = 0;
        var copies = 0;
        void Link(string link, string existing)
        {
            attempts++;
            if (failLink) throw new IOException("unsupported volume");
            File.Copy(existing, link);
        }
        void Copy(string existing, string copy)
        {
            copies++;
            File.Copy(existing, copy);
        }
        ExportAssetLink.Publish(source, target, Link, Copy);
        Assert.Equal(1, attempts);
        Assert.Equal("audio", File.ReadAllText(target));
        Assert.Equal(failLink ? 1 : 0, copies);
        File.WriteAllText(target, "edited by another tool");
        ExportAssetLink.Publish(source, target, Link);
        Assert.Equal(1, attempts);
        Assert.Equal("edited by another tool", File.ReadAllText(target));
    }

    [Fact]
    public void A_native_hard_link_keeps_its_file_identity_when_published()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows hard links.");
            return;
        }
        var source = Path.Combine(_dir, "source.ogg");
        var probe = Path.Combine(_dir, "probe.ogg");
        var target = Path.Combine(_dir, "export", "song.ogg");
        File.WriteAllText(source, "audio");
        try { Win32GameMirrorFileSystem.Instance.CreateHardLink(probe, source); }
        catch (IOException ex) { Assert.Skip("Hard links are unavailable: " + ex.Message); }
        Assert.SkipWhen(Win32GameMirrorFileSystem.Instance.FileId(source) != Win32GameMirrorFileSystem.Instance.FileId(probe),
            "This filesystem does not preserve native hard-link identity.");
        ExportAssetLink.Publish(source, target);
        Assert.Equal(Win32GameMirrorFileSystem.Instance.FileId(source), Win32GameMirrorFileSystem.Instance.FileId(target));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
