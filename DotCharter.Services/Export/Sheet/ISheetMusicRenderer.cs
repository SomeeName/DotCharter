namespace DotCharter.Services.Export.Sheet;

/// <summary>Produces owned sheet images; the caller disposes a result or transfers it to the canvas.</summary>
public interface ISheetMusicRenderer
{
    Task<SheetMusicRenderer.RenderResult?> RenderAsync(string tex, double width, double displayScale, int staves,
        CancellationToken cancellationToken);

    Task<SheetMusicRenderer.RenderResult?> RenderPagedAsync(string tex, double pageWidth, double displayScale, int staves,
        CancellationToken cancellationToken, SheetMusicRenderer.InkPalette palette = SheetMusicRenderer.InkPalette.Screen);
}
