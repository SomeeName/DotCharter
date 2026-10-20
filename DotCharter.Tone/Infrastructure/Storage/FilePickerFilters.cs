namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
/// Catalog of the reusable <see cref="FilePickerFilter" /> definitions that were
/// otherwise re-declared inline across the dialog view models (the "All Files"
/// filter appeared ~11 times; the Guitar Pro extension array twice).
/// <see cref="FilePickerFilter" /> is an immutable record, so these shared
/// instances are safe to hand to any picker.
/// </summary>
public static class FilePickerFilters
{
    /// <summary>The catch-all "*.*" filter every open dialog offers as a fallback.</summary>
    public static FilePickerFilter AllFiles { get; } = new("All Files", new[] { "*.*" });

    /// <summary>Every Guitar Pro container extension the importer understands.</summary>
    public static FilePickerFilter GuitarPro { get; } = new("Guitar Pro",
        new[] { "*.gp", "*.gpx", "*.gp7", "*.gpa", "*.gp5", "*.gp4", "*.gp3" });
}
