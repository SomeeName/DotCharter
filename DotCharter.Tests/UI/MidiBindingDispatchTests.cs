using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Input;
using DotCharter.Services.Diagnostics;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI;

/// <summary>
///     A MIDI controller press runs the verb Settings ▸ Keybinds bound to it (#310)
///     through the keybind dispatch — the same action, dialog block and usage count
///     as the verb's chord — and only where a key would.
/// </summary>
public sealed class MidiBindingDispatchTests
{
    private static readonly MidiTrigger Pad = MidiTrigger.Note(9, 36);

    private sealed class RecordingTracker : IUsageTracker
    {
        public List<(string Kind, string Id)> Records { get; } = [];
        public void Record(string kind, string id) => Records.Add((kind, id));
        public void RecordDuration(string kind, string id, long milliseconds) { }
        public UsageStatistics Snapshot { get; } = new();
        public void Flush() { }
    }

    private sealed class Rig
    {
        public KeybindConfig Table { get; set; } = new();
        public FakeMidiTriggerInput Midi { get; } = new();
        public RecordingTracker Usage { get; } = new();
        public bool CanRoute { get; set; } = true;
        public bool DialogOpen { get; set; }
        public List<string> Ran { get; } = [];
        public int Dispatched { get; private set; }
        public ChartKeybindHandler Handler { get; }
        public MidiBindingDispatch Dispatch { get; }

        public Rig()
        {
            Handler = new ChartKeybindHandler(() => DialogOpen, new FakeAppLogger(), Usage);
            Handler.Register(() => Table.PlayPause, () => Ran.Add("play"));
            Handler.Register(() => Table.Undo, () => Ran.Add("undo"));
            Handler.RefreshBinds();
            Dispatch = new MidiBindingDispatch(Midi, () => Table, Handler, () => CanRoute);
            Dispatch.Dispatched += () => Dispatched++;
        }
    }

    [Fact]
    public void ABoundPress_RunsTheVerb_CountedUnderItsKeybindId()
    {
        var rig = new Rig();
        rig.Table.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);

        rig.Midi.Press(Pad);

        Assert.Equal(["play"], rig.Ran);
        Assert.Equal(1, rig.Dispatched);
        Assert.Equal([(UsageKind.Keybind, nameof(KeybindConfig.PlayPause))], rig.Usage.Records);
    }

    [Fact]
    public void AVerbWhoseChordWasCleared_StillRunsFromItsPad()
    {
        var rig = new Rig();
        rig.Table.Undo = KeyChord.None;
        rig.Handler.RefreshBinds();
        rig.Table.SetMidiTrigger(nameof(KeybindConfig.Undo), Pad);

        rig.Midi.Press(Pad);

        Assert.Equal(["undo"], rig.Ran);
    }

    /// <summary>The registrar registers some verbs twice under one label
    /// (<c>() => k.SpeedUp</c>, then <c>Key.Add</c>): a pad runs it ONCE.</summary>
    [Fact]
    public void AVerbRegisteredTwice_RunsOnce_TheFirstRegistration()
    {
        var table = new KeybindConfig();
        var ran = new List<string>();
        var handler = new ChartKeybindHandler(() => false, new FakeAppLogger());
        handler.Register(() => table.SpeedUp, () => ran.Add("speed"));
        handler.Register(() => Key.Add, () => ran.Add("numpad"), nameof(KeybindConfig.SpeedUp));
        handler.RefreshBinds();

        Assert.True(handler.HandleVerb(nameof(KeybindConfig.SpeedUp)));
        Assert.False(handler.HandleVerb("NoSuchVerb"));
        Assert.Equal(["speed"], ran);
    }

    [Fact]
    public void NothingRuns_OutsideTheEditor_UnderADialog_OrForAnUnboundPress()
    {
        var rig = new Rig();
        rig.Table.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);

        rig.CanRoute = false;
        rig.Midi.Press(Pad);
        rig.CanRoute = true;
        rig.DialogOpen = true;
        rig.Midi.Press(Pad);
        rig.DialogOpen = false;
        rig.Midi.Press(MidiTrigger.Note(9, 37));

        Assert.Empty(rig.Ran);
        Assert.Equal(0, rig.Dispatched);
        Assert.Empty(rig.Usage.Records);
    }

    [Fact]
    public void AReplacedTable_IsTheOneRead()
    {
        var rig = new Rig();
        rig.Table.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);
        var reverted = new KeybindConfig();
        reverted.SetMidiTrigger(nameof(KeybindConfig.Undo), Pad);
        rig.Table = reverted;

        rig.Midi.Press(Pad);

        Assert.Equal(["undo"], rig.Ran);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var rig = new Rig();
        rig.Table.SetMidiTrigger(nameof(KeybindConfig.PlayPause), Pad);

        rig.Dispatch.Dispose();
        rig.Midi.Press(Pad);

        Assert.Empty(rig.Ran);
        Assert.Equal(0, rig.Midi.PressedSubscribers);
    }

    private static readonly Regex PlainRegistration = new(
        @"Register\(\s*\(\)\s*=>\s*[A-Za-z_.]*\bKeybinds\.(?<verb>[A-Za-z0-9]+)\s*,", RegexOptions.CultureInvariant);

    /// <summary>
    ///     A pad runs the action registered under the verb's NAME, which only a
    ///     plain <c>Register(() => …Keybinds.Verb, …)</c> labels that way. So every
    ///     verb a row lets the user bind must be registered like that, and the rows
    ///     that may not take a pad (<see cref="KeybindConfig.MidiUnbindableVerbs" />)
    ///     must be exactly the ones registered some other way (a digit family).
    /// </summary>
    [Fact]
    public void EveryMidiBindableVerb_IsRegisteredUnderItsOwnName_AndOnlyTheDigitFamiliesAreNot()
    {
        var uiRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        var plain = Directory.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => PlainRegistration.Matches(File.ReadAllText(f)).Select(m => m.Groups["verb"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var verbs = typeof(KeybindConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(KeyChord) && p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .ToList();
        Assert.True(plain.Count > 100, $"only {plain.Count} plain registrations found — the scan went blind");

        var notPlain = verbs.Where(v => !plain.Contains(v)).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(KeybindConfig.MidiUnbindableVerbs.Order(StringComparer.Ordinal), notPlain);
        Assert.All(verbs.Where(v => !notPlain.Contains(v)), v => Assert.True(KeybindConfig.IsMidiBindable(v), v));
    }

    /// <summary>A dispatch nobody resolves listens to nothing: the app must resolve
    /// it at startup and redraw the chart after a pad ran a verb, as a key does.</summary>
    [Fact]
    public void TheAppStartsTheDispatch_AndRedrawsAfterIt()
    {
        var app = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml.cs"));

        Assert.Contains("GetRequiredService<MidiBindingDispatch>()", app, StringComparison.Ordinal);
        Assert.Contains("midiBindings.Dispatched += window.RequestChartFrame;", app, StringComparison.Ordinal);
    }
}
