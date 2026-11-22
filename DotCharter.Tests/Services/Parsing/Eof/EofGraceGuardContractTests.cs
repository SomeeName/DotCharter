using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofGraceGuardContractTests
{
    public static TheoryData<string> Cases => [.. EofGraceGuardCases.Names];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_rejection_guard_prevents_an_unproven_grace_fold(string name)
    {
        var imported = EofGraceGuardCases.Import(EofGraceGuardCases.Track(EofGraceGuardCases.Create(name)), EofGraceGuardCases.Beats());
        Assert.False(EofGraceGuardCases.HasGrace(imported), name);
        Assert.DoesNotContain(imported.ChordTemplates, t => t.Name == "[DotCharter grace]");
        if (name is "reserved chord" or "reserved Nop chord" or "reserved spaced chord")
        {
            var project = EofGraceChordCases.NamedProject(EofGraceGuardCases.Create(name).Notes[1].Name);
            var target = EofNoteBuilder.Build(project.Arrangements[0], project.Beats).Single(n => n.Raw.PositionMs == 675);
            Assert.Empty(target.Raw.Name);
        }
    }
}
