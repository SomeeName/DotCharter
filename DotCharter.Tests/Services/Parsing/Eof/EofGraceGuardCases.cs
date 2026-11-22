using System.Numerics;
using System.Text.Json;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

internal static class EofGraceGuardCases
{
    internal sealed class Input
    {
        public List<EofRawProGuitarNote> Notes { get; } = Pair();
        public bool Finger { get; set; }
        public bool Trill { get; set; }
        public List<EofRawProGuitarNote> Tech { get; } = [];
    }

    public static readonly string[] Names = ["marker absent", "marker case", "finger", "difficulty 255", "empty mask",
        "multi-string ornament", "ghost ornament", "ghost carrier", "legacy", "extended", "zero length", "length cap",
        "open origin", "muted origin", "open slide end", "muted slide end", "no interval", "missing link", "missing notation",
        "missing slide", "wrong direction", "extra technique", "noncanonical length", "different difficulty", "different string",
        "named single", "reserved chord", "reserved Nop chord", "reserved spaced chord", "invalid target ghosts", "wrong target fret",
        "open target", "muted target", "target gap", "target overlap", "no target", "target before ornament", "overflow",
        "ambiguous", "played neighbour", "slide tech", "other tech", "trill"];

    public static List<BeatMarker> Beats() => Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 600 }).ToList();

    public static Input Create(string name)
    {
        var input = new Input();
        var g = input.Notes[0];
        var t = input.Notes[1];
        switch (name)
        {
            case "marker absent": g.Name = ""; break;
            case "marker case": g.Name = "[DotCharter Grace]"; break;
            case "finger": input.Finger = true; break;
            case "difficulty 255": g.Difficulty = t.Difficulty = 255; break;
            case "empty mask": g.NoteBitmask = 0; break;
            case "multi-string ornament": g.NoteBitmask = t.NoteBitmask = 3; g.Frets[1] = 7; t.Frets[1] = 9; break;
            case "ghost ornament": g.GhostBitmask = 1; break;
            case "ghost carrier": g.NoteBitmask = 3; g.GhostBitmask = 2; g.Frets[1] = 7; break;
            case "legacy": g.LegacyMask = 1; break;
            case "extended": g.ExtendedFlags = 1; break;
            case "zero length": g.LengthMs = 0; t.PositionMs = g.PositionMs; break;
            case "length cap": g.LengthMs = 201; t.PositionMs = g.PositionMs + g.LengthMs; break;
            case "open origin": g.Frets[0] = 0; break;
            case "muted origin": g.Frets[0] |= 0x80; break;
            case "open slide end": g.SlideEndFret = t.Frets[0] = 0; break;
            case "muted slide end": g.SlideEndFret |= 0x80; break;
            case "no interval": g.Frets[0] = 7; break;
            case "missing link": g.Flags &= ~EofConstants.PgFlagLinkNext; break;
            case "missing notation": g.Flags &= ~EofConstants.PgFlagRsNotation; break;
            case "missing slide": g.Flags &= ~EofConstants.PgFlagSlideUp; break;
            case "wrong direction": g.Flags ^= EofConstants.PgFlagSlideUp | EofConstants.PgFlagSlideDown; break;
            case "extra technique": g.Flags |= EofConstants.PgFlagVibrato; break;
            case "noncanonical length": g.LengthMs++; t.PositionMs++; break;
            case "different difficulty": t.Difficulty++; break;
            case "different string": t.NoteBitmask = 2; t.Frets[1] = 7; break;
            case "named single": t.Name = "Authored target"; break;
            case "reserved chord": SetChord(t, EofGraceTiming.SynthesizedNoteName); break;
            case "reserved Nop chord": SetChord(t, EofGraceTiming.SynthesizedNoteName + "-nop"); break;
            case "reserved spaced chord": SetChord(t, " " + EofGraceTiming.SynthesizedNoteName + " "); break;
            case "invalid target ghosts": t.GhostBitmask = 2; break;
            case "wrong target fret": t.Frets[0] = 8; break;
            case "open target": t.Frets[0] = 0; break;
            case "muted target": t.Frets[0] |= 0x80; break;
            case "target gap": t.PositionMs++; break;
            case "target overlap": t.PositionMs--; break;
            case "no target": input.Notes.RemoveAt(1); break;
            case "target before ornament": input.Notes.Reverse(); break;
            case "overflow": t.LengthMs = uint.MaxValue; break;
            case "ambiguous": input.Notes.Insert(0, Pair()[0]); break;
            case "played neighbour":
                var neighbour = Pair()[1];
                neighbour.PositionMs = 601;
                neighbour.Frets[0] = 11;
                input.Notes.Insert(1, neighbour);
                break;
            case "slide tech": AddTech(input, EofConstants.PgFlagSlideUp | EofConstants.PgFlagRsNotation); break;
            case "other tech": AddTech(input, EofConstants.PgFlagVibrato); break;
            case "trill": input.Trill = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        return input;
    }

    private static void SetChord(EofRawProGuitarNote target, string name)
    {
        target.Name = name;
        target.NoteBitmask = 3;
        target.Frets[1] = 9;
    }

    private static void AddTech(Input input, uint flags)
    {
        var tech = Pair()[0];
        tech.Name = "";
        tech.PositionMs = 620;
        tech.LengthMs = 1;
        tech.Flags = flags;
        tech.SlideEndFret = 9;
        input.Tech.Add(tech);
    }

    private static List<EofRawProGuitarNote> Pair()
    {
        var ornament = new EofRawProGuitarNote
        {
            Name = EofGraceTiming.SynthesizedNoteName, NoteBitmask = 1, PositionMs = 600, LengthMs = 75,
            Flags = EofConstants.PgFlagLinkNext | EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp,
            SlideEndFret = 7
        };
        ornament.Frets[0] = 5;
        var target = new EofRawProGuitarNote { NoteBitmask = 1, PositionMs = 675, LengthMs = 1125 };
        target.Frets[0] = 7;
        return [ornament, target];
    }

    public static byte[] Track(Input input)
    {
        var writer = new EofPackWriter();
        writer.WriteU8(24);
        writer.WriteU8(8);
        for (var i = 0; i < 8; i++) writer.WriteU8(0);
        writer.WriteU32((uint)input.Notes.Count);
        foreach (var note in input.Notes) EofProGuitarNoteWriter.Write(writer, note);
        writer.WriteU16(input.Trill ? (ushort)1 : (ushort)0);
        if (input.Trill)
        {
            writer.WriteU16(EofConstants.SectionTrill);
            writer.WriteU32(1);
            writer.WriteString("");
            writer.WriteU8(0);
            writer.WriteU32(600);
            writer.WriteU32(610);
            writer.WriteU32(0);
        }
        writer.WriteU32((uint)((input.Finger ? 1 : 0) + (input.Tech.Count > 0 ? 1 : 0)));
        if (input.Finger)
        {
            writer.WriteU32(4 + (uint)input.Notes.Sum(n => BitOperations.PopCount(n.NoteBitmask)));
            writer.WriteU32(EofConstants.PgCustomFingerArray);
            for (var i = 0; i < input.Notes.Count; i++) writer.WriteU8(i == 0 ? (byte)1 : (byte)0);
        }
        if (input.Tech.Count > 0)
        {
            var payload = new EofPackWriter();
            payload.WriteU32((uint)input.Tech.Count);
            foreach (var tech in input.Tech) EofProGuitarNoteWriter.Write(payload, tech);
            writer.WriteU32((uint)(4 + payload.Length));
            writer.WriteU32(EofConstants.PgCustomTechNotes);
            writer.WriteBytes(payload.ToArray());
        }
        return writer.ToArray();
    }

    public static Arrangement Import(byte[] bytes, IList<BeatMarker> beats) =>
        EofProGuitarTrackParser.Parse(new EofPackReader(bytes), new EofTrackInfo { Id = 9, Name = "Lead" }, beats);

    internal static void CompareMain(Func<byte[], IList<BeatMarker>, Arrangement> parseMain, string output)
    {
        var rows = Names.Select(name =>
        {
            var bytes = Track(Create(name));
            var main = parseMain(bytes, Beats());
            var branch = Import(bytes, Beats());
            return new { name, mainFolds = HasGrace(main), branchFolds = HasGrace(branch) };
        });
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool HasGrace(Arrangement arrangement) => arrangement.Levels.Any(l =>
        l.Notes.Any(n => n.GraceFret != null) || l.Chords.Any(c => c.ChordNotes.Any(n => n.GraceFret != null)));
}
