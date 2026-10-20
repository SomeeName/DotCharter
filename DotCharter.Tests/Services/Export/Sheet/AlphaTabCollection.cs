namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     AlphaTab's first <c>Settings</c> construction lazily fills shared static
///     engraving defaults, and that initializer enumerates a list while mutating
///     it. xUnit runs test classes in parallel across collections, so two sheet
///     test classes hitting the cold static init at once threw
///     "Collection was modified; enumeration operation may not execute" — a flake
///     that failed roughly one run in three.
///
///     Every test class that touches AlphaTab joins this collection so they run
///     serially. The production path is protected separately by
///     <c>SheetMusicRenderer.CreateSettings</c>.
/// </summary>
[CollectionDefinition("AlphaTab", DisableParallelization = true)]
public class AlphaTabCollection;
