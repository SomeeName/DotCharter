using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Validation;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Editing.Tones;

/// <summary>
///     The default definitions for the tone names an arrangement uses (base, slots A-D) that the project
///     defines nowhere: what the export dialog's "Create missing tone definitions" fix adds. Each is the
///     tone designer's own default tone under the used name, so it is a real, editable tone body.
/// </summary>
public static class DefaultToneDefinitions
{
    /// <summary>One definition per missing, valid tone key of <paramref name="arrangement" />, in slot order.</summary>
    public static IReadOnlyList<ToneDefinition> For(ChartProject project, Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        var (toneBase, slots) = RsToneSlots.Resolve(arrangement);
        var made = new List<ToneDefinition>();
        foreach (var key in ChartExportValidator.MissingToneDefinitions(project, toneBase, slots))
        {
            if (!RsToneAuthoring.IsValidKey(key)) continue;
            var tone = RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared);
            made.Add(new ToneDefinition { Key = key, Name = key, Json = RsToneJsonWriter.Write(tone) });
        }

        return made;
    }
}
