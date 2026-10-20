using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Tests.Ai.Chart;
using DotCharter.Tests.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Chat;

/// <summary>
///     The AI chat's Tones task (#614) on a FAKE model: 8 bars of 2,000 ms, four placed tone changes —
///     "Clean" at 2,000 ms, "Lead_Dist" at 6,000, "Clean" again at 10,000 and an UNNAMED one at 15,000 —
///     and authored bodies for "Clean" and "Lead_Dist". The windows (one measure of lead, each ending where
///     the next starts) are 0–4,000, 4,000–8,000, 8,000–13,000 and 13,000–16,000, so "Clean"'s LONGEST
///     window is its second one. The stems are a fake isolator; the model's reply goes through the REAL
///     mapper, reader and catalogue gate.
/// </summary>
internal sealed class AiChatToneFixture
{
    public const double SongMs = 16_000;

    public AiChatToneFixture()
    {
        Beats = GenerateFakes.Beats(8, 2000);
        foreach (var beat in Beats) Project.Beats.Add(beat);
        Project.Chart.SongName = "Sakasma";
        Project.Chart.Artist = "The Band";
        Arrangement = new Arrangement { Name = "Lead", ToneBase = "Clean" };
        Level = new ChartLevel();
        Level.Notes.Add(new GuitarNote { StringIndex = 1, Fret = 5, Position = new FractionalPosition(4) });
        Arrangement.Levels.Add(Level);
        CleanIntro = GenerateFakes.At(2000, Beats, "Clean");
        Lead = GenerateFakes.At(6000, Beats, "Lead_Dist");
        CleanOutro = GenerateFakes.At(10000, Beats, "Clean");
        Unnamed = GenerateFakes.At(15000, Beats);
        // Out of time order on purpose: the scope and the planner sort.
        foreach (var change in new[] { Lead, CleanIntro, Unnamed, CleanOutro }) Arrangement.ToneChanges.Add(change);
        Project.Arrangements.Add(Arrangement);
        Project.ToneDefinitions.Add(new ToneDefinition { Key = "Clean", Name = "Clean", Json = Body("Clean") });
        Project.ToneDefinitions.Add(new ToneDefinition { Key = "Lead_Dist", Name = "Lead Dist", Json = Body("Lead_Dist") });

        State = new ProjectState
        {
            ActiveProject = Project, ActiveArrangement = Arrangement, ActiveLevel = Level, TotalAudioLengthMs = SongMs
        };
    }

    public ChartProject Project { get; } = new();

    public Arrangement Arrangement { get; }

    public ChartLevel Level { get; }

    public List<BeatMarker> Beats { get; }

    public ToneChange CleanIntro { get; }

    public ToneChange Lead { get; }

    public ToneChange CleanOutro { get; }

    public ToneChange Unnamed { get; }

    public ProjectState State { get; }

    public HistoryService History { get; } = new(new FakeAppLogger());

    public FakeLocalization Loc { get; } = FakeLocalization.English();

    public GenerateFakes.FakeIsolator Isolator { get; } = new();

    public RigConfig Config { get; } = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };

    /// <summary>The default tone body for <paramref name="key" />, as the Tone designer writes one.</summary>
    public static string Body(string key) =>
        RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared));

    /// <summary>The run, its tones generated over <paramref name="model" /> through the real mapper.</summary>
    public AiChartPassRun Run(FakeChartModel model, IStemIsolator? isolator = null) =>
        new(State, History, null, Loc, new FakeAppLogger(), new FingeringGenerationService(), new Factory(model),
            () => Config, () => null)
        {
            ToneGenerator = mapper => new RsToneGenerator(isolator ?? Isolator, mapper, Loc),
            SongAudio = () => "song.wav"
        };

    /// <summary>The Tones task's request over a scope.</summary>
    public static AiChatInstructionRequest Ask(string instruction, AiChatScope scope, AiChatConversation? chat = null) =>
        new(instruction, scope, chat, Job: AiChartJob.Tones);

    /// <summary>Every placed tone change of the arrangement.</summary>
    public AiChatScope All() =>
        AiChatScope.Tones(AiChatScopeKind.Arrangement, Arrangement, [], 0, Beats)!;

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
