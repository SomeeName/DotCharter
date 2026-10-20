using System.Security.Cryptography;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Infrastructure.Psarc.Write;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #885: the SNG and PSARC writers pinned by their BYTES, without the package reader
///     (which leaves the public tree). Every input is fixed: the SNG model, the IV and the
///     key (<see cref="TestRocksmithKeys" />' synthetic ones, passed explicitly or scoped),
///     so each output is a pure function and its SHA-256 is pinned. The round trips through
///     the reader are <c>RocksmithSngWriterTests</c> (reader side). A failure here means a
///     writer changed what it emits: investigate, never re-pin.
/// </summary>
public sealed partial class SngWriterBytePinTests
{
    internal static readonly byte[] FixedIv =
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    /// <summary>Every SNG section populated, with fields a lossy writer would drop.</summary>
    internal static RocksmithSng BuildSyntheticSng()
    {
        var sng = new RocksmithSng();

        sng.Beats.Add(new RsBeat(0.0f, 0, 0, 3, 0));
        sng.Beats.Add(new RsBeat(0.5f, 0, 1, 0, 0));
        sng.Beats.Add(new RsBeat(1.0f, 1, 0, 1, 1));

        sng.Phrases.Add(new RsPhrase(0, 1, "COUNT"));
        sng.Phrases.Add(new RsPhrase(2, 2, "riff", Solo: 1, Disparity: 0, Ignore: 0));
        sng.Phrases.Add(new RsPhrase(0, 1, "END"));

        sng.Chords.Add(new RsChordTemplate(
            0,
            [-1, 3, 5, 5, -1, -1],
            [-1, 1, 3, 4, -1, -1],
            "C5")
        {
            Notes = [0, 48, 55, 60, 0, 0]
        });
        sng.Chords.Add(new RsChordTemplate(1, [0, 2, 2, -1, -1, -1], [-1, 1, 2, -1, -1, -1], "E-arp"));

        var chordNotes = new RsChordNotes();
        chordNotes.Mask[1] = RsNoteMask.HammerOn;
        chordNotes.SlideTo[1] = 7;
        chordNotes.SlideUnpitchTo[2] = 9;
        chordNotes.Vibrato[3] = 80;
        chordNotes.BendData[1].Add(new RsBendValue(1.25f, 1.0f));
        chordNotes.BendData[1].Add(new RsBendValue(1.5f, 0.5f));
        sng.ChordNotes.Add(chordNotes);

        sng.Vocals.Add(new RsVocal(5.0f, 60, 0.5f, "hel-"));
        sng.Vocals.Add(new RsVocal(5.5f, 62, 0.5f, "lo+"));
        sng.SymbolsHeaders.Add(new RsSymbolsHeader(0));
        sng.SymbolsTextures.Add(new RsSymbolsTexture("assets/ui/lyrics/lyrics.dds", 27, 1024, 512));
        sng.SymbolDefinitions.Add(new RsSymbolDefinition(
            "h",
            [0f, 1f, 2f, 3f],
            [0.5f, 1.5f, 2.5f, 3.5f]));

        sng.PhraseIterations.Add(new RsPhraseIteration(0, 0f, 1f, [0, 0, 0]));
        sng.PhraseIterations.Add(new RsPhraseIteration(1, 1f, 9f, [0, 1, 2]));
        sng.PhraseIterations.Add(new RsPhraseIteration(2, 9f, 10f, [0, 0, 0]));

        sng.PhraseExtraInfos.Add(new RsPhraseExtraInfo(1, 2, 0, 1, 3));
        sng.NewLinkedDifficulties.Add(new RsNewLinkedDifficulty(2, [0, 1]));
        sng.Actions.Add(new RsAction(3.0f, "some_action"));

        sng.Events.Add(new RsEvent(1.0f, "E3"));
        sng.Tones.Add(new RsTone(0f, 0));
        sng.Tones.Add(new RsTone(4.5f, 1));
        sng.Dnas.Add(new RsDna(0f, 0));
        sng.Dnas.Add(new RsDna(1.0f, 2));

        sng.Sections.Add(new RsSection("intro", 1, 0f, 1f, 0, 1)
        {
            StringMask = CreateStringMask()
        });
        sng.Sections.Add(new RsSection("riff", 1, 1f, 9f, 1, 2));

        for (var difficulty = 0; difficulty < 2; difficulty++)
        {
            var level = new RsLevel { Difficulty = difficulty };
            level.Anchors.Add(new RsAnchor(1.0f, 3, 4, 9.0f, 1.1f, 8.5f, 1));
            level.AnchorExtensions.Add(new RsAnchorExtension(2.0f, 5));
            level.HandShapes.Add(new RsFingerPrint(0, 1.0f, 2.0f, 1.1f, 1.9f));
            level.Arpeggios.Add(new RsFingerPrint(1, 3.0f, 4.0f, -1f, -1f));

            level.Notes.Add(new RsNote
            {
                Mask = RsNoteMask.Single | RsNoteMask.Sustain | RsNoteMask.Bend,
                Flags = 0,
                Hash = 0xDEADBEEF,
                Time = 1.1f,
                StringIndex = 2,
                Fret = 5,
                AnchorFret = 3,
                AnchorWidth = 4,
                ChordId = -1,
                ChordNotesId = -1,
                PhraseId = 1,
                PhraseIterationId = 1,
                FingerPrintId0 = 0,
                FingerPrintId1 = -1,
                NextIterNote = 1,
                PrevIterNote = 0,
                ParentPrevNote = -1,
                SlideTo = -1,
                SlideUnpitchTo = -1,
                LeftHand = 2,
                Tap = 0,
                PickDirection = 0,
                Slap = -1,
                Pluck = -1,
                Vibrato = 0,
                Sustain = 0.75f,
                MaxBend = 1.0f,
                BendData = [new RsBendValue(1.4f, 1.0f)]
            });
            level.Notes.Add(new RsNote
            {
                Mask = RsNoteMask.Chord | RsNoteMask.ChordNotes,
                Hash = 0x12345678,
                Time = 2.0f,
                StringIndex = -1,
                Fret = -1,
                AnchorFret = 3,
                AnchorWidth = 4,
                ChordId = 0,
                ChordNotesId = 0,
                PhraseId = 1,
                PhraseIterationId = 1,
                NextIterNote = 2,
                PrevIterNote = 1
            });

            level.AverageNotesPerIteration = [0f, 2f, 0f];
            level.NotesInPhraseIterationsExclIgnored = [0, 2, 0];
            level.NotesInPhraseIterationsAll = [0, 2, 0];
            sng.Levels.Add(level);
        }

        sng.MetaData = new RsMetaData
        {
            MaxScore = 100000.0,
            MaxNotesAndChords = 4.0,
            MaxNotesAndChordsReal = 4.0,
            PointsPerNote = 25000.0,
            FirstBeatLength = 0.5f,
            StartTime = 0.0f,
            CapoFretId = -1,
            LastConversionDateTime = "07-4-26 12:00",
            Part = 1,
            SongLength = 10.0f,
            Tuning = [0, 0, 0, 0, 0, 0],
            FirstNoteTime = 1.1f,
            MaxDifficulty = 1
        };

        return sng;
    }

    internal static sbyte[] CreateStringMask()
    {
        var mask = new sbyte[36];
        for (var i = 0; i < mask.Length; i++) mask[i] = (sbyte)(i % 3);
        return mask;
    }

    // Captured from the writer as split in #885; the whole-package pins in
    // RocksmithPlatformPackageTests (captured before #782) cover the same packer and TOC
    // encryptor and pass unchanged.
    private const string PinnedBody = "cc8cf886b6af915dbfcaf97c1b29d2a62f4da1b9452a4fb411be057fcfc3387a";
    private const string PinnedBodyWithoutVocals = "ea8aa98c92970df6e17584620aaf45b55f66aecb0478acf454d7e04d70df922d";
    private const string PinnedPackedPc = "925e1b73f2db1c02f326faac19b00b6d394d009e88a786e907fe616fa27b2218";
    private const string PinnedPackedMac = "7fe3ce40869cab143fbca3f2aa436a2e056e2a4ed0ed6be978c2fd84ce134f7b";
    private const string PinnedPlainPc = "594113e050766e5df2b07b0147d6c617c05209ce021561eb427b37c8bacf114c";
    private const string PinnedToc = "dba957891cd0887fe5678a7f0fbf8218f3c07d25105d28c2a698d0867ba70a73";
    private const string PinnedArchivePlain = "87cdbd06152b162bd47061dc2055e2ba2e5adf00f2bda18ffc68a708d19e074a";
    private const string PinnedArchiveEncrypted = "eac29dd8256698a4b0c2672cc5c1df710b0e03176f82e5557b6767db3b15062d";

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void AssertPinned(string expected, byte[] bytes, string what)
    {
        var actual = Sha256(bytes);
        Assert.True(expected == actual, $"{what}: SHA-256 {actual} != pinned {expected}");
    }

    private static RocksmithSng WithoutVocals()
    {
        var sng = BuildSyntheticSng();
        sng.Vocals.Clear();
        sng.SymbolsHeaders.Clear();
        sng.SymbolsTextures.Clear();
        sng.SymbolDefinitions.Clear();
        return sng;
    }

    private static byte[] WriteArchive(bool encryptToc)
    {
        var big = new byte[100_000];
        for (var i = 0; i < big.Length; i++) big[i] = (byte)((i % 251) ^ (i >> 9));
        using var output = new MemoryStream();
        PsarcWriter.Write(output, [
            new PsarcWriteEntry("appid.appid", "248750"u8.ToArray()),
            new PsarcWriteEntry("songs/bin/generic/pin_lead.sng", big)
        ], encryptToc);
        return output.ToArray();
    }

    private static byte[] Write(Action<Stream> write)
    {
        using var output = new MemoryStream();
        write(output);
        return output.ToArray();
    }
}
