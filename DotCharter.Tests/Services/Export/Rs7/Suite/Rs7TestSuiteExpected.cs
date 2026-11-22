using System.Text;

namespace DotCharter.Tests.Services.Export.Rs7.Suite;

/// <summary>One line of an expected-results notes table.</summary>
internal sealed record ExpectedNote(int Beat, int String, int Fret, string? Chord, Tech Tech, int Level);

/// <summary>Writes the <c>.expected.md</c> of a chart and the suite's README.md (#307).</summary>
internal static class Rs7TestSuiteExpected
{
    /// <summary>The sounds of a part in the game slots after the mod loads it. Every chord stays one chord (#309): a bass chord keeps
    /// its low B / high C as columns 4/5, an extra-only guitar chord of two or more notes keeps its extra columns in the block.</summary>
    public static List<ExpectedNote> Notes(Part p)
    {
        var rows = new List<ExpectedNote>();
        foreach (var e in p.Events.OrderBy(e => e.Beat))
        {
            foreach (var c in e.Cols.OrderBy(c => Array.IndexOf(p.Inst.Order, c.String)))
                rows.Add(new ExpectedNote(e.Beat, c.String, c.Fret, e.Chord, c.Tech, e.Level));
        }

        return rows;
    }

    public static string TechText(Tech t, int fret)
    {
        if (t == Tech.None) return "";
        var parts = new List<string>();
        foreach (var flag in Enum.GetValues<Tech>().Where(f => f != Tech.None && t.HasFlag(f)))
        {
            parts.Add(flag switch
            {
                Tech.Slide => $"slide to fret {fret + Rs7SuiteSpec.SlideFrets}",
                Tech.UnpitchedSlide => $"unpitched slide to fret {fret + Rs7SuiteSpec.SlideFrets}",
                Tech.Bend => "bend +1 tone then release to 0",
                Tech.Sustain or Tech.Tremolo or Tech.Vibrato => $"{flag.ToString().ToLowerInvariant()} ({Rs7TestSuiteBuilder.Duration(flag)} beats)",
                _ => flag.ToString().ToLowerInvariant()
            });
        }

        return string.Join("; ", parts);
    }

    public static string Markdown(ChartSpec spec, BuiltChart built)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {spec.Name}").AppendLine();
        sb.AppendLine(spec.Tests).AppendLine();
        sb.AppendLine($"- Package: `{spec.Name}{(spec.Parts.Any(p => p.Inst.Extended) ? "_rsxr" : "")}_p.psarc` (plain), DLC key `{DlcKey(spec)}`");
        sb.AppendLine($"- Length: {Rs7SuiteSpec.Fmt(built.SongBeats * Rs7SuiteSpec.BeatMs / 1000)} s ({built.SongBeats} beats at {Rs7SuiteSpec.Bpm:0} BPM, one beat = {Rs7SuiteSpec.Fmt(Rs7SuiteSpec.BeatMs / 1000)} s; note times are beat x 0.5 s)");
        sb.AppendLine($"- Arrangements: {spec.Parts.Length}");
        sb.AppendLine($"- Expected rs7string log: {LogLine(spec)}");
        for (var i = 0; i < spec.Parts.Length; i++) WritePart(sb, spec, i, built);
        return sb.ToString();
    }

    public static string DlcKey(ChartSpec spec) => "rs7t" + spec.Name.Replace("_", "", StringComparison.Ordinal);

    public static string LogLine(ChartSpec spec)
    {
        var ext = spec.Parts.Where(p => p.Inst.Extended).Select(p => $"{p.Inst.Strings} strings ({(p.Inst.Bass ? "bass" : "guitar")})").ToList();
        return ext.Count == 0 ? "none (no extended arrangement; no `extblock` line)" : $"`extblock: SNG ... read` x{ext.Count}: {string.Join(", ", ext)}";
    }

    private static void WritePart(StringBuilder sb, ChartSpec spec, int index, BuiltChart built)
    {
        var p = spec.Parts[index];
        var inst = p.Inst;
        sb.AppendLine().AppendLine($"## Arrangement {index + 1}: {Rs7TestSuiteBuilder.ArrangementName(spec, index)}").AppendLine();
        sb.AppendLine($"- Instrument: {(inst.Bass ? "bass" : "guitar")}, **{inst.Strings} strings**, role {p.Role}");
        sb.AppendLine($"- Expected in-game tuning name: **{Rs7TuningNames.Expected(p)}**");
        sb.AppendLine($"- Difficulty levels: {p.Levels} (level 0 is the sparsest; each chart level holds every note whose `lvl` is at most that level)");
        sb.AppendLine().AppendLine("Tuning (game slot order after loading; offsets against the slot open string):").AppendLine();
        sb.AppendLine("| game slot | native/chart str | open (MIDI) | offset | tuned MIDI | note |").AppendLine("|---|---|---|---|---|---|");
        foreach (var s in Enumerable.Range(0, inst.Strings))
        {
            var midi = Rs7SuiteSpec.Midi(inst, p.Tuning, s, 0);
            sb.AppendLine($"| {s}{(inst.Extras.Contains(s) ? " (extra)" : "")} | {Rs7SuiteSpec.ToDomain(inst, s)} | {Rs7SuiteSpec.NoteName(Rs7SuiteSpec.Open(inst, s))} ({Rs7SuiteSpec.Open(inst, s)}) | {p.Tuning[s]:+0;-0;0} | {midi} | {Rs7SuiteSpec.NoteName(midi)} |");
        }

        Sections(sb, p, built);
        var notes = Notes(p);
        sb.AppendLine().AppendLine($"Notes ({notes.Count} sounds; the table is the top difficulty level):").AppendLine();
        sb.AppendLine("| time s | native/chart str | game slot | fret | MIDI (note) | chord | techniques | lvl |").AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var n in notes)
        {
            var midi = Rs7SuiteSpec.Midi(inst, p.Tuning, n.String, n.Fret);
            var chord = n.Chord ?? "";
            sb.AppendLine($"| {Rs7SuiteSpec.Fmt(Rs7SuiteSpec.Seconds(n.Beat))} | {Rs7SuiteSpec.ToDomain(inst, n.String)} | {n.String} | {n.Fret} | {midi} ({Rs7SuiteSpec.NoteName(midi)}) | {chord} | {TechText(n.Tech, n.Fret)} | {n.Level} |");
        }
    }

    private static void Sections(StringBuilder sb, Part p, BuiltChart built)
    {
        var secs = (p.Sections ?? [new Sec("riff", Rs7SuiteSpec.LeadInBeats)]).Append(new Sec("noguitar", built.EndBeat)).ToArray();
        sb.AppendLine().AppendLine("Sections:").AppendLine().AppendLine("| # | name | start s | extra-string notes |").AppendLine("|---|---|---|---|");
        for (var i = 0; i < secs.Length; i++)
        {
            var end = i + 1 < secs.Length ? secs[i + 1].Beat : int.MaxValue;
            var uses = p.Events.Where(e => e.Beat >= secs[i].Beat && e.Beat < end).SelectMany(e => e.Cols).Any(c => p.Inst.Extras.Contains(c.String));
            sb.AppendLine($"| {i + 1} | {secs[i].Name} | {Rs7SuiteSpec.Fmt(Rs7SuiteSpec.Seconds(secs[i].Beat))} | {(uses ? "yes" : "no")} |");
        }
    }

    public static string Readme(IReadOnlyList<ChartSpec> specs, IReadOnlyDictionary<string, string> refused)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# rs7string in-game test suite").AppendLine();
        sb.AppendLine("Generated by DotCharter (`Rs7TestSuiteTests.Write_the_suite`, env `DOTCHARTER_RS7_SUITE_OUT`). Each chart is a plain PSARC");
        sb.AppendLine("(`<name>_rsxr_p.psarc` for extended content, `<name>_p.psarc` for stock controls) with an `<name>.expected.md` listing the arrangements, tunings, expected tuning names, sections and every note.");
        sb.AppendLine("A control chart has no extended arrangement: rs7string must log no `extblock` line for it.").AppendLine();
        sb.AppendLine($"{specs.Count} charts. Packages use positional strings; the expected tables also name the game slots after loading. Every multi-string chord stays one chord;");
        sb.AppendLine("no note is split off a chord.").AppendLine();
        sb.AppendLine(refused.Count == 0 ? "Refused by DotCharter: nothing; every chart in the issue's coverage was exported." : $"Refused by DotCharter: {refused.Count} chart(s), see the last column.").AppendLine();
        sb.AppendLine("| chart | tests | arrangements | expected rs7string log | DotCharter |").AppendLine("|---|---|---|---|---|");
        foreach (var s in specs)
        {
            var arr = string.Join(", ", s.Parts.Select(p => $"{p.Inst.Key}{(p.Role == p.Inst.Type ? "" : $" {p.Role}")}"));
            refused.TryGetValue(s.Name, out var why);
            sb.AppendLine($"| `{s.Name}` | {s.Tests} | {arr} | {LogLine(s)} | {(why is null ? "built" : $"refused by DotCharter: {why}")} |");
        }

        return sb.ToString();
    }
}
