namespace DotCharter.Services.Editing.Improvement;

/// <summary>
/// Keeps the retained working difficulty-slot owners while generation builds independent values.
/// Publishing through these owners preserves live view-state and earlier history references.
/// </summary>
internal sealed class GeneratedLevelOwners(Arrangement workingArrangement)
{
    private readonly ChartLevel[] _levels = [.. workingArrangement.Levels];

    public void Apply(Arrangement generated)
    {
        for (var i = 0; i < Math.Min(_levels.Length, generated.Levels.Count); i++)
        {
            ChartProjectRestorer.RestoreLevel(_levels[i], generated.Levels[i]);
            generated.Levels[i] = _levels[i];
        }
    }
}
