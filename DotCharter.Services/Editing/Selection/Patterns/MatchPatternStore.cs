namespace DotCharter.Services.Editing.Selection.Patterns;

/// <summary>
///     CRUD over <see cref="AppConfig.MatchPatterns" />, in the shape
///     the config stores established: every mutation goes through
///     <see cref="IConfigurationService.Update" /> so the read-modify-write of a
///     COLLECTION member is atomic under the config lock, and everything handed
///     back is a defensive clone.
/// </summary>
public sealed class MatchPatternStore
{
    private readonly IConfigurationService _config;

    public MatchPatternStore(IConfigurationService config) => _config = config;

    /// <summary>All saved patterns, in stored order.</summary>
    public IReadOnlyList<MatchPattern> List()
    {
        var config = _config.LoadAppConfig();
        return config.MatchPatterns.Select(p => p.Clone()).ToList();
    }

    /// <summary>
    ///     Writes the starter set exactly ONCE per install and returns whatever
    ///     is stored afterwards. The seeded flag is what makes it once: a user who
    ///     deletes every built-in must not find them back after the next launch,
    ///     and an empty list is a legitimate state.
    /// </summary>
    public IReadOnlyList<MatchPattern> EnsureSeeded()
    {
        _config.Update(config =>
        {
            if (config.MatchPatternsSeeded) return;
            config.MatchPatternsSeeded = true;
            if (config.MatchPatterns.Count > 0) return;
            foreach (var pattern in MatchPatternCatalog.BuiltIns())
                config.MatchPatterns.Add(pattern.Normalized());
        });
        return List();
    }

    /// <summary>Adds the pattern, or replaces the stored one with the same id.</summary>
    public MatchPattern Save(MatchPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var normalized = pattern.Normalized();
        _config.Update(config =>
        {
            var index = config.MatchPatterns.FindIndex(p => p.Id == normalized.Id);
            if (index >= 0) config.MatchPatterns[index] = normalized;
            else config.MatchPatterns.Add(normalized);
        });
        return normalized.Clone();
    }

    /// <summary>
    ///     Replaces the whole list in one write — what the builder dialog's Save
    ///     does, so a session of adding, editing, reordering and deleting lands as
    ///     a single atomic config update rather than a burst of per-row writes
    ///     that a crash could leave half applied.
    /// </summary>
    public IReadOnlyList<MatchPattern> ReplaceAll(IEnumerable<MatchPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var normalized = patterns.Select(p => p.Normalized()).ToList();
        _config.Update(config =>
        {
            config.MatchPatterns.Clear();
            config.MatchPatterns.AddRange(normalized);
            // Replacing the list is an explicit statement about what should
            // exist, so it also counts as having been seeded — otherwise
            // "delete them all and save" is undone on the next launch.
            config.MatchPatternsSeeded = true;
        });
        return normalized.Select(p => p.Clone()).ToList();
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var removed = false;
        _config.Update(config => { removed = config.MatchPatterns.RemoveAll(p => p.Id == id) > 0; });
        return removed;
    }

    /// <summary>The stored pattern with this id, or null.</summary>
    public MatchPattern? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _config.LoadAppConfig().MatchPatterns.FirstOrDefault(p => p.Id == id)?.Clone();
    }
}
