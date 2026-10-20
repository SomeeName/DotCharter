using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf.InspectorCapture;

/// <summary>
///     #601: the seeded project the Inspector's review captures (every tab, every Sound-tab
///     state) are drawn over, and the capture plumbing. Opt-in — nothing here runs unless
///     <see cref="Enabled" /> and the session draws with Skia (<c>DOTCHARTER_UI_SWEEP_DUMP=1</c>).
/// </summary>
internal static class InspectorCaptureFixture
{
    public const string EnableVar = "DOTCHARTER_INSPECTOR_CAPTURE";
    public const double DefaultWidth = 600;
    public const double DefaultHeight = 524;
    public const double NarrowWidth = 380;

    /// <summary>Real grips (string 0 = low E), so a template's name matches its shape; the last
    /// one is the unnamed grip "needs work" counts.</summary>
    private static readonly (string Name, Dictionary<int, int> Frets)[] Voicings =
    [
        ("Am", new() { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 }),
        ("C", new() { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 }),
        ("G", new() { [0] = 3, [1] = 2, [2] = 0, [3] = 0, [4] = 0, [5] = 3 }),
        ("Em", new() { [0] = 0, [1] = 2, [2] = 2, [3] = 0, [4] = 0, [5] = 0 }),
        ("D", new() { [2] = 0, [3] = 2, [4] = 3, [5] = 2 }),
        ("F", new() { [0] = 1, [1] = 3, [2] = 3, [3] = 2, [4] = 1, [5] = 1 }),
        ("E5", new() { [0] = 0, [1] = 2, [2] = 2 }),
        ("A5", new() { [1] = 0, [2] = 2, [3] = 2 }),
        ("Cadd9", new() { [1] = 3, [2] = 2, [3] = 0, [4] = 3, [5] = 3 }),
        ("", new() { [1] = 7, [2] = 9, [3] = 9, [4] = 8 })
    ];

    public static readonly string RawLyrics =
        string.Join('\n', "Hold the line through the night", "Ev-ery light is burn-ing out", "Sing it back to me",
            "One more time be-fore the dawn");

    private static readonly string[] AnnotationTexts =
    [
        "Check the bend release here - the original drops a quarter tone before the slide into the chorus",
        "Re-time the chorus phrase after the tempo map fix",
        "Harmonics at fret 12 sound off, try pinch harmonics instead",
        "Verse 2 riff matches verse 1 except the last bar",
        "Tone change for the solo",
        "Mark"
    ];

    private static readonly string[][] LyricLines =
    [
        ["Hold", "the", "line", "through", "the", "night"],
        ["Ev-", "ery", "light", "is", "burn-", "ing", "out"],
        ["Sing", "it", "back", "to", "me"],
        ["One", "more", "time", "be-", "fore", "the", "dawn"]
    ];

    public static bool Enabled =>
        Environment.GetEnvironmentVariable(EnableVar) == "1" && UiSweepRunner.CaptureEnabled;

    public static string OutputDirectory =>
        Environment.GetEnvironmentVariable("DOTCHARTER_INSPECTOR_CAPTURE_OUT") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "TestResults", "inspector-capture");

    public static IServiceProvider Services =>
        App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");

    /// <summary>A two-minute, 240-sound chart with real chord names and fingers (some missing,
    /// so the Issues tab and "needs work" have findings), a vocal track, sections and
    /// phrases, tone changes and six annotations.</summary>
    public static ChartLevel Seed(ProjectState state)
    {
        var chart = StressChartFactory.Chart(soundCount: 240, lengthMinutes: 2);
        var project = new ChartProject
        {
            Chart = { SongName = "Northern Lights", Artist = "The Night Shift", AlbumName = "Afterglow", AlbumYear = "2019" }
        };
        foreach (var beat in chart.Beats) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        var level = new ChartLevel { DifficultyIndex = 0 };
        foreach (var note in chart.Notes) level.Notes.Add(note);
        foreach (var chord in chart.Chords) level.Chords.Add(chord);
        foreach (var shape in chart.HandShapes) level.HandShapes.Add(shape);
        foreach (var anchor in chart.Anchors) level.Anchors.Add(anchor);
        arrangement.Levels.Add(level);
        foreach (var point in chart.EventPoints) arrangement.EventPoints.Add(point);
        foreach (var tone in chart.Tones) arrangement.ToneChanges.Add(tone);

        var templates = new Dictionary<int, ChordTemplate>();
        var index = 0;
        foreach (var chord in level.Chords)
        {
            var voicing = index++ % Voicings.Length;
            var (name, frets) = Voicings[voicing];
            chord.ChordNotes.Clear();
            foreach (var (s, f) in frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            if (!templates.TryGetValue(voicing, out var template))
            {
                template = new ChordTemplate { Id = templates.Count, Name = name, Frets = new Dictionary<int, int>(frets) };
                if (voicing % 4 != 1) ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
                templates[voicing] = template;
                arrangement.ChordTemplates.Add(template);
            }

            chord.TemplateId = template.Id;
        }

        SeedPhrases(arrangement);
        project.Arrangements.Add(arrangement);
        SeedAnnotations(project);
        SeedLyrics(project);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        return level;
    }

    private static void SeedPhrases(Arrangement arrangement)
    {
        arrangement.Phrases.Clear();
        arrangement.PhraseIterations.Clear();
        arrangement.Phrases.Add("COUNT");
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        foreach (var point in arrangement.EventPoints.Where(p => p.Phrase.Length > 0))
        {
            var id = arrangement.Phrases.IndexOf(point.Phrase);
            if (id < 0)
            {
                id = arrangement.Phrases.Count;
                arrangement.Phrases.Add(point.Phrase);
            }

            arrangement.PhraseIterations.Add(new PhraseIteration(point.Position, id));
        }
    }

    private static void SeedAnnotations(ChartProject project)
    {
        for (var i = 0; i < AnnotationTexts.Length; i++)
        {
            project.Annotations.Add(new ChartAnnotation
            {
                TimeMs = 4_000 + (i * 9_500),
                Kind = (ChartAnnotationKind)(i % 3),
                Text = AnnotationTexts[i],
                Resolved = i == 3
            });
        }
    }

    private static void SeedLyrics(ChartProject project)
    {
        var track = new VocalTrack { Name = "Lead vocals" };
        var beat = 8;
        foreach (var line in LyricLines)
        {
            var phrase = new LyricPhrase { Position = new FractionalPosition(beat) };
            foreach (var word in line)
            {
                phrase.Lyrics.Add(new LyricEvent
                {
                    Position = new FractionalPosition(beat),
                    EndPosition = new FractionalPosition(beat + 1),
                    Text = word.TrimEnd('-'),
                    IsWordPart = word.EndsWith('-'),
                    Note = 60 + (beat % 7)
                });
                beat++;
            }

            phrase.Lyrics[^1].IsPhraseEnd = true;
            phrase.EndPosition = new FractionalPosition(beat);
            track.Phrases.Add(phrase);
            beat += 4;
        }

        project.VocalTracks.Add(track);
    }

    /// <summary>What lives outside the project: the per-user riff library, the typed lyric sheet
    /// and the mixer's levels.</summary>
    public static void SeedApp(InspectorViewModel vm, Arrangement arrangement, ChartLevel level)
    {
        var catalog = Services.GetRequiredService<IPhraseCatalogService>();
        foreach (var entry in catalog.Entries.ToList()) catalog.Remove(entry);
        string[] riffs = ["Intro lick", "Verse riff (palm-muted)", "Chorus chords", "Solo run, bar 9", "Outro bend"];
        for (var i = 0; i < riffs.Length; i++)
        {
            var notes = level.Notes.Skip(i * 12).Take(6 + (i * 3)).ToList();
            var chords = level.Chords.Skip(i * 4).Take(i % 2 == 0 ? 2 : 0).ToList();
            catalog.Add(riffs[i], arrangement, notes, chords, [], level.Anchors.Take(1));
        }

        vm.RiffLibrary?.EnsureLoaded();
        if (vm.RiffLibrary is { } library && library.Entries.Count > 1) library.SelectedEntry = library.Entries[1];
        vm.Lyrics.RawLyrics = RawLyrics;
        vm.Transport.Volume = 0.85;
        vm.Transport.InputVolume = 0.7;
        vm.Transport.MidiVolume = 0.6;
    }

    public static ChordTemplate? TemplateOf(Arrangement arrangement, Chord chord) =>
        arrangement.ChordTemplates.FirstOrDefault(t => t.Id == chord.TemplateId);

    public static void Select(ISelectionService selection, object? sound)
    {
        selection.ClearSelections();
        if (sound is GuitarNote note) selection.SelectedNotes.Add(note);
        else if (sound is Chord chord) selection.SelectedChords.Add(chord);
    }

    public static Window NewWindow(double width, double height, Control content)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        if (Application.Current is { } app
            && app.TryGetResource("AppBackground", app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            window.Background = brush;
        }

        return window;
    }

    public static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static void Save(Window window, string subdirectory, string name)
    {
        var dir = Path.Combine(OutputDirectory, subdirectory);
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"), new PngBitmapEncoderOptions());
    }

    public static InspectorViewModel Inspector => Services.GetRequiredService<InspectorViewModel>();
}
