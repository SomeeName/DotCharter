namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>One chord of a hand shape, in time order, with the grip it plays.</summary>
/// <param name="Chord">The chord.</param>
/// <param name="Grip">The template the chord plays (or is about to play).</param>
/// <param name="Assigned">
///     True when the running flow is the one giving the chord this grip (an import, or Generate hand
///     shapes for a chord that had none). Only an assigned chord is ever repointed, so a chord a
///     charter already pointed at a template keeps it.
/// </param>
public readonly record struct ShapeChord(Chord Chord, ChordTemplate Grip, bool Assigned);

/// <summary>The unnamed twins to add and the chords to repoint at them.</summary>
public sealed record RepeatedChordNamePlan(
    IReadOnlyList<ChordTemplate> Twins,
    IReadOnlyList<(Chord Chord, int TemplateId)> Repoints)
{
    public bool IsEmpty => Repoints.Count == 0;
}

/// <summary>
///     "Name only the first chord of a hand shape" (<c>AppConfig.NameOnlyFirstChordInHandShape</c>, on by
///     default): within one hand shape only the first chord shows its name. A later chord that plays the
///     same NAMED grip again points to an unnamed copy of it instead (same frets, fingers and flags, an
///     empty <see cref="ChordTemplate.Name" />, <see cref="ChordTemplate.Unnamed" /> so no automatic flow
///     names it back), reusing an unnamed twin the arrangement already holds. Every exporter writes an
///     empty name as empty, so the game draws the repeat without a label, the way hand-made charts do.
///     <para>
///         Runs only where a flow assigns grips: Guitar Pro import and Generate hand shapes. Loading a
///         project never runs it, and a chord that already played a template is never repointed.
///     </para>
/// </summary>
public static class RepeatedChordNames
{
    private static Func<bool> _enabled = static () => true;

    /// <summary>Wires the setting; the app passes a read of its cached config. Unwired it is on.</summary>
    public static void Configure(Func<bool> enabled) =>
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));

    /// <summary>Whether repeated names are blanked right now.</summary>
    public static bool Enabled => _enabled();

    /// <summary>
    ///     The twins and repoints for <paramref name="shapes" /> (each one hand shape's chords in time order).
    ///     <paramref name="templates" /> are the arrangement's templates (plus any the flow is about to add),
    ///     searched for an unnamed twin before one is minted at <paramref name="nextId" /> and up.
    /// </summary>
    public static RepeatedChordNamePlan Plan(IReadOnlyCollection<ChordTemplate> templates,
        IEnumerable<IReadOnlyList<ShapeChord>> shapes, int nextId)
    {
        var twins = new List<ChordTemplate>();
        var twinOf = new Dictionary<ChordTemplate, ChordTemplate>(ReferenceEqualityComparer.Instance);
        var repoints = new List<(Chord, int)>();
        foreach (var shape in shapes)
        {
            var seen = new HashSet<ChordTemplate>(ReferenceEqualityComparer.Instance);
            foreach (var (chord, grip, assigned) in shape)
            {
                if (seen.Add(grip) || !assigned || string.IsNullOrEmpty(grip.Name)) continue;
                if (!twinOf.TryGetValue(grip, out var twin))
                {
                    twin = FindTwin(templates, grip) ?? FindTwin(twins, grip);
                    if (twin == null)
                    {
                        twin = MintTwin(grip, nextId++);
                        twins.Add(twin);
                    }

                    twinOf[grip] = twin;
                }

                repoints.Add((chord, twin.Id));
            }
        }

        return new RepeatedChordNamePlan(twins, repoints);
    }

    /// <summary>
    ///     Applies <see cref="Plan" /> in place over <paramref name="level" />'s hand shapes: a chord belongs to
    ///     the shape whose span holds its start. The import path, where every chord is freshly assigned.
    /// </summary>
    public static void ApplyInPlace(Arrangement arrangement, ChartLevel level)
    {
        var byId = new Dictionary<int, ChordTemplate>();
        foreach (var template in arrangement.ChordTemplates) byId.TryAdd(template.Id, template);

        var shapes = new List<IReadOnlyList<ShapeChord>>();
        foreach (var shape in level.HandShapes)
        {
            var chords = new List<ShapeChord>();
            foreach (var chord in level.Chords)
            {
                if (chord.Position.CompareTo(shape.Position) < 0 || chord.Position.CompareTo(shape.EndPosition) >= 0) continue;
                if (byId.TryGetValue(chord.TemplateId, out var grip)) chords.Add(new ShapeChord(chord, grip, true));
            }

            shapes.Add(chords);
        }

        var nextId = arrangement.ChordTemplates.Count == 0 ? 0 : arrangement.ChordTemplates.Max(t => t.Id) + 1;
        var plan = Plan(arrangement.ChordTemplates, shapes, nextId);
        foreach (var twin in plan.Twins) arrangement.ChordTemplates.Add(twin);
        foreach (var (chord, id) in plan.Repoints) chord.TemplateId = id;
    }

    private static ChordTemplate? FindTwin(IEnumerable<ChordTemplate> templates, ChordTemplate grip)
    {
        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template.Name) && template.GhostMask == grip.GhostMask && SameButName(template, grip))
                return template;
        }

        return null;
    }

    private static bool SameButName(ChordTemplate template, ChordTemplate grip)
    {
        var unnamed = grip.Clone();
        unnamed.Name = string.Empty;
        return template.Equals(unnamed);
    }

    private static ChordTemplate MintTwin(ChordTemplate grip, int id)
    {
        var twin = grip.Clone();
        twin.Id = id;
        twin.Name = string.Empty;
        twin.Unnamed = true;
        return twin;
    }
}
