using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

/// <summary>
///     Resources.resx, Resources.de-DE.resx and Resources.ja-JP.resx keep #532's reviewed action labels.
///     Exact copy is the regression here: the previous labels promised a different action.
/// </summary>
public sealed class ChartEditorWordingTests
{
    public static TheoryData<string, string, string> Labels => new()
    {
        { "Resources.resx", "Inspector_Task_FixNext", "Show first problem" },
        { "Resources.de-DE.resx", "Inspector_Task_FixNext", "Erstes Problem anzeigen" },
        { "Resources.ja-JP.resx", "Inspector_Task_FixNext", "最初の問題を表示" },
        { "Resources.resx", "Toolbar_ToggleClaps_Hint", "Claps ({0}). Clap on each charted note or chord." },
        { "Resources.de-DE.resx", "Toolbar_ToggleClaps_Hint", "Claps ({0}). Bei jeder Note und jedem Akkord im Chart klatschen." },
        { "Resources.ja-JP.resx", "Toolbar_ToggleClaps_Hint", "クラップ（{0}）。譜面の各ノートやコードに合わせて鳴らします。" },
        { "Resources.resx", "Dialog_MidiImport_Subtitle", "Notes go into a new draft arrangement. Replacing the beat grid also moves existing notes." },
        { "Resources.de-DE.resx", "Dialog_MidiImport_Subtitle", "Noten werden in ein neues Entwurfsarrangement eingefügt. Beim Ersetzen des Beat-Rasters verschieben sich auch vorhandene Noten." },
        { "Resources.ja-JP.resx", "Dialog_MidiImport_Subtitle", "ノートは新しい下書きアレンジメントに追加されます。ビートグリッドを置き換えると、既存のノートのタイミングも変わります。" },
        { "Resources.resx", "Menu_ChartDiff", "Compare with another project…" },
        { "Resources.de-DE.resx", "Menu_ChartDiff", "Mit einem anderen Projekt vergleichen…" },
        { "Resources.ja-JP.resx", "Menu_ChartDiff", "別のプロジェクトと比較…" },
        { "Resources.resx", "Toolbar_Tracks_Solo", "Show only this arrangement" },
        { "Resources.de-DE.resx", "Toolbar_Tracks_Solo", "Nur dieses Arrangement anzeigen" },
        { "Resources.ja-JP.resx", "Toolbar_Tracks_Solo", "このアレンジメントだけを表示" },
        { "Resources.resx", "Toolbar_Tracks_Mute", "Mute chart sound" },
        { "Resources.de-DE.resx", "Toolbar_Tracks_Mute", "Chart-Klang stummschalten" },
        { "Resources.ja-JP.resx", "Toolbar_Tracks_Mute", "チャートサウンドをミュート" },
        { "Resources.resx", "Inspector_IgnoreTip", "Do not score this note in Rocksmith." },
        { "Resources.de-DE.resx", "Inspector_IgnoreTip", "Diese Note in Rocksmith nicht werten." },
        { "Resources.ja-JP.resx", "Inspector_IgnoreTip", "Rocksmithでこのノートを採点対象から外します。" },
        { "Resources.resx", "Dialog_Event_Subtitle", "Edit sections, phrases and Rocksmith events." },
        { "Resources.de-DE.resx", "Dialog_Event_Subtitle", "Abschnitte, Phrasen und Rocksmith-Ereignisse bearbeiten." },
        { "Resources.ja-JP.resx", "Dialog_Event_Subtitle", "セクション、フレーズ、Rocksmithイベントを編集します。" },
        { "Resources.resx", "Inspector_AudioLint_NoArrangement", "Select an arrangement, then run the audio check." },
        { "Resources.de-DE.resx", "Inspector_AudioLint_NoArrangement", "Ein Arrangement auswählen, dann die Audio-Prüfung starten." },
        { "Resources.ja-JP.resx", "Inspector_AudioLint_NoArrangement", "アレンジメントを選択してから、オーディオチェックを実行してください。" },
        { "Resources.resx", "Inspector_Phrases_Propagate", "Copy to repeats" },
        { "Resources.de-DE.resx", "Inspector_Phrases_Propagate", "In Wiederholungen kopieren" },
        { "Resources.ja-JP.resx", "Inspector_Phrases_Propagate", "繰り返しにコピー" },
        { "Resources.resx", "Inspector_Phrases_PropagateConfirm", "Confirm copy" },
        { "Resources.de-DE.resx", "Inspector_Phrases_PropagateConfirm", "Kopieren bestätigen" },
        { "Resources.ja-JP.resx", "Inspector_Phrases_PropagateConfirm", "コピーを確定" },
        { "Resources.resx", "Inspector_MuteMidi", "Mute chart sound" },
        { "Resources.de-DE.resx", "Inspector_MuteMidi", "Chart-Klang stummschalten" },
        { "Resources.ja-JP.resx", "Inspector_MuteMidi", "チャートサウンドをミュート" },
        { "Resources.resx", "Inspector_SoloMidi", "Solo chart sound" },
        { "Resources.de-DE.resx", "Inspector_SoloMidi", "Nur Chart-Klang hören" },
        { "Resources.ja-JP.resx", "Inspector_SoloMidi", "チャートサウンドをソロにする" },
        { "Resources.resx", "Keybind_OpenTracksPopover", "Open the Arrangements panel" },
        { "Resources.de-DE.resx", "Keybind_OpenTracksPopover", "Arrangements-Panel öffnen" },
        { "Resources.ja-JP.resx", "Keybind_OpenTracksPopover", "アレンジメントパネルを開く" },
        { "Resources.resx", "Keybind_OpenToolsPopover", "Open Practice tools" },
        { "Resources.de-DE.resx", "Keybind_OpenToolsPopover", "Übungswerkzeuge öffnen" },
        { "Resources.ja-JP.resx", "Keybind_OpenToolsPopover", "練習ツールを開く" }
    };

    [Theory]
    [MemberData(nameof(Labels))]
    public void Labels_DescribeTheActionRatherThanPromisingAnother(string file, string key, string expected)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file);
        var value = XDocument.Load(path).Root!.Elements("data")
            .Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;

        Assert.Equal(expected, value);
    }
}
