using DotCharter.Services.Editing.Anchors;

namespace DotCharter.Services.Editing.Validation;

/// <summary>The fret phrase of an index-finger finding ("fret 4", "fret 6 or 7", "frets 5 to 7") as a
/// translatable fragment, mirroring <see cref="IndexFret.Frets" />.</summary>
internal static class IndexFretText
{
    public static IssueTextRef Of(IndexFret index) =>
        index.Highest == index.Fret ? new IssueTextRef("Validation_FretOne", [index.Fret])
        : index.Highest == index.Fret + 1 ? new IssueTextRef("Validation_FretPair", [index.Fret, index.Highest])
        : new IssueTextRef("Validation_FretRange", [index.Fret, index.Highest]);
}
