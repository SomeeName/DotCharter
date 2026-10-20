namespace DotCharter.Services.Editing.Fixing;

/// <summary>
///     Clears the chord name from two-string shapes that already carry one.
///     <para>
///         <b>Why this is a tool and not only a rule.</b> Suppressing the
///         AUTOMATIC namer only governs templates minted from now on. A name
///         reaches a template three other ways — a PSARC/RS-XML import carries the
///         original charter's <c>chordName</c>, a cross-arrangement paste carries
///         the source template's name, and any chart authored before the
///         suppression existed simply has them. So turning the setting on has to
///         come with a way to apply it to a chart that already exists, the same way
///         Editor on Fire pairs its preference with a chart-wide pass.
///     </para>
///     <para>
///         <b>Mutating the name in place is correct here</b>, even though a
///         <c>ChordTemplate</c> is SHARED and must never be edited in place to
///         change a SHAPE. No fret moves, so no chord's shape changes and nothing
///         is re-pointed; every chord on that shape is a double stop and wants the
///         same answer. This is the same reasoning that lets
///         the chord panel's "+ the same grip elsewhere" rung edit templates in place.
///     </para>
/// </summary>
public static class DoubleStopChordNames
{
    /// <summary>How many templates one pass would clear, without changing anything.</summary>
    public static int Count(Arrangement arrangement, DoubleStopNameOptions options)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        var count = 0;
        foreach (var template in Targets(arrangement, options)) { _ = template; count++; }
        return count;
    }

    /// <summary>
    ///     Clears the name of every two-string template the options suppress.
    ///     Returns how many templates changed.
    /// </summary>
    public static int Strip(Arrangement arrangement, DoubleStopNameOptions options)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        var cleared = 0;
        foreach (var template in Targets(arrangement, options).ToList())
        {
            template.Name = string.Empty;
            cleared++;
        }

        return cleared;
    }

    /// <summary>Clears across every arrangement of a project.</summary>
    public static int Strip(ChartProject project, DoubleStopNameOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);

        var cleared = 0;
        foreach (var arrangement in project.Arrangements) cleared += Strip(arrangement, options);
        return cleared;
    }

    /// <summary>
    ///     The templates carrying a name they should not. Fret-hand-mute templates
    ///     are included: the RS-XML writer emits them through the same element and
    ///     would otherwise ship a name the chord templates do not carry.
    /// </summary>
    private static IEnumerable<ChordTemplate> Targets(Arrangement arrangement, DoubleStopNameOptions options)
    {
        foreach (var template in arrangement.ChordTemplates.Concat(arrangement.FretHandMuteTemplates))
        {
            if (string.IsNullOrEmpty(template.Name)) continue;
            if (!ChordNameSuggester.SuppressesAutoName(arrangement, template.Frets, options)) continue;
            yield return template;
        }
    }
}
