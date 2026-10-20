namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>The words the fake chat pane shows: a short chat about a verse riff, its German and Japanese twins, and a long session.</summary>
internal static class FakeAiChatData
{
    public const string Sonnet = "Claude Sonnet 5.5";

    public const string ProviderText = "Claude Code on this computer, using your Claude sign-in";

    private const string ReviewLine = "The proposals go to the review card; nothing changes until you accept them there.";

    /// <summary>Each task's empty pane and hint, in <see cref="Jobs" /> order.</summary>
    public static readonly (string Empty, string Hint)[] JobText =
    [
        ("Say how the chords should be fingered, in your own words. The hand positions and the notes stay as they are. " +
         ReviewLine, "e.g. Finger the power chords 1·3·4"),
        ("Say where the hand should sit and how to finger the chords. The notes stay as they are. " + ReviewLine,
            "e.g. Keep the hand in 5th position in the verse"),
        ("Say where the sections go and how to split the phrases: names, bars, lengths. The notes stay as they are. " +
         ReviewLine, "e.g. Intro, verse, chorus, solo; phrases of 4 bars"),
        ("Ask a question about this chart: why a hand position or a fingering, what the quality report flags, what is " +
         "hard to play. You get an answer in words; nothing is proposed or changed.",
            "e.g. Why is the hand in 5th position in bar 19?"),
        ("Describe the sound you want for the placed tone changes: amp, gain, effects, brighter or darker. The AI proposes " +
         "Rocksmith tones from the song's audio and your words; they go to the review card, and nothing changes until " +
         "you accept them there.", "e.g. Lead tone: high-gain amp, a bit darker")
    ];

    public const string NoAnswer = "The AI gave no answer. Ask again, or pick a smaller scope.";

    /// <summary>A reply that lists the findings a pass left in its range.</summary>
    public const string FindingsLeft = "No section changed.\n\n2 findings are still left in this range:\n" +
        "• I20 · M21.1 · 00:41.20 — Anchor is placed inside a hand shape — this can break the hand shape rendering.\n" +
        "• E06 · M32.1 · 01:02.80 — Phrase 'chorus' plays notes in this difficulty but defines no anchor.";

    public static IReadOnlyList<FakeAiChatOption> ClaudeModels() =>
    [
        new("Claude Haiku 5.5", "claude-haiku-5-5"),
        new(Sonnet, "claude-sonnet-5-5"),
        new("Claude Opus 5.5", "claude-opus-5-5")
    ];

    public static IReadOnlyList<FakeAiChatOption> Scopes() =>
    [
        new("Selection", "12 notes · bars 21–22"),
        new("This section", "Verse 2 · bars 17–32"),
        new("Whole arrangement", "212 bars")
    ];

    public static IReadOnlyList<FakeAiChatOption> Jobs() =>
    [
        new("Fingering (hand positions stay)",
            "Recommended: a model fingers chords far better than the algorithm (96.1 % vs 67.1 %), while the " +
            "algorithm's hand positions are as good as any model's (95.7 % vs 92.4–96.6 %), so they stay."),
        new("Hand positions & fingering",
            "Hand positions from the model too: measured on guitar, no clear gain over the algorithm (models 92.4–96.6 % vs 95.7 %)."),
        new("Sections & phrases", ""),
        new("Ask / explain", ""),
        new("Tones", "")
    ];

    public static IReadOnlyList<FakeAiChatOption> Arrangements() => [new("Lead"), new("Rhythm"), new("Bass")];

    public static IReadOnlyList<FakeAiChatTurn> Conversation() =>
    [
        new(true, "You", "Why is the hand in 5th position for the verse riff? The original is played around the 3rd fret.", "14:02"),
        new(false, Sonnet,
            "The verse riff (bars 17–32) reaches fret 8 on the G string twice, in bars 19 and 27. From 5th position " +
            "those notes fall under the fourth finger, so the hand never moves. From 3rd position each of them needs " +
            "a shift.\n\nIf you prefer the lower position, I can propose anchors at fret 3 with a short shift to 5 " +
            "for those two bars.",
            "14:02", "2,310 in · 214 out · $0.011"),
        new(true, "You", "Keep the hand at 3 and finger the octaves 1·3.", "14:04"),
        new(false, Sonnet,
            "Proposed 6 anchor changes and 14 chord fingerings in Verse 2. They are marked on the timeline; " +
            "accept or reject them per section in the review card.",
            "14:04", "2,906 in · 388 out · $0.016")
    ];

    public static IReadOnlyList<(string, string, string)> History() =>
    [
        ("Why is the hand in 5th position for the verse riff?", "Today, 14:04", Sonnet),
        ("Mark the sections", "Yesterday, 21:40", "Claude Opus 5.5"),
        ("Finger the chorus barre chords with the index barre", "7 Oct, 19:12", Sonnet),
        ("What's wrong in bar 34?", "2 Oct, 10:55", "Claude Haiku 5.5")
    ];

    public const string GermanTitle =
        "Warum liegt die Hand in der zweiten Strophe in der fünften Lage statt in der dritten wie im Original?";

    public static IReadOnlyList<FakeAiChatTurn> LongConversation() =>
    [
        new(true, "Du", GermanTitle + " Bitte erkläre es Takt für Takt, mit den Bundgriffen.", "14:02"),
        new(false, "Qwen3-Coder-480B-A35B-Instruct (LM Studio, lokal)",
            "Das Strophenriff (Takte 17–32) erreicht zweimal den achten Bund auf der G-Saite, in Takt 19 und 27. " +
            "Aus der fünften Lage liegen diese Töne unter dem vierten Finger, die Hand muss sich also nie bewegen. " +
            "Aus der dritten Lage bräuchte jeder davon einen Lagenwechsel, und bei 152 BPM ist das in Takt 27 " +
            "kaum sauber spielbar.\n\nSaitenübergreifende-Oktavgriffe-mit-gedämpfter-Zwischensaite bleiben in beiden " +
            "Lagen gleich.",
            "14:03", "12.310 ein · 1.214 aus · Kosten unbekannt"),
        new(true, "あなた", "コーラスのバレーコードは人差し指のバレーで運指して、ブリッジのフレーズは4小節ずつに分けてください。", "14:05"),
        new(false, "Qwen3-Coder-480B-A35B-Instruct (LM Studio, lokal)",
            "コーラス（33〜48小節）のバレーコード14個に人差し指のバレーを提案しました。ブリッジは4小節ずつ4つのフレーズに分けています。" +
            "レビューカードでセクションごとに承認または却下してください。",
            "14:05", "13,002 in · 402 out")
    ];

    public static IReadOnlyList<(string, string, string)> LongHistory() =>
    [
        (GermanTitle, "Heute, 14:05", "Qwen3-Coder-480B-A35B-Instruct (LM Studio, lokal)"),
        ("コーラスのバレーコードは人差し指のバレーで運指して、ブリッジのフレーズを分けてください", "昨日 21:40", "Claude Opus 5.5"),
        ("Abschnitte markieren: Intro, Strophe, Refrain, Bridge, Solo, Outro", "7. Okt., 19:12", Sonnet)
    ];

    private static readonly string[] Questions =
    [
        "Why 1·3 in bar {0}?",
        "Keep the hand in 5th position from bar {0} to the end of the solo.",
        "What's wrong in bar {0}? The export report flags a fingering there.",
        "Split the solo into phrases of two bars starting at bar {0}.",
        "Finger the power chords from bar {0} with 1·3·4."
    ];

    /// <summary>A long session: <paramref name="exchanges" /> questions with their answers.</summary>
    public static IReadOnlyList<FakeAiChatTurn> LongSession(int exchanges)
    {
        var turns = new List<FakeAiChatTurn>(exchanges * 2);
        for (var i = 0; i < exchanges; i++)
        {
            var bar = 9 + (i * 7);
            var time = $"{13 + (i / 20)}:{(i * 3) % 60:00}";
            turns.Add(new FakeAiChatTurn(true, "You", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                Questions[i % Questions.Length], bar), time));
            turns.Add(new FakeAiChatTurn(false, Sonnet,
                $"Bar {bar} holds a stretch from fret {3 + (i % 5)} to fret {7 + (i % 5)}. A 1·3 fingering keeps the " +
                "second finger free for the slide that follows, which is why it was suggested.",
                time, $"{2400 + (i * 310):N0} in · {180 + (i * 11)} out · ${0.01 + (i * 0.002):0.000}"));
        }

        return turns;
    }

    public static IReadOnlyList<(string, string, string)> ManyChats(int count)
    {
        var chats = new List<(string, string, string)>(count);
        for (var i = 0; i < count; i++)
        {
            var title = string.Format(System.Globalization.CultureInfo.InvariantCulture, Questions[i % Questions.Length], 4 + (i * 5));
            chats.Add((title, i == 0 ? "Today, 14:30" : $"{1 + (i % 28)} Sep, {9 + (i % 12)}:{(i * 7) % 60:00}",
                (i % 3) switch { 0 => Sonnet, 1 => "Claude Opus 5.5", _ => "Claude Haiku 5.5" }));
        }

        return chats;
    }
}
