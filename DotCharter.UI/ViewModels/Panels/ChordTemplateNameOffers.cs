using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     What the chord panel OFFERS for the loaded template, never applies on its own: the name
///     list, the suggested names and the existing shapes that contain the loaded one. Lifted out
/// of <see cref="ChordTemplateEditorViewModel" /> (size budget); the panel binds it as
///     <c>Names</c> and keeps the verbs (<c>ApplySuggestedChordNameCommand</c>,
///     <c>ApplyContainingShapeCommand</c>), which edit the chart.
/// </summary>
public sealed class ChordTemplateNameOffers(ChartEditorViewModel editor, IConfigurationService? configService)
    : ObservableObject
{
    public ObservableCollection<string> KnownChordNames { get; } = [];

    public ObservableCollection<string> SuggestedChordNames { get; } = [];

    public bool HasSuggestedChordNames => SuggestedChordNames.Count > 0;

    /// <summary>
    ///     Existing shapes that CONTAIN the loaded one, offered as pills.
    ///     <para>
    ///         Generating a bar over a single note builds a one-string shape; the
    ///         charter then adds the rest of the grip by hand. The next occurrence
    ///         seeds the same one string, does not match the finished shape, and
    ///         mints another — so the grip is rebuilt every time (one session built
    ///         one identical grip <b>seven times</b>).
    ///     </para>
    ///     <para>
    ///         An OFFER, never automatic: binding here makes the bar declare a
    ///         string the note does not sound, which is a real authoring decision,
    ///         and two templates for the same frets is sometimes wanted.
    ///     </para>
    /// </summary>
    public ObservableCollection<ShapeSuggestion> ContainingShapes { get; } = [];

    public bool HasContainingShapes => ContainingShapes.Count > 0;

    /// <summary>The chord-naming style from Settings ▸ Editor, keyed to the project.</summary>
    public ChordNamingOptions ChordNaming(Arrangement arrangement) => ChordNamingOptions.For(
        configService?.LoadAppConfig().ChordNamingStyle ?? ChordNamingStyle.Eof, arrangement, editor.ActiveProject?.Chart);

    /// <summary>
    ///     Brings the name list up to date IN PLACE — removing what is gone,
    ///     adding what is new — and always offers <paramref name="current" /> (a
    ///     display-only shape carries a suggested name no template has yet). A
    ///     Clear() + refill dropped the box's selected item, and its binding wrote
    /// the resulting null back into the chord name.
    /// </summary>
    public void UpdateKnownChordNames(string current)
    {
        var wanted = new List<string>();
        if (editor.ActiveArrangement != null)
            wanted.AddRange(editor.ActiveArrangement.ChordTemplates.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)));
        if (!string.IsNullOrEmpty(current)) wanted.Add(current);
        wanted = wanted.Distinct(StringComparer.Ordinal).ToList();

        for (var i = KnownChordNames.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(KnownChordNames[i], StringComparer.Ordinal)) KnownChordNames.RemoveAt(i);
        }

        foreach (var name in wanted)
        {
            if (!KnownChordNames.Contains(name, StringComparer.Ordinal)) KnownChordNames.Add(name);
        }
    }

    public void Clear()
    {
        SuggestedChordNames.Clear();
        OnPropertyChanged(nameof(HasSuggestedChordNames));
        ContainingShapes.Clear();
        OnPropertyChanged(nameof(HasContainingShapes));
    }

    /// <summary>The suggested names and the containing shapes for <paramref name="template" />.</summary>
    public void Update(ChordTemplate template)
    {
        SuggestedChordNames.Clear();
        ContainingShapes.Clear();

        if (editor.ActiveArrangement is { } arrangement)
        {
            foreach (var name in ChordNameSuggester.SuggestChordNames(arrangement, template.Frets, ChordNaming(arrangement)).Distinct())
                SuggestedChordNames.Add(name);
            foreach (var candidate in ChordShapeSuperset.FindContaining(arrangement, template))
                ContainingShapes.Add(ShapeSuggestion.For(candidate, template));
        }

        OnPropertyChanged(nameof(HasSuggestedChordNames));
        OnPropertyChanged(nameof(HasContainingShapes));
    }
}
