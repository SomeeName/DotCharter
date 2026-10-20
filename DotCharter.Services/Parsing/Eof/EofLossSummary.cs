namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     One place a loss happened: the arrangement (or <see cref="EofLosses.BeatsSubject" /> /
///     <see cref="EofLosses.VocalsSubject" />), the time in the song when it happened at one place,
///     and the value that names the item.
/// </summary>
public sealed record EofLossPlace(string Subject, double? TimeMs, string Detail);

/// <summary>
///     Every place one kind of loss happened, in report order. <see cref="Shown" /> is the
///     first <see cref="EofLossSummary.PlacesPerGroup" /> of them; <see cref="Hidden" /> how
///     many a capped list leaves out.
/// </summary>
public sealed record EofLossGroup(EofLoss Loss, EofConversionKind Kind, IReadOnlyList<EofLossPlace> Places,
    IReadOnlyList<EofLossPlace> Shown)
{
    public int Count => Places.Count;

    public int Hidden => Places.Count - Shown.Count;
}

/// <summary>
/// What an Editor on Fire export could not keep exactly, grouped by WHAT: one
///     <see cref="EofLossGroup" /> per <see cref="EofLoss" />, dropped kinds first, then the
///     most frequent. The writer reports a chart element once per difficulty level that holds
///     it, so a ladder repeats the same note twenty times; a place is counted ONCE per subject,
///     time and detail. Each group lists its first <see cref="PlacesPerGroup" /> places so a
///     big chart stays readable; <see cref="EofLossGroup.Places" /> keeps every one for "show
///     all" and the copy.
/// </summary>
public sealed class EofLossSummary
{
    /// <summary>How many places a group lists before "N more".</summary>
    public const int DefaultPlacesPerGroup = 5;

    private EofLossSummary(IReadOnlyList<EofLossGroup> groups, int placesPerGroup)
    {
        Groups = groups;
        PlacesPerGroup = placesPerGroup;
    }

    public static EofLossSummary Empty { get; } = new([], DefaultPlacesPerGroup);

    public IReadOnlyList<EofLossGroup> Groups { get; }

    public int PlacesPerGroup { get; }

    public bool IsLossless => Groups.Count == 0;

    /// <summary>Every place, over every group.</summary>
    public int Total => Groups.Sum(g => g.Count);

    public int Dropped => Groups.Where(g => g.Kind == EofConversionKind.Dropped).Sum(g => g.Count);

    public int Approximated => Groups.Where(g => g.Kind == EofConversionKind.Approximated).Sum(g => g.Count);

    public static EofLossSummary From(EofConversionReport report, int placesPerGroup = DefaultPlacesPerGroup)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThan(placesPerGroup, 1);

        // Subjects keep the order the writer met them in: arrangements in project order,
        // then the beat map and the vocals.
        var subjectOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var byLoss = new Dictionary<EofLoss, (EofConversionKind Kind, List<EofLossPlace> Places)>();
        var seen = new HashSet<(EofLoss, string, long?, string)>();
        foreach (var note in report.Notes)
        {
            subjectOrder.TryAdd(note.Subject, subjectOrder.Count);
            if (!byLoss.TryGetValue(note.Loss, out var group))
                byLoss[note.Loss] = group = (note.Kind, []);
            // The writer's times are whole milliseconds; a level's copy of a note lands on the same one.
            var key = (note.Loss, note.Subject, note.TimeMs is { } ms ? (long?)Math.Round(ms) : null, note.Detail);
            if (seen.Add(key)) group.Places.Add(new EofLossPlace(note.Subject, note.TimeMs, note.Detail));
        }

        var groups = byLoss
            .Select(pair =>
            {
                var places = pair.Value.Places
                    .OrderBy(p => subjectOrder[p.Subject])
                    .ThenBy(p => p.TimeMs ?? double.MinValue)
                    .ThenBy(p => p.Detail, StringComparer.Ordinal)
                    .ToList();
                return new EofLossGroup(pair.Key, pair.Value.Kind, places, places.Take(placesPerGroup).ToList());
            })
            .OrderBy(g => g.Kind == EofConversionKind.Dropped ? 0 : 1)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Loss)
            .ToList();
        return new EofLossSummary(groups, placesPerGroup);
    }

    /// <summary>"UnusedTone×1, BendRounded×5" — the log's line, so a save's log says WHAT, not just how many.</summary>
    public string LogLine() => string.Join(", ", Groups.Select(g => $"{g.Loss}×{g.Count}"));
}
