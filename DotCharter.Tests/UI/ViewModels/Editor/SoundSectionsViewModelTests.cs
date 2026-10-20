using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #601: the Sound tab's rail, pure — which sections each selection type offers, which one is
///     open, what the ONE pinned "Apply to" row governs and that a chord has no Playback section.
///     The headless half (the real view at the default pane size, the chips, the picker, the edits)
///     is <c>Perf/SoundTabRailUiPerfSmokeTests</c>.
/// </summary>
public sealed class SoundSectionsViewModelTests
{
    public static TheoryData<SoundSelectionKind, SoundSection[]> Rails => new()
    {
        { SoundSelectionKind.Empty, [SoundSection.Task, SoundSection.Library] },
        { SoundSelectionKind.Note, [SoundSection.Placement, SoundSection.Technique, SoundSection.Bends, SoundSection.Findings] },
        { SoundSelectionKind.Chord, [SoundSection.Shape, SoundSection.Technique, SoundSection.Bends, SoundSection.Findings] },
        { SoundSelectionKind.HandShape, [SoundSection.Shape, SoundSection.Findings] },
        { SoundSelectionKind.ShapeAndSounds, [SoundSection.Shape, SoundSection.Technique, SoundSection.Findings] },
        { SoundSelectionKind.MultiSame, [SoundSection.Selection, SoundSection.Technique, SoundSection.Findings] },
        { SoundSelectionKind.MultiMixed, [SoundSection.Selection, SoundSection.Technique, SoundSection.Findings] }
    };

    [Theory]
    [MemberData(nameof(Rails))]
    public void EachSelectionType_OffersItsSections_AndOpensOnTheFirst(SoundSelectionKind kind, SoundSection[] expected)
    {
        var vm = new SoundSectionsViewModel { Kind = kind };

        Assert.Equal(expected, vm.Sections);
        Assert.Equal(expected[0], vm.Section);
        foreach (var section in Enum.GetValues<SoundSection>())
        {
            Assert.Equal(expected.Contains(section), Offers(vm, section));
            Assert.Equal(section == expected[0], Is(vm, section));
        }
    }

    /// <summary>Playback was folded into Technique (#601): there is no such section, for any selection.</summary>
    [Fact]
    public void NoSelection_HasAPlaybackSection() =>
        Assert.DoesNotContain(Enum.GetNames<SoundSection>(), n => n.Contains("Playback", StringComparison.Ordinal));

    /// <summary>The ONE pinned "Apply to" row: over a chord's Shape, Technique and Bends (#604), a
    /// hand shape's Shape, and nothing else — an edit is never broader than the bar above it. A
    /// note's Bends has none: its curve is the note's alone.</summary>
    [Theory]
    [InlineData(SoundSelectionKind.Chord, SoundSection.Shape, true)]
    [InlineData(SoundSelectionKind.Chord, SoundSection.Technique, true)]
    [InlineData(SoundSelectionKind.Chord, SoundSection.Bends, true)]
    [InlineData(SoundSelectionKind.Chord, SoundSection.Findings, false)]
    [InlineData(SoundSelectionKind.Note, SoundSection.Bends, false)]
    [InlineData(SoundSelectionKind.HandShape, SoundSection.Shape, true)]
    [InlineData(SoundSelectionKind.ShapeAndSounds, SoundSection.Technique, true)]
    [InlineData(SoundSelectionKind.Note, SoundSection.Technique, false)]
    [InlineData(SoundSelectionKind.MultiSame, SoundSection.Technique, false)]
    [InlineData(SoundSelectionKind.Empty, SoundSection.Library, false)]
    public void TheApplyRow_PinsOverTheSectionsItGoverns(SoundSelectionKind kind, SoundSection section, bool pinned)
    {
        var vm = new SoundSectionsViewModel { Kind = kind };
        vm.ChooseCommand.Execute(section);

        Assert.Equal(section, vm.Section);
        Assert.Equal(pinned, vm.ShowApplyBar);
        Assert.Equal(pinned, SoundSectionsViewModel.ShowsApplyBar(kind, section));
    }

    /// <summary>#581: the pinned ladder counts and previews the techniques' reach over a chord's
    /// Technique section and (#604, a bend is written like a technique) its Bends, the shape's
    /// everywhere else; and a chord's technique edits are reached
    /// by the ladder at all only while a chord is in the selection.</summary>
    [Theory]
    [InlineData(SoundSelectionKind.Chord, SoundSection.Technique, ChordApplyFamily.Technique, true)]
    [InlineData(SoundSelectionKind.Chord, SoundSection.Shape, ChordApplyFamily.Shape, true)]
    [InlineData(SoundSelectionKind.Chord, SoundSection.Bends, ChordApplyFamily.Technique, true)]
    [InlineData(SoundSelectionKind.Note, SoundSection.Bends, ChordApplyFamily.Shape, false)]
    [InlineData(SoundSelectionKind.ShapeAndSounds, SoundSection.Technique, ChordApplyFamily.Technique, true)]
    [InlineData(SoundSelectionKind.Note, SoundSection.Technique, ChordApplyFamily.Shape, false)]
    [InlineData(SoundSelectionKind.MultiMixed, SoundSection.Technique, ChordApplyFamily.Shape, false)]
    public void TheLadder_FollowsTheOpenSection(SoundSelectionKind kind, SoundSection section, ChordApplyFamily family, bool chordScope)
    {
        var vm = new SoundSectionsViewModel { Kind = kind };
        vm.ChooseCommand.Execute(section);

        Assert.Equal(family, vm.ApplyFamily);
        Assert.Equal(chordScope, vm.HasChordScope);
        Assert.Equal(chordScope && section == SoundSection.Technique, vm.ShowChordTechnique);
        Assert.Equal(!chordScope && section == SoundSection.Technique, vm.ShowNoteTechnique);
    }

    /// <summary>The open section is remembered PER SELECTION TYPE: a chord stays on Technique across
    /// a detour through a note (which opens on its own default), and a section a type does not offer
    /// falls back to that type's first.</summary>
    [Fact]
    public void TheOpenSection_IsRememberedPerSelectionType()
    {
        var vm = new SoundSectionsViewModel { Kind = SoundSelectionKind.Chord };
        vm.ChooseCommand.Execute(SoundSection.Technique);

        vm.Kind = SoundSelectionKind.Note;
        Assert.Equal(SoundSection.Placement, vm.Section);
        vm.ChooseCommand.Execute(SoundSection.Bends);

        vm.Kind = SoundSelectionKind.Chord;
        Assert.Equal(SoundSection.Technique, vm.Section);
        vm.Kind = SoundSelectionKind.Note;
        Assert.Equal(SoundSection.Bends, vm.Section);

        vm.Kind = SoundSelectionKind.HandShape;
        vm.ChooseCommand.Execute(SoundSection.Technique);
        Assert.Equal(SoundSection.Shape, vm.Section);
    }

    /// <summary>A click on the lit entry unchecks it locally; the command re-announces the flags so
    /// its OneWay binding re-reads and the entry stays lit.</summary>
    [Fact]
    public void ChoosingTheOpenSectionAgain_ReannouncesIt()
    {
        var vm = new SoundSectionsViewModel { Kind = SoundSelectionKind.Chord };
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ChooseCommand.Execute(SoundSection.Shape);

        Assert.Contains(nameof(SoundSectionsViewModel.IsShape), raised);
    }

    /// <summary>The empty state's task order: fingerings, then names, then export blockers.</summary>
    [Theory]
    [InlineData(3, 2, 9, SoundQueueFilter.MissingFingering)]
    [InlineData(0, 2, 9, SoundQueueFilter.UnnamedChord)]
    [InlineData(0, 0, 9, SoundQueueFilter.BlocksExport)]
    [InlineData(0, 0, 0, null)]
    public void TheNextTask_ComesInTheShippedOrder(int fingerings, int unnamed, int blockers, SoundQueueFilter? expected) =>
        Assert.Equal(expected, SoundNextTaskViewModel.NextTaskFor(fingerings, unnamed, blockers));

    private static bool Offers(SoundSectionsViewModel vm, SoundSection section) => section switch
    {
        SoundSection.Task => vm.OffersTask,
        SoundSection.Library => vm.OffersLibrary,
        SoundSection.Placement => vm.OffersPlacement,
        SoundSection.Shape => vm.OffersShape,
        SoundSection.Selection => vm.OffersSelection,
        SoundSection.Technique => vm.OffersTechnique,
        SoundSection.Bends => vm.OffersBends,
        _ => vm.OffersFindings
    };

    private static bool Is(SoundSectionsViewModel vm, SoundSection section) => section switch
    {
        SoundSection.Task => vm.IsTask,
        SoundSection.Library => vm.IsLibrary,
        SoundSection.Placement => vm.IsPlacement,
        SoundSection.Shape => vm.IsShape,
        SoundSection.Selection => vm.IsSelection,
        SoundSection.Technique => vm.IsTechnique,
        SoundSection.Bends => vm.IsBends,
        _ => vm.IsFindings
    };
}
