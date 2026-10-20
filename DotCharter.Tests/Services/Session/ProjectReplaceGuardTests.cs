using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     Opening a project REPLACES the session — <c>ApplyProject(resetHistory:
///     true)</c> clears the undo history, so the previous project's unsaved edits
///     become unreachable the instant it runs. Until now only the window-CLOSE
///     path consulted <c>IsProjectDirty</c>; every replace path went straight
///     through.
///     <para>
///         File → Open is the worst of them, because it also calls
///         <c>NotifyProjectSwitched</c>, which DELETES the previous project's
///         crash snapshot — so unlike the other replace paths there is not even a
///         recovery copy left behind.
///     </para>
///     <para>
///         These drive the REAL predicate
///         (<c>ProjectSessionService.ShouldProceedAsync</c>), not a copy of it —
///         a re-implemented rule in a test proves only that the test agrees with
///         itself.
///     </para>
/// </summary>
public sealed class ProjectReplaceGuardTests
{
    [Fact]
    public async Task ACleanSessionIsNeverPrompted()
    {
        var asked = false;

        var proceed = await ProjectSessionService.ShouldProceedAsync(
            isDirty: false,
            confirm: () => { asked = true; return Task.FromResult(true); });

        Assert.True(proceed);
        Assert.False(asked, "a clean session must not interrupt the user");
    }

    [Fact]
    public async Task ADirtySessionAsksAndCancelBlocksTheReplace()
    {
        Assert.False(await ProjectSessionService.ShouldProceedAsync(
            isDirty: true, confirm: () => Task.FromResult(false)));
    }

    [Fact]
    public async Task ADirtySessionProceedsWhenTheUserSaysSo()
    {
        Assert.True(await ProjectSessionService.ShouldProceedAsync(
            isDirty: true, confirm: () => Task.FromResult(true)));
    }

    /// <summary>
    ///     A host with no prompt wired — tests, headless — must behave exactly as
    ///     it did before rather than silently refusing every open.
    /// </summary>
    [Fact]
    public async Task WithNoPromptWiredEverythingProceeds() => Assert.True(await ProjectSessionService.ShouldProceedAsync(isDirty: true, confirm: null));
}
