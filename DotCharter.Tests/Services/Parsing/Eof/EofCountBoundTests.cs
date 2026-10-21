using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     The note and section counts of a Pro Guitar track are read straight off
///     the file. They are bounded by the bytes that remain, at the smallest size
///     one record can take - the same rule the beat reader applies - so a
///     corrupt count fails as a corrupt file, immediately, instead of when the
///     stream finally runs dry.
/// </summary>
public sealed class EofCountBoundTests
{
    private const int MinNoteBytes = 19;

    /// <summary>numFrets, numStrings = 6 and six tuning offsets.</summary>
    private static List<byte> TrackHeader()
    {
        var bytes = new List<byte> { 24, 6 };
        bytes.AddRange(new byte[6]);
        return bytes;
    }

    private static void U32(List<byte> bytes, uint value) => bytes.AddRange(BitConverter.GetBytes(value));

    private static void U16(List<byte> bytes, ushort value) => bytes.AddRange(BitConverter.GetBytes(value));

    private static Arrangement Parse(List<byte> bytes) =>
        EofProGuitarTrackParser.Parse(new EofPackReader(bytes.ToArray()), new EofTrackInfo { Id = 1, Name = "Lead" }, []);

    [Fact]
    public void ANoteCountLargerThanTheRemainingBytesCouldHold_FailsAsACorruptFile()
    {
        var bytes = TrackHeader();
        U32(bytes, 0xFFFFFFF0); // note count; nothing follows

        var ex = Assert.Throws<InvalidDataException>(() => Parse(bytes));

        Assert.Contains("notes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoteCountOneRecordPastTheRemainingBytes_FailsAsACorruptFile()
    {
        var bytes = TrackHeader();
        U32(bytes, 3);
        bytes.AddRange(new byte[(2 * MinNoteBytes) + 8]); // room for two minimal notes plus change

        Assert.Throws<InvalidDataException>(() => Parse(bytes));
    }

    [Fact]
    public void ASectionInstanceCountLargerThanTheRemainingBytesCouldHold_FailsAsACorruptFile()
    {
        var bytes = TrackHeader();
        U32(bytes, 0); // no notes
        U16(bytes, 1); // one section type
        U16(bytes, EofConstants.SectionFretHandPos);
        U32(bytes, 0xFFFFFF00); // instances; nothing follows

        var ex = Assert.Throws<InvalidDataException>(() => Parse(bytes));

        Assert.Contains("sections", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryTrack_IsUntouchedByTheBound()
    {
        var bytes = TrackHeader();
        U32(bytes, 2); // two minimal notes, exactly as many as fit
        for (var i = 0; i < 2; i++) bytes.AddRange(new byte[MinNoteBytes]);
        U16(bytes, 0); // no section types
        U32(bytes, 0); // no custom blocks

        var arrangement = Parse(bytes);

        Assert.Equal(6, arrangement.StringCount);
    }
}
