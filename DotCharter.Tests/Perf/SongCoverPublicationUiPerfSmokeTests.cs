using Avalonia;
using Avalonia.Media.Imaging;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.Perf;

/// <summary>Publishes real decoded bitmaps after recycling or releasing a library row,
/// and checks the shared cache charges only results that the row still owns.</summary>
[Collection("HeadlessUI")]
public sealed class SongCoverPublicationUiPerfSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ADecodedCoverIsChargedToTheParkOrDroppedAfterRelease(bool released) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var item = new PsarcSongListItem(new LibrarySongEntry
            {
                SongKey = "cover-publication", Title = "Synthetic cover", Artist = "Test"
            }, FakeLocalization.English());
            var generation = item.CoverGeneration;
            var before = SongCoverCache.Shared.ParkedBytes;
            var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96));
            if (released) item.DisposeCoverImage();
            try
            {
                item.PublishCover(bitmap, generation);
                Assert.Equal(released ? before : before + 16, SongCoverCache.Shared.ParkedBytes);
                if (released) Assert.Null(item.CoverImage);
                else Assert.Same(bitmap, item.CoverImage);
            }
            finally
            {
                item.DisposeCoverImage();
                bitmap.Dispose();
            }

            Assert.Equal(before, SongCoverCache.Shared.ParkedBytes);
        }, CancellationToken.None);
}
