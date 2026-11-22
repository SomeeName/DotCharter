using DotCharter.Services.Export.Sheet;
using SkiaSharp;

namespace DotCharter.Tests.Services.Export.Sheet;

public sealed class SheetChunkOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRender_ReleasesEveryCreatedImage(bool cancelled)
    {
        using var first = CreateImage();
        using var second = CreateImage();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = Record.Exception(() => SheetMusicRenderer.RenderWithChunks(chunks =>
        {
            chunks.Add(new SheetMusicRenderer.SheetChunk(0, 0, 1, 1, first));
            chunks.Add(new SheetMusicRenderer.SheetChunk(1, 0, 1, 1, second));
            if (cancelled) cts.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("bounds extraction failed");
        }));

        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(IntPtr.Zero, first.Handle);
        Assert.Equal(IntPtr.Zero, second.Handle);
    }

    [Fact]
    public void UnavailableRender_ReleasesItsImages()
    {
        using var image = CreateImage();

        var result = SheetMusicRenderer.RenderWithChunks(chunks =>
        {
            chunks.Add(new SheetMusicRenderer.SheetChunk(0, 0, 1, 1, image));
            return null;
        });

        Assert.Null(result);
        Assert.Equal(IntPtr.Zero, image.Handle);
    }

    [Fact]
    public void SuccessfulRender_TransfersImagesToTheRetainedResult()
    {
        using var image = CreateImage();
        var result = SheetMusicRenderer.RenderWithChunks(chunks =>
        {
            chunks.Add(new SheetMusicRenderer.SheetChunk(0, 0, 1, 1, image));
            return new SheetMusicRenderer.RenderResult(chunks, 1, 1, []);
        });
        Assert.NotNull(result);
        var retained = result.Retain();
        try
        {
            result.Dispose();
            Assert.NotEqual(IntPtr.Zero, image.Handle);
        }
        finally
        {
            retained.Dispose();
        }
        Assert.Equal(IntPtr.Zero, image.Handle);
    }

    private static SKImage CreateImage()
    {
        using var bitmap = new SKBitmap(1, 1);
        return SKImage.FromBitmap(bitmap);
    }
}
