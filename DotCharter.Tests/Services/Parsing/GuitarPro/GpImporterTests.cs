using System.IO.Compression;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public sealed class GpImporterTests
{
    [Theory]
    [InlineData(".gp7")]
    [InlineData(".gpa")]
    public void GetAvailableTracks_RecognizesModernContainerExtensions(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("content/score.gpif");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""
                             <GPIF>
                               <Tracks>
                                 <Track id="1"><Name>Lead</Name></Track>
                                 <Track id="2"><Name>Bass</Name></Track>
                               </Tracks>
                             </GPIF>
                             """);
            }

            var tracks = new GpImporter().GetAvailableTracks(path);

            Assert.Collection(tracks,
                // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
                track =>
                {
                    Assert.Equal(1, track.Id);
                    Assert.Equal("Lead", track.Name);
                },
                // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
                track =>
                {
                    Assert.Equal(2, track.Id);
                    Assert.Equal("Bass", track.Name);
                });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
